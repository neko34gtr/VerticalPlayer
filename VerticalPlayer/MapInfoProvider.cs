using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 地図情報通知オーバーレイのデータ供給元。
    ///
    /// 【通信タイミング】LoadRouteAsync()（ファイル/連結ルートを開いた直後に1回だけ呼ぶ）でのみ
    /// Overpass APIへ通信する。UpdateFrame()（毎フレーム呼ばれる）は完全にローカル計算のみで、
    /// 一切ネットワークへアクセスしない（地名のNominatimフォールバックのみ例外。後述）。
    ///
    /// 【地名判定】OverpassでルートのバウンディングボックスごとOSMのplaceノードを取得し、
    /// 現在地に最も近いものをローカルで採用する。近傍にplaceノードが無い区間（山間部・郊外の
    /// 過疎地等でOSMのタグ付けが粗い場合）だけ、フォールバックとしてNominatim逆ジオコーディングを
    /// 1回だけ呼ぶ（結果は座標を丸めたグリッド単位でキャッシュし、同じ付近を再度通っても
    /// 再度は呼ばない）。
    ///
    /// 【残り距離の基準】ルートの走行軌跡を折れ線として繋ぎ、始点からの累積距離(km)を軸とする。
    /// 各トンネル/SA-PAの位置もこの軸へ最近傍点として投影し、現在位置より軸上で先（＝cumKmが
    /// 大きい）にあるものだけを「前方」として扱う。コンパス方位(Heading)は「前方」判定には
    /// 使わない（進行方向は軸の増加方向で決まるため不要）。Headingは、ジャンクション等で
    /// 近接して並ぶ道路名タグの中からどちらの道路を走っているかを絞り込む用途にのみ使う。
    ///
    /// 【Overpass結果のキャッシュとcumKmの関係】RouteCacheにはOverpassの生の地理座標のみを
    /// キャッシュする（トンネル/SA-PAの「ルート上の累積距離」はキャッシュしない）。同じ
    /// バウンディングボックスでも、連結するファイルの組み合わせが違えばルート折れ線の始点・
    /// 形状は毎回変わり得るため、cumKmへの投影はLoadRouteAsyncのたびにこのインスタンス上で
    /// 必ずやり直す（ProjectBundleOntoRoute）。
    ///
    /// 【トンネル通過中の扱い】接近中(DistanceToTunnelMeters が閾値未満)の状態から
    /// HasGpsFix が true→false に切り替わった場合、「トンネル進入によるGPS遮断」とみなし、
    /// 該当トンネルの情報を保持したまま「通過中」表示に固定する（DistanceToTunnelMeters=0を
    /// 通過中の合図として使う。MapInfoOverlayControl側で0以下なら「通過中」表記に切り替える）。
    /// それ以外のGPSロスト（地下駐車場・山間部遮蔽等、接近中の予兆が無いまま切れた場合）は
    /// オーバーレイごと非表示にする（直前値は保持するがShouldShowOverlay=falseにする）。
    /// </summary>
    public sealed class MapInfoProvider
    {
        // ── 定数 ──

        private const double TunnelApproachThresholdMeters = 1000.0; // 「接近中」とみなす距離
        private const double TunnelEntryDetectionThresholdKm = 3.0; // GPSロスト時の「トンネル進入」検出に使う広めの閾値 4.0→3.0km 80キロで2分走ると仮定して2.8キロなので3とした
        private const double SaPaApproachThresholdKm = 30.0;         // SA/PA案内を出し始める手前距離 30→5km
        private const double PlaceMatchRadiusKm = 6.0;               // Overpassのplaceノードを採用する最大距離
        private const double MinBearingSampleMeters = 8.0;           // Heading再計算に使う最小移動量
        private const double HeadingLockSpeedKmh = 3.0;              // これ未満の速度ではHeadingをロック
        private const double BboxPaddingDeg = 0.03;                  // 約3km相当の余白（トンネル入口が測位断続範囲の外に出るのを防ぐ）
        // seek判定用。VideoOffsetは連結ルートのローカル座標で、ファイル切替のたびに0起点で
        // 作り直されるため比較に使えない（切替直後を「シークした」と誤判定してしまう）。
        // Timestampは絶対時刻でファイル切替をまたいでも連続なので、こちらを使う。
        // ファイル境界のわずかな録画ギャップも許容できるよう少し余裕を持たせている。
        private static readonly TimeSpan FrameContinuityThreshold = TimeSpan.FromSeconds(5);

        private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // ルートのバウンディングボックス単位でOverpassの生データをプロセス内キャッシュ（再訪時の再通信を避ける）
        private static readonly ConcurrentDictionary<string, OverpassBundle> RouteCache = new();
        // Nominatim逆ジオコーディングの結果キャッシュ（緯度経度を約1km格子に丸めたキー）
        private static readonly ConcurrentDictionary<string, string> ReverseGeocodeCache = new();

        static MapInfoProvider()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("VerticalPlayer-Dashcam/1.0 (personal dashcam review tool)");
        }

        // ── ルート（走行軌跡の折れ線・累積距離） ──

        private readonly record struct RoutePoint(double CumKm, double Lat, double Lng);

        private List<RoutePoint> _route = new();
        private OverpassBundle _bundle = OverpassBundle.Empty;
        private List<ProjectedTunnel> _projectedTunnels = new();
        private List<ProjectedSaPa> _projectedSaPas = new();
        private bool _routeReady; // LoadRouteAsyncが（成功/失敗問わず）一度完了したか

        // ── Heading（進行方位）の直前値ロック ──

        private double? _lastHeadingDeg;
        private DashcamSensorFrame? _prevFixFrame;

        // ── トンネル通過中の状態保持 ──

        private ProjectedTunnel? _passingTunnel; // 「今ロック中（接近中〜通過中）のトンネル」として厳密に扱います
        private bool _isInsideTunnelInstance;    // 実際にGPSロスト等で「通過中」フラグが立ったかどうかの内部管理用
        private DashcamSensorFrame? _lastFrame;
        private double _lastCumKm; // 「情報一覧」の通知情報タブ表示用（GetDebugSnapshot参照）

        /// <summary>常に同じインスタンスを使い回す（毎フレームのGC割り当てを避けるため）。
        /// UpdateFrame()の戻り値ではなく、このプロパティを都度読み直して使うこと。</summary>
        public MapInfoState State { get; } = new();

        /// <summary>オーバーレイ全体を表示すべきか。falseの間はStateの中身を見ずに
        /// Collapsedにしてよい（直前の値は保持されるが表示はしない）。</summary>
        public bool ShouldShowOverlay { get; private set; }

        /// <summary>現在、高速道路(motorway)本線上を走行中とみなしているか。UpdateHighwayName内で
        /// 更新される。下道走行時に高速道路専用トンネルを通知対象から除外する判定や、
        /// 「情報一覧」の内部データ確認タブで使う。GPSロスト中（トンネル通過中含む）は
        /// 直前の値を保持する。</summary>
        public bool IsOnExpressway { get; private set; }

        /// <summary>「情報一覧」ウィンドウの通知情報タブに出す、内部で保持・計算しているデータの
        /// スナップショットを返す。呼び出し自体は読み取り専用でUpdateFrame等の動作に影響しない。
        /// ウィンドウが開いている間だけ、DashcamPlayerView側が定期的にこれを呼んで表示を更新する想定。</summary>
        public MapInfoDebugSnapshot GetDebugSnapshot()
        {
            var nearTunnels = _projectedTunnels
                .Select(t => new MapInfoDebugTunnelRow
                {
                    Name = string.IsNullOrEmpty(t.Raw.Name) ? "(無名)" : t.Raw.Name,
                    RoadName = t.RoadName,
                    IsMotorwayTunnel = t.Raw.IsMotorwayTunnel,
                    LengthMeters = t.Raw.LengthMeters,
                    EntryCumKm = t.EntryCumKm,
                    DistanceAheadKm = t.EntryCumKm - _lastCumKm,
                    IsPassing = ReferenceEquals(t, _passingTunnel),
                })
                .OrderBy(r => Math.Abs(r.DistanceAheadKm))
                .Take(20)
                .ToList();

            var nearSaPas = _projectedSaPas
                .Select(s => new MapInfoDebugSaPaRow
                {
                    Name = s.Raw.Name,
                    Type = s.Raw.Type,
                    RoadName = s.RoadName,
                    CumKm = s.CumKm,
                    DistanceAheadKm = s.CumKm - _lastCumKm,
                })
                .OrderBy(r => Math.Abs(r.DistanceAheadKm))
                .Take(20)
                .ToList();

            return new MapInfoDebugSnapshot
            {
                RouteReady = _routeReady,
                RoutePointCount = _route.Count,
                TunnelCount = _bundle.Tunnels.Count,
                SaPaCount = _bundle.SaPas.Count,
                PlaceCount = _bundle.Places.Count,
                HighwayWayCount = _bundle.Highways.Count,
                HasLastFrame = _lastFrame != null,
                CurrentLat = _lastFrame?.Latitude ?? 0,
                CurrentLng = _lastFrame?.Longitude ?? 0,
                HasGpsFix = _lastFrame?.HasGpsFix ?? false,
                CurrentCumKm = _lastCumKm,
                HeadingDeg = _lastHeadingDeg,
                IsOnExpressway = IsOnExpressway,
                IsPassingTunnel = _passingTunnel != null,
                ShouldShowOverlay = ShouldShowOverlay,
                HighwayName = State.HighwayName,
                CurrentLocationName = State.CurrentLocationName,
                HasNextSaPa = State.HasNextSaPa,
                NextSaPaName = State.NextSaPaName,
                NextSaPaType = State.NextSaPaType,
                NextSaPaDistanceKm = State.NextSaPaDistanceKm,
                HasUpcomingTunnel = State.HasUpcomingTunnel,
                NextTunnelName = State.NextTunnelName,
                NextTunnelLengthMeters = State.NextTunnelLengthMeters,
                DistanceToTunnelMeters = State.DistanceToTunnelMeters,
                NearbyTunnels = nearTunnels,
                NearbySaPas = nearSaPas,
            };
        }

        /// <summary>再生停止・全く別のドライブを開いた時など、明確に非連続なタイミングでのみ呼ぶ。
        /// トンネル通過中フラグやHeadingロックなど「今まさに継続している状態」をクリアする。
        /// 通常のシーケンシャル再生中は前後窓の連結範囲がファイル境界ごとに動くたびLoadRouteAsyncが
        /// 呼ばれるが、それだけでは呼ばない（呼んでしまうと、トンネル内でちょうどファイルが
        /// 切り替わった瞬間に「通過中」表示が消えてしまっていた）。</summary>
        public void Reset()
        {
            _passingTunnel = null;
            _prevFixFrame = null;
            _lastHeadingDeg = null;
            _lastFrame = null;
            IsOnExpressway = false;
            ShouldShowOverlay = false;
        }

        /// <summary>起動時のレジューム復元専用。前回終了時点の「利用中」道路名・地名を、
        /// 実際のGPSフレームがまだ1枚も届いていない起動直後の段階で即座に表示へ反映する
        /// （でなければ、Overpassの再取得とルート再構築が終わるまでの間、毎回いったん
        /// 空の状態から始まってしまう）。ここで入れた値は、最初の有効なGPSフレームが
        /// UpdateFrame()に届いた時点で、通常どおりライブ判定の結果に上書きされる
        /// （＝間違った値を復元してしまっても、実際の位置情報が届けばすぐ正しく直る）。
        /// トンネル通過中フラグ・次のSA/PA等の一時的な情報は復元しない（実データに基づく
        /// ものではなく起動直後は再計算されるまで確定できないため、意図的に対象外にしている）。</summary>
        public void ApplyResumedState(string highwayName, bool isOnExpressway, string locationName)
        {
            State.HighwayName = highwayName ?? string.Empty;
            IsOnExpressway = isOnExpressway;
            State.CurrentLocationName = locationName ?? string.Empty;
            ShouldShowOverlay = !string.IsNullOrEmpty(State.HighwayName) || !string.IsNullOrEmpty(State.CurrentLocationName);
        }

        /// <summary>ファイル（または前後を連結したルート）を開いた直後に呼ぶ。通常のシーケンシャル
        /// 再生では前後窓がスライドするたびに呼ばれるため、トンネル通過中フラグ等の継続的な状態は
        /// ここではクリアしない（クリアしたい場合はReset()を先に呼ぶこと。例: StopPlayback）。
        /// Overpassへの通信はこの中でのみ発生し、失敗・タイムアウトしても例外は投げない。
        ///
        /// 【重要】この処理の間、_routeReadyをfalseに戻すことはしない。以前はここでfalseにしていたため、
        /// GPSが生きている区間でもファイル切替のたび（前後窓の再構築・Overpass再取得中）は必ず
        /// UpdateFrame()がShouldShowOverlay=falseを返し、地図情報通知が毎回消えてしまっていた。
        /// 今は「今持っているroute/bundle/投影データをそのまま使い続け、新しいデータが揃った時だけ
        /// 差し替える」方式にしている。ルートが組めない窓（長いトンネルが複数ファイルにまたがって
        /// 続く場合、この窓に有効なGPS点が1つも無いことがある）や、Overpass通信の失敗時も、
        /// 直前まで持っていたデータをそのまま使い続ける（空にはしない）。</summary>
        public async Task LoadRouteAsync(IReadOnlyList<DashcamSensorFrame> mergedFrames, CancellationToken ct = default)
        {
            var newRoute = BuildRoutePolyline(mergedFrames);

            if (newRoute.Count < 2)
            {
                // この窓には有効なGPS点が無い（例: 長いトンネルの中だけで完結する窓）。
                // 今のroute/bundleを維持したまま何もしない。初回読み込みで一度もrouteが
                // 組めたことが無ければ_routeReadyはfalseのままで、UpdateFrameは非表示を返す。
                return;
            }

            double minLat = newRoute.Min(p => p.Lat) - BboxPaddingDeg;
            double maxLat = newRoute.Max(p => p.Lat) + BboxPaddingDeg;
            double minLng = newRoute.Min(p => p.Lng) - BboxPaddingDeg;
            double maxLng = newRoute.Max(p => p.Lng) + BboxPaddingDeg;
            string cacheKey = string.Create(CultureInfo.InvariantCulture,
                $"{minLat:F2},{minLng:F2},{maxLat:F2},{maxLng:F2}");

            OverpassBundle? newBundle = null;
            if (RouteCache.TryGetValue(cacheKey, out var cached))
            {
                newBundle = cached;
            }
            else
            {
                try
                {
                    newBundle = await FetchOverpassAsync(minLat, minLng, maxLat, maxLng, ct).ConfigureAwait(false);
                    RouteCache[cacheKey] = newBundle;
                }
                catch (Exception ex)
                {
                    // 【要件4】ネットワークエラー・タイムアウト時は例外を投げず、直前のbundleを
                    // そのまま使い続ける（ここでEmptyに差し替えると、通信が一時的に不安定なだけで
                    // 既に取得済みのトンネル/SA-PA情報まで失われてしまう）。
                    System.Diagnostics.Debug.WriteLine($"[MapInfoProvider] Overpass取得失敗（直前のデータを維持して続行）: {ex.Message}");
                }
            }

            // ここまで来て初めて、route（と、取得できていればbundle）を実際に差し替える。
            // 取得に失敗した場合はnewBundleがnullのままなので、_bundleは直前の値を保持する。
            _route = newRoute;
            if (newBundle != null) _bundle = newBundle;
            ProjectBundleOntoRoute();
            _routeReady = true;
        }

        /// <summary>_bundle（生の地理座標）から、現在のルート折れ線上の累積距離(km)を都度計算し直す。
        /// キャッシュされたOverpassの生データは使い回せても、ルート折れ線側は連結ファイルの
        /// 組み合わせによって毎回変わり得るため、この投影だけはLoadRouteAsyncのたびに必ず行う。</summary>
        private void ProjectBundleOntoRoute()
        {
            var validTunnels = new List<ProjectedTunnel>();
            foreach (var t in _bundle.Tunnels)
            {
                // ProjectToRouteの第2戻り値（d.DistKm）は「ルート折れ線からその点までの物理的な直線距離(km)」
                var projA = ProjectToRoute(t.EntryLat, t.EntryLng);
                var projB = ProjectToRoute(t.ExitLat, t.ExitLng);

                // ❗【絶対防壁】トンネルの入口または出口が、走行中のルートから「100m (0.1km)」以上
                // 離れている場合は、並走する下道や、広域BBOXに巻き込まれただけの無関係なトンネル（須原トンネル等）
                // と断定し、経路上の候補から【完全に除外】する。
                if (projA.DistKm > 0.1 || projB.DistKm > 0.1)
                {
                    continue;
                }

                double entryCum = Math.Min(projA.CumKm, projB.CumKm);
                double exitCum = Math.Max(projA.CumKm, projB.CumKm);

                double midLat = (t.EntryLat + t.ExitLat) / 2.0;
                double midLng = (t.EntryLng + t.ExitLng) / 2.0;
                string roadName = FindNearestHighwayName(midLat, midLng);

                validTunnels.Add(new ProjectedTunnel(t, entryCum, exitCum, roadName));
            }
            _projectedTunnels = validTunnels;

            // ── SA/PA 側も同様に、ルートから 800m 以上離れている無関係な施設を排除 ──
            // ❗【修正点】ひるが野高原SAや飛騨白川PAのように、地形の制約で本線から大きく奥まった場所に
            // 配置されている主要な施設がフィルターで誤って消滅してしまうのを防ぐため、
            // 距離判定の防壁を 300m から 「800m (0.8km)」 へと安全に拡張します。
            var validSaPas = new List<ProjectedSaPa>();
            foreach (var s in _bundle.SaPas)
            {
                var proj = ProjectToRoute(s.Lat, s.Lng);

                if (proj.DistKm > 0.8) // 0.3 から 0.8 へ拡張
                {
                    continue; // ルートから完全に遠い、無関係な一般道の道の駅などを排除
                }

                string roadName = FindNearestHighwayName(s.Lat, s.Lng);
                validSaPas.Add(new ProjectedSaPa(s, proj.CumKm, roadName));
            }
            _projectedSaPas = validSaPas;
        }

        /// <summary>毎フレーム呼ぶ。ネットワーク通信は行わない（地名のNominatimフォールバックのみ、
        /// 見つからなかった場合に限り1回だけ非同期で裏側から発火する。呼び出し元をブロックしない）。
        /// frameがnull（再生停止・ファイル未選択）の場合はオーバーレイを非表示にする。</summary>
        public void UpdateFrame(DashcamSensorFrame? frame)
        {
            if (frame == null || !_routeReady || _route.Count < 2)
            {
                _lastFrame = frame;
                ShouldShowOverlay = false;
                return;
            }

            // 前フレームからの連続性チェック（シーク直後の大ジャンプでは「接近中→通過中」の
            // 引き継ぎ判定を行わない。無関係な地点の接近フラグを誤って引き継がないため）。
            bool continuous = _lastFrame != null &&
                (frame.Timestamp - _lastFrame.Timestamp).Duration() <= FrameContinuityThreshold;

            if (!frame.HasGpsFix)
            {
                if (continuous && _passingTunnel != null)
                {
                    // 既に通過中状態を保持している（トンネル内を引き続き走行中）→そのまま維持
                    ApplyPassingTunnelState();
                    _lastFrame = frame;
                    return;
                }
                if (continuous && _lastFrame != null && _lastFrame.HasGpsFix &&
                    TryFindTunnelNear(_lastFrame, out var enteredTunnel))
                {
                    // 【要件5】GPSロスト直前位置のすぐ先にトンネルがある→進入とみなし、通過中表示へ切り替える。
                    // 「接近中(1km以内)」表示が出ていたかどうかは問わない（山間部ではトンネル手前で
                    // GPS感度が落ち、1km以内の測位が1点も取れないままロストすることが多いため、
                    // 接近中バナーの有無とは切り離して判定する。閾値はTryFindTunnelNear内で別管理）。
                    _passingTunnel = enteredTunnel;
                    ApplyPassingTunnelState();
                    _lastFrame = frame;
                    return;
                }

                // 【要件5】それ以外のGPSロスト（地下駐車場・山間部遮蔽等）・Rear単体再生時は非表示
                ShouldShowOverlay = false;
                _lastFrame = frame;
                return;
            }

            // ── ここから測位あり ──
            _passingTunnel = null; // GPS復帰＝トンネル通過完了とみなし、通過中状態を解除する
            UpdateHeading(frame);

            double cumKm = ProjectToRoute(frame.Latitude, frame.Longitude).CumKm;
            _lastCumKm = cumKm;

            UpdateLocationName(frame);
            UpdateHighwayName(frame);
            UpdateTunnelState(cumKm); // トンネル接近時はここでHighwayNameを上書きする（並走道路の誤検出対策）
            UpdateSaPaState(cumKm);

            ShouldShowOverlay = State.HasNextSaPa || State.HasUpcomingTunnel
                || !string.IsNullOrEmpty(State.CurrentLocationName) || !string.IsNullOrEmpty(State.HighwayName);
            _lastFrame = frame;
        }

        private void ApplyPassingTunnelState()
        {
            if (_passingTunnel == null) { ShouldShowOverlay = false; return; }
            State.HasUpcomingTunnel = true;
            State.NextTunnelName = _passingTunnel.Raw.Name;
            State.NextTunnelLengthMeters = _passingTunnel.Raw.LengthMeters;
            State.DistanceToTunnelMeters = 0; // 0＝MapInfoOverlayControl側で「通過中」表記に切り替える合図
            if (!string.IsNullOrEmpty(_passingTunnel.RoadName))
                State.HighwayName = _passingTunnel.RoadName; // GPS喪失中でも「利用中」表示だけは維持する
            // SA/PA・地名はGPSが無いため更新できない。直前値をそのまま保持して表示を続ける。
            ShouldShowOverlay = true;
        }

        private bool TryFindTunnelNear(DashcamSensorFrame lastFixFrame, out ProjectedTunnel? tunnel)
        {
            double cumKm = ProjectToRoute(lastFixFrame.Latitude, lastFixFrame.Longitude).CumKm;
            // 「接近中」表示の閾値(1km)とは別枠。山間部トンネルは入口の数km手前からGPS感度が
            // 落ち始め、1km以内の測位点が1つも取れないままロストすることが多いため、
            // 進入検出だけはこちらの広い閾値で判定する。
            // ❗【バグ修正】3km先まで探してしまうと連続トンネルで1本先を誤ロックするため、
            // GPSロスト直前の足元「500m (0.5km) 以内」にある直近のトンネルだけに厳密に限定します。
            tunnel = FilterTunnelCandidatesForCurrentRoad(_projectedTunnels)
                .Where(t => t.EntryCumKm >= cumKm - 0.05 && t.EntryCumKm - cumKm <= 0.5) // 3.0 から 0.5 へ修正
                .OrderBy(t => t.EntryCumKm)
                .FirstOrDefault();
            return tunnel != null;
        }

        /// <summary>【要件5】下道(一般道)走行中は、高速道路専用のトンネル(TunnelCandidate.IsMotorwayTunnel)を
        /// 通知対象から除外する。さらに、現在走行中の一般道路線名(State.HighwayName)が分かっている場合は、
        /// その道路線上のトンネルだけに厳密に限定する（名前が取れず判定できない場合のみ、
        /// IsMotorwayTunnel=falseという緩い条件まで許容する）。高速道路走行中はこのフィルタを適用しない
        /// （motorway/trunk双方のトンネルを候補として扱う。ジャンクション付近の一時的な誤判定を過度に
        /// 締め出さないため）。</summary>
        private IEnumerable<ProjectedTunnel> FilterTunnelCandidatesForCurrentRoad(IEnumerable<ProjectedTunnel> source)
        {
            if (string.IsNullOrEmpty(State.HighwayName))
                return Enumerable.Empty<ProjectedTunnel>();

            if (IsOnExpressway)
            {
                // 【高速道路・都市高速を走行中】
                return source.Where(t =>
                    // A. トンネル側の道路名が空欄なら、経路上にあると信じて救済
                    string.IsNullOrEmpty(t.RoadName) ||
                    // B. 「名古屋高速」などのキーワードが相互に部分一致すればOK
                    t.RoadName.Contains(State.HighwayName) ||
                    State.HighwayName.Contains(t.RoadName) ||
                    // C. 走行中の名前に「高速」が入っていれば、トンネル側が IsMotorwayTunnel であれば救済
                    (State.HighwayName.Contains("高速") && t.Raw.IsMotorwayTunnel)
                );
            }
            else
            {
                // 【一般道（下道）走行時】
                // 高速道路専用トンネルを確実に除外し、下道名が部分一致するもの
                return source.Where(t => !t.Raw.IsMotorwayTunnel &&
                                        !string.IsNullOrEmpty(t.RoadName) &&
                                        (t.RoadName.Contains(State.HighwayName) || State.HighwayName.Contains(t.RoadName)));
            }
        }

        // ── 各要素の更新 ──

        private void UpdateLocationName(DashcamSensorFrame frame)
        {
            var nearest = _bundle.Places
                .Select(p => (Place: p, DistKm: HaversineKm(frame.Latitude, frame.Longitude, p.Lat, p.Lng)))
                .Where(x => x.DistKm <= PlaceMatchRadiusKm)
                .OrderBy(x => x.DistKm)
                .Select(x => x.Place)
                .FirstOrDefault();

            if (nearest != null)
            {
                State.CurrentLocationName = nearest.Name + "付近";
                return;
            }

            // 近傍にOverpassのplaceノードが無い区間だけ、Nominatimへ1回フォールバックする
            // （結果が出るまでは直前の地名表示を維持し、非同期で届いたら次フレーム以降に反映される）
            string gridKey = string.Create(CultureInfo.InvariantCulture,
                $"{Math.Round(frame.Latitude, 2):F2},{Math.Round(frame.Longitude, 2):F2}");
            if (ReverseGeocodeCache.TryGetValue(gridKey, out var cachedName))
            {
                if (!string.IsNullOrEmpty(cachedName))
                    State.CurrentLocationName = cachedName;
                return;
            }

            ReverseGeocodeCache[gridKey] = string.Empty; // 二重発火防止のプレースホルダ
            double lat = frame.Latitude, lng = frame.Longitude;
            _ = Task.Run(async () =>
            {
                try
                {
                    string name = await ReverseGeocodeAsync(lat, lng).ConfigureAwait(false);
                    ReverseGeocodeCache[gridKey] = name;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MapInfoProvider] Nominatim逆ジオコーディング失敗: {ex.Message}");
                }
            });
        }

        // ── UpdateTunnelState 全面差し替え ──
        private void UpdateTunnelState(double cumKm)
        {
            // ❗【シンプルイズベスト刷新】
            // 短いトンネルの連続区間やジッタによる誤判定を防ぐため、複雑な常時ロック機構や
            // GPSのFix条件によるパージ判定をすべて撤廃。ジッタっぽい動きは完全に無視します。
            // 毎フレーム、車の累積距離(cumKm)を基準にして、現在地が「手前1km以内(接近中)」または
            // 「入口から出口の間(通過中)」に物理的に合致する直近のトンネルをクエリから1本だけ素直に選び直します。

            var currentOrNextTunnel = FilterTunnelCandidatesForCurrentRoad(_projectedTunnels)
                .Where(t =>
                    // ケース1: まだ入口の手前にいる（接近中）
                    (t.EntryCumKm > cumKm - 0.05) ||
                    // ケース2: 物理的に入口と出口の間にいる（GPSがロストしていても、累積距離の軸上で内側にあれば通過中とみなす）
                    (cumKm >= t.EntryCumKm - 0.05 && cumKm <= t.ExitCumKm + 0.05)
                )
                .OrderBy(t => t.EntryCumKm)
                .FirstOrDefault();

            // 3. 対象となるトンネルが前方（または足元）に無いなら非表示にして終了
            if (currentOrNextTunnel == null)
            {
                _passingTunnel = null;
                State.HasUpcomingTunnel = false;
                return;
            }

            // フィールドの _passingTunnel も最新の状態に同期
            _passingTunnel = currentOrNextTunnel;

            // 4. ロック中トンネルとの手前までの距離を計算
            double distM = (_passingTunnel.EntryCumKm - cumKm) * 1000.0;

            // 5. すでに車がトンネルの入り口を通過している（またはまさに足元にある）場合
            if (distM <= 0 || (cumKm >= _passingTunnel.EntryCumKm && cumKm <= _passingTunnel.ExitCumKm))
            {
                State.HasUpcomingTunnel = true;
                State.NextTunnelName = _passingTunnel.Raw.Name;
                State.NextTunnelLengthMeters = _passingTunnel.Raw.LengthMeters;
                State.DistanceToTunnelMeters = 0; // 0 = 通過中フラグ
                return;
            }

            // 6. まだ入口の手前にいる場合（接近中判定）
            if (distM > TunnelApproachThresholdMeters)
            {
                // 1km以上手前なら、バックグラウンドでロック（予約）は保持するが画面にはまだ出さない
                State.HasUpcomingTunnel = false;
                return;
            }

            // 1km以内の接近中画面を表示
            State.HasUpcomingTunnel = true;
            State.NextTunnelName = _passingTunnel.Raw.Name;
            State.NextTunnelLengthMeters = _passingTunnel.Raw.LengthMeters;
            State.DistanceToTunnelMeters = Math.Max(1, (int)Math.Round(distM));
        }

        private void UpdateSaPaState(double cumKm)
        {
            if (!IsOnExpressway || string.IsNullOrEmpty(State.HighwayName))
            {
                State.HasNextSaPa = false;
                return;
            }

            // 部分一致、またはSA/PA側の所属道路名が空欄の場合も救済して対象にする
            var next = _projectedSaPas
                .Where(s => s.CumKm > cumKm &&
                            (string.IsNullOrEmpty(s.RoadName) ||
                             s.RoadName.Contains(State.HighwayName) ||
                             State.HighwayName.Contains(s.RoadName)))
                .OrderBy(s => s.CumKm)
                .FirstOrDefault();

            if (next == null)
            {
                State.HasNextSaPa = false;
                return;
            }

            double distKm = next.CumKm - cumKm;
            if (distKm > SaPaApproachThresholdKm)
            {
                State.HasNextSaPa = false;
                return;
            }

            State.HasNextSaPa = true;
            State.NextSaPaName = next.Raw.Name;
            State.NextSaPaType = next.Raw.Type;
            State.NextSaPaDistanceKm = Math.Round(distKm, 1);
        }

        private void UpdateHighwayName(DashcamSensorFrame frame)
        {
            // 150m以内にある名称付き道路のうち、motorway(高速道路本線)をtrunk(国道等)より常に
            // 優先する。同格(motorway同士/trunk同士)の場合のみHeadingに最も近い向きのものを選ぶ
            // （並走区間・ジャンクション付近の絞り込み用途）。
            var candidates = _bundle.Highways
                .Select(h => (Way: h, DistM: HaversineKm(frame.Latitude, frame.Longitude, h.NearLat, h.NearLng) * 1000.0))
                .Where(x => x.DistM <= 150.0)
                .ToList();

            // ❗【修正点】150m以内に道路候補が1件もない場合の処理を安全にフォールバック
            if (candidates.Count == 0)
            {
                // データの読み込み待ちなどで一時的に0件になった場合は、
                // 道路名(HighwayName)の表示だけは直前値をキープしてチラつきを防ぎます。
                // 判定自体をフリーズ（return）させず、後続のIsOnExpresswayの評価へ流します。
                if (string.IsNullOrEmpty(State.HighwayName))
                {
                    IsOnExpressway = false;
                }
                return;
            }

            // ── ここから高速道路の再チェック処理（必ず毎フレーム走るようになります） ──

            // motorway だけでなく、名称に「高速」や「有料」が含まれる trunk も高速道路扱いにする
            bool anyMotorway = candidates.Any(x => x.Way.IsMotorway || x.Way.Name.Contains("高速") || x.Way.Name.Contains("有料"));
            IsOnExpressway = anyMotorway;

            var filtered = anyMotorway
                ? candidates.Where(x => x.Way.IsMotorway || x.Way.Name.Contains("高速") || x.Way.Name.Contains("有料")).ToList()
                : candidates;

            // ⭕【バグ修正】filteredが空(0件)になった場合の配列空っぽエラー(例外落ち)を完全に防ぐ
            if (filtered.Count == 0)
            {
                var fallback = candidates.Where(x => !string.IsNullOrEmpty(x.Way.Name)).OrderBy(x => x.DistM).FirstOrDefault();
                if (fallback.Way != null)
                {
                    State.HighwayName = fallback.Way.Name;
                }
                return;
            }

            if (filtered.Count == 1 || _lastHeadingDeg == null)
            {
                State.HighwayName = filtered.OrderBy(x => x.DistM).First().Way.Name;
                return;
            }

            // ⭕ 正しい位置：elseによる強制遮断を完全に撤廃し、方位が確定した後の並走時の絞り込みを毎フレーム確実に実行させます
            State.HighwayName = filtered
                .OrderBy(x => AngleDiffDeg(x.Way.BearingDeg, _lastHeadingDeg.Value))
                .ThenBy(x => x.DistM)
                .First().Way.Name;
        }

        /// <summary>与えられた地点周辺で、motorwayを最優先に最も近い名称付き道路名を返す（見つからなければ空文字）。
        /// トンネルの道路名をあらかじめ解決する用途（ProjectBundleOntoRoute）専用。1km以内を対象とする。</summary>
        private string FindNearestHighwayName(double lat, double lng)
        {
            var best = _bundle.Highways
                .Select(h => (Way: h, DistKm: HaversineKm(lat, lng, h.NearLat, h.NearLng)))
                .Where(x => x.DistKm <= 1.0)
                .OrderByDescending(x => x.Way.IsMotorway)
                .ThenBy(x => x.DistKm)
                .FirstOrDefault();
            return best.Way?.Name ?? string.Empty;
        }

        private void UpdateHeading(DashcamSensorFrame frame)
        {
            if (frame.SpeedKmh < HeadingLockSpeedKmh)
            {
                // 停車中〜低速はGPS方位ノイズが大きいため、直前の有効な方位をロックしたまま維持する
                return;
            }

            if (_prevFixFrame != null)
            {
                double distM = HaversineKm(_prevFixFrame.Latitude, _prevFixFrame.Longitude, frame.Latitude, frame.Longitude) * 1000.0;
                if (distM >= MinBearingSampleMeters)
                {
                    _lastHeadingDeg = BearingDeg(_prevFixFrame.Latitude, _prevFixFrame.Longitude, frame.Latitude, frame.Longitude);
                    _prevFixFrame = frame;
                }
                // 移動量が小さすぎる場合はノイズとみなし、_prevFixFrameを更新せず次フレームへ持ち越す
            }
            else
            {
                _prevFixFrame = frame;
            }
        }

        // ── ルート折れ線・投影 ──

        private static List<RoutePoint> BuildRoutePolyline(IReadOnlyList<DashcamSensorFrame> frames)
        {
            var valid = frames
                .Where(f => f.HasGpsFix && (Math.Abs(f.Latitude) > 0.0001 || Math.Abs(f.Longitude) > 0.0001))
                .OrderBy(f => f.VideoOffset)
                .ToList();

            // 0.7秒間隔の間引き（DashcamMapPointBuilderと同じ考え方。折れ線の密度は十分でよく、
            // フレーム全件を使うと数十万点になり毎フレームの最近傍探索が重くなるため）
            var interval = TimeSpan.FromSeconds(0.7);
            var downsampled = new List<DashcamSensorFrame>();
            TimeSpan? lastKept = null;
            for (int i = 0; i < valid.Count; i++)
            {
                bool isFirstOrLast = i == 0 || i == valid.Count - 1;
                if (isFirstOrLast || !lastKept.HasValue || (valid[i].VideoOffset - lastKept.Value) >= interval)
                {
                    downsampled.Add(valid[i]);
                    lastKept = valid[i].VideoOffset;
                }
            }

            var route = new List<RoutePoint>(downsampled.Count);
            double cum = 0.0;
            for (int i = 0; i < downsampled.Count; i++)
            {
                if (i > 0)
                    cum += HaversineKm(downsampled[i - 1].Latitude, downsampled[i - 1].Longitude,
                                        downsampled[i].Latitude, downsampled[i].Longitude);
                route.Add(new RoutePoint(cum, downsampled[i].Latitude, downsampled[i].Longitude));
            }
            return route;
        }

        /// <summary>与えられた緯度経度を、ルート折れ線上の最も近い点へ投影し、その累積距離(km)を返す。
        /// 2番目の戻り値は最近傍点までの直線距離(km)（呼び出し側で「ルートから大きく外れている」
        /// 判定が必要になった場合のための予備で、現状は未使用）。</summary>
        private (double CumKm, double DistKm) ProjectToRoute(double lat, double lng)
        {
            double bestCum = 0.0, bestDist = double.MaxValue;
            // 折れ線の点数は多くても数千点程度（0.7秒間隔×21ファイル分でも実測十数分程度）なので
            // 線形探索で十分（毎フレーム呼ばれるが、数千回のHaversine計算はここでは軽い部類）。
            foreach (var p in _route)
            {
                double d = HaversineKm(lat, lng, p.Lat, p.Lng);
                if (d < bestDist) { bestDist = d; bestCum = p.CumKm; }
            }
            return (bestCum, bestDist);
        }

        // ── 幾何計算 ──

        private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371.0088;
            double dLat = ToRad(lat2 - lat1);
            double dLng = ToRad(lng2 - lng1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        private static double BearingDeg(double lat1, double lng1, double lat2, double lng2)
        {
            double phi1 = ToRad(lat1), phi2 = ToRad(lat2), dLng = ToRad(lng2 - lng1);
            double y = Math.Sin(dLng) * Math.Cos(phi2);
            double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLng);
            double deg = ToDeg(Math.Atan2(y, x));
            return (deg + 360.0) % 360.0;
        }

        private static double AngleDiffDeg(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
        private static double ToDeg(double rad) => rad * 180.0 / Math.PI;

        // ── Overpass ──

        private static async Task<OverpassBundle> FetchOverpassAsync(double minLat, double minLng, double maxLat, double maxLng, CancellationToken ct)
        {
            string bbox = string.Create(CultureInfo.InvariantCulture, $"{minLat:F5},{minLng:F5},{maxLat:F5},{maxLng:F5}");
            string query =
                "[out:json][timeout:20];" +
                "(" +
                $"way[\"tunnel\"=\"yes\"][\"highway\"]({bbox});" +
                $"nwr[\"highway\"~\"^(services|rest_area)$\"]({bbox});" +
                $"nwr[\"place\"~\"^(city|town|village|suburb|neighbourhood)$\"]({bbox});" +
                $"way[\"highway\"~\"^(motorway|trunk)$\"][\"name\"]({bbox});" +
                ");" +
                "out center geom tags;";

            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var tunnels = new List<TunnelCandidate>();
            var saPas = new List<SaPaCandidate>();
            var places = new List<PlaceCandidate>();
            var highways = new List<HighwayCandidate>();

            if (!doc.RootElement.TryGetProperty("elements", out var elements))
                return new OverpassBundle(tunnels, saPas, places, highways);

            foreach (var el in elements.EnumerateArray())
            {
                string type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var tags = el.TryGetProperty("tags", out var tg) ? tg : default;
                string GetTag(string key) => tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

                string highway = GetTag("highway");
                string tunnel = GetTag("tunnel");

                // 1. トンネルの判定 (Way型で tunnel=yes 属性を持つもの)
                if (type == "way" && tunnel == "yes" && !string.IsNullOrEmpty(highway))
                {
                    if (el.TryGetProperty("geometry", out var geom) && geom.GetArrayLength() >= 2)
                    {
                        var pts = geom.EnumerateArray()
                            .Select(g => (Lat: g.GetProperty("lat").GetDouble(), Lng: g.GetProperty("lon").GetDouble()))
                            .ToList();

                        int lengthM = 0;
                        for (int i = 1; i < pts.Count; i++)
                            lengthM += (int)Math.Round(HaversineKm(pts[i - 1].Lat, pts[i - 1].Lng, pts[i].Lat, pts[i].Lng) * 1000.0);

                        // 日本のトンネルは tunnel:name に入っていることが多いのでまずこちらを見る
                        string name = GetTag("tunnel:name");
                        if (string.IsNullOrEmpty(name)) name = GetTag("name");

                        // ❗【修正点】名前に「トンネル」が含まれているものだけを本物のトンネルとして採用する
                        // 「〇〇線」や「〇〇高速」などが誤ってトンネル名として登録されているノイズを完全に除外します。
                        if (!string.IsNullOrEmpty(name) && name.Contains("トンネル"))
                        {
                            bool isMotorwayTunnel = highway == "motorway";
                            tunnels.Add(new TunnelCandidate(name, lengthM, pts[0].Lat, pts[0].Lng, pts[^1].Lat, pts[^1].Lng, isMotorwayTunnel));
                        }
                    }
                }

                // 2. SA/PAの判定 (敷地がWayやRelationで登録されていても、out centerにより一律 center から座標が取れる)
                if (highway is "services" or "rest_area")
                {
                    double? lat = TryGetLat(el), lng = TryGetLng(el);
                    if (lat != null && lng != null)
                    {
                        string name = GetTag("name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            string spType = highway == "services" ? "SA" : "PA";
                            if (name.Contains("SA", StringComparison.OrdinalIgnoreCase)) spType = "SA";
                            else if (name.Contains("PA", StringComparison.OrdinalIgnoreCase)) spType = "PA";
                            saPas.Add(new SaPaCandidate(name, spType, lat.Value, lng.Value));
                        }
                    }
                }
                // 3. 地名の判定
                else if (!string.IsNullOrEmpty(GetTag("place")))
                {
                    double? lat = TryGetLat(el), lng = TryGetLng(el);
                    if (lat != null && lng != null)
                    {
                        string name = GetTag("name");
                        if (!string.IsNullOrEmpty(name))
                        {
                            places.Add(new PlaceCandidate(name, lat.Value, lng.Value));
                        }
                    }
                }

                // 4. 道路名の判定 (トンネルとして処理されたWayも、道路名判定のために別途ここで重複して処理させる)
                if (highway is "motorway" or "trunk" && !string.IsNullOrEmpty(GetTag("name")))
                {
                    if (el.TryGetProperty("geometry", out var geom) && geom.GetArrayLength() >= 2)
                    {
                        var pts = geom.EnumerateArray()
                            .Select(g => (Lat: g.GetProperty("lat").GetDouble(), Lng: g.GetProperty("lon").GetDouble()))
                            .ToList();
                        int mid = pts.Count / 2;
                        double bearing = BearingDeg(pts[0].Lat, pts[0].Lng, pts[^1].Lat, pts[^1].Lng);
                        highways.Add(new HighwayCandidate(GetTag("name"), pts[mid].Lat, pts[mid].Lng, bearing, highway == "motorway"));
                    }
                }
            }

            return new OverpassBundle(tunnels, saPas, places, highways);
        }

        private static double? TryGetLat(JsonElement el)
        {
            // 1. centerプロパティがあれば、そこから取る（WayやRelationのSA/PA用）
            if (el.TryGetProperty("center", out var c) && c.TryGetProperty("lat", out var cl)) return cl.GetDouble();
            // 2. 直下に lat があれば、そこから取る（Node用）
            if (el.TryGetProperty("lat", out var lat)) return lat.GetDouble();
            return null;
        }

        private static double? TryGetLng(JsonElement el)
        {
            if (el.TryGetProperty("center", out var c) && c.TryGetProperty("lon", out var cl)) return cl.GetDouble();
            if (el.TryGetProperty("lon", out var lng)) return lng.GetDouble();
            return null;
        }

        // ── Nominatim（地名フォールバック） ──

        private static async Task<string> ReverseGeocodeAsync(double lat, double lng)
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat:F6}&lon={lng:F6}&zoom=12&accept-language=ja");
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("address", out var addr)) return string.Empty;
            string state = addr.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            string city = addr.TryGetProperty("city", out var c) ? c.GetString() ?? ""
                : addr.TryGetProperty("town", out var tn) ? tn.GetString() ?? ""
                : addr.TryGetProperty("village", out var vl) ? vl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(city)) return string.Empty;
            return state + city + "付近";
        }

        // ── 内部データ型 ──
        // Tunnel/SaPaは「Overpassから取得した生の地理座標」(Candidate)と、
        // 「現在のルート折れ線に投影した累積距離」(Projected)を分けて持つ。
        // Candidate側はRouteCacheで使い回せるが、Projected側はルートが変わるたび作り直す。

        private sealed record TunnelCandidate(string Name, int LengthMeters, double EntryLat, double EntryLng, double ExitLat, double ExitLng, bool IsMotorwayTunnel);
        private sealed record ProjectedTunnel(TunnelCandidate Raw, double EntryCumKm, double ExitCumKm, string RoadName);

        private sealed record SaPaCandidate(string Name, string Type, double Lat, double Lng);
        // ── RoadName を追加 ──
        private sealed record ProjectedSaPa(SaPaCandidate Raw, double CumKm, string RoadName);
        private sealed record PlaceCandidate(string Name, double Lat, double Lng);

        private sealed record HighwayCandidate(string Name, double NearLat, double NearLng, double BearingDeg, bool IsMotorway);

        private sealed class OverpassBundle
        {
            public List<TunnelCandidate> Tunnels { get; }
            public List<SaPaCandidate> SaPas { get; }
            public List<PlaceCandidate> Places { get; }
            public List<HighwayCandidate> Highways { get; }

            public OverpassBundle(List<TunnelCandidate> tunnels, List<SaPaCandidate> saPas,
                List<PlaceCandidate> places, List<HighwayCandidate> highways)
            {
                Tunnels = tunnels; SaPas = saPas; Places = places; Highways = highways;
            }

            public static OverpassBundle Empty { get; } = new(new(), new(), new(), new());
        }
    }
}
