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
        /// <summary>GPSロスト中（トンネル進入）の検出方式。設定で切替可能にするための公開列挙体。</summary>
        public enum TunnelEntryDetectionMode
        {
            /// <summary>従来方式（実績あり・既定）。GPSロスト直後の1フレームのみ、直前位置から
            /// 一定距離(0.5km)以内にあるトンネル入口を探して判定する。以降ロストが続いても
            /// 再判定はしない（最初に見つからなければそのまま非表示）。</summary>
            Legacy,

            /// <summary>実験的方式。GPSロスト直前の位置・速度・時刻を起点に、経過時間×速度で
            /// 走行距離を推定し、推定累積距離を使ってロストが続く間も毎フレーム「接近中/通過中」を
            /// 再評価し続ける。長い山岳トンネル（入口の数km手前からGPSが落ちるケース）向けの対策。</summary>
            Experimental,
        }

        // ── 定数 ──

        // Experimental方式の推定に使う速度の上限（km/h）。GPS速度ノイズの瞬間的な跳ね上がりで
        // 推定距離が暴走しないための安全キャップ。日本の高速道路の法定速度より十分高い値。
        private const double MaxAssumedTunnelApproachSpeedKmh = 150.0;

        // ── IC/JCTゲート（IsOnExpressway切替のジッター対策）用の定数 ──
        private const double MotorwayLinkGateRadiusKm = 0.25; // この距離以内をIC/JCTランプ通過とみなす
        private static readonly TimeSpan MotorwayLinkGateWindow = TimeSpan.FromSeconds(30); // ランプ通過後、切替を許可する猶予時間
        private const int ExpresswayHysteresisFrameCount = 3; // ランプデータが1件も無い地域向けフォールバック：この回数連続で同じ判定が出るまで切替を保留

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
        //private bool _isInsideTunnelInstance;    // 実際にGPSロスト等で「通過中」フラグが立ったかどうかの内部管理用(不使用)
        private DashcamSensorFrame? _lastFrame;
        private double _lastCumKm; // 「情報一覧」の通知情報タブ表示用（GetDebugSnapshot参照）

        // ── Experimental方式（推定距離ベース）専用の状態 ──

        private bool _isTrackingLossDynamic; // GPSロスト中、起点情報を確保済みかどうか

        // ── IC/JCTゲート（IsOnExpressway切替のジッター対策）用の状態 ──
        private bool _hasExpresswayBaseline;             // 再生開始直後・シーク直後はfalse（ゲート無しで直接確定させる）

        // ── Overpass取得エラーの可視化用 ──
        // ❗【今回追加】これまでの取得失敗はSystem.Diagnostics.Debug.WriteLineにしか出力しておらず、
        // デバッガを繋いでいない通常の実行では一切見えなかった。「直っていないのか、毎回静かに
        // 例外で落ちているだけなのか」を外部から判別できるようにするため、直近の取得結果を保持する。
        private string? _mainFetchError;          // 本体クエリ(トンネル/SA-PA本体/道路名等)の直近エラー
        private string? _motorwayLinkFetchError;  // motorway_link(IC/JCTランプ)取得の直近エラー
        private string? _namedSaPaFetchError;     // SA/PA名パターン検索の直近エラー

        /// <summary>直近のOverpass取得で発生したエラーのまとめ（無ければnull）。情報一覧デバッグ
        /// パネル等、UI側から参照して表示する想定の公開プロパティ。</summary>
        public string? LastFetchErrorSummary
        {
            get
            {
                var parts = new List<string>();
                if (_mainFetchError != null) parts.Add($"本体:{_mainFetchError}");
                if (_motorwayLinkFetchError != null) parts.Add($"ランプ:{_motorwayLinkFetchError}");
                if (_namedSaPaFetchError != null) parts.Add($"SA/PA名:{_namedSaPaFetchError}");
                return parts.Count == 0 ? null : string.Join(" / ", parts);
            }
        }
        private DateTime? _lastNearMotorwayLinkAt;        // 直近にIC/JCTランプの近傍を通過した時刻
        private bool _pendingExpresswayValue;             // ヒステリシス（フォールバック）用の暫定判定値
        private int _pendingExpresswayStreak;             // 同じ暫定判定が何フレーム連続したか
        private double _lostAtCumKm;         // ロスト直前の実測cumKm（起点）
        private double _lostAtSpeedKmh;      // ロスト直前の実測速度（起点。この速度を一定として距離を推定する）
        private DateTime _lostAtTimestamp;   // ロスト直前の実測フレームの時刻（起点）

        /// <summary>トンネル進入検出方式。既定はLegacy（従来方式）。AppSettings等から復元した値を
        /// 起動時にセットする想定の公開プロパティ（このクラス自身は永続化を行わない）。</summary>
        public TunnelEntryDetectionMode EntryDetectionMode { get; set; } = TunnelEntryDetectionMode.Legacy;

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

            // 【今回追加・デバッグ専用】ここだけは0.8km/2.0km等の距離フィルタを一切適用せず、
            // _bundle.SaPas（Overpassの生取得結果）全件について実際の距離を計算して表示する。
            // 「SA/PA:2件取得できているのに近傍候補が0件」という報告の原因切り分け用。
            // RoadNameカラムに実測距離[dist=X.XXkm]を埋め込んで表示する（デバッグ用の間借り）。
            var nearSaPas = _bundle.SaPas
                .Select(s =>
                {
                    var proj = ProjectToRoute(s.Lat, s.Lng);
                    string rn = FindNearestHighwayName(s.Lat, s.Lng);
                    return new MapInfoDebugSaPaRow
                    {
                        Name = s.Name,
                        Type = s.Type,
                        RoadName = $"{rn} [dist={proj.DistKm:F2}km]",
                        CumKm = proj.CumKm,
                        DistanceAheadKm = proj.CumKm - _lastCumKm,
                    };
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
            _isTrackingLossDynamic = false;
            _hasExpresswayBaseline = false;
            _lastNearMotorwayLinkAt = null;
            _pendingExpresswayStreak = 0;
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
                    _mainFetchError = null;
                }
                catch (Exception ex)
                {
                    // 【要件4】ネットワークエラー・タイムアウト時は例外を投げず、直前のbundleを
                    // そのまま使い続ける（ここでEmptyに差し替えると、通信が一時的に不安定なだけで
                    // 既に取得済みのトンネル/SA-PA情報まで失われてしまう）。
                    _mainFetchError = $"{ex.GetType().Name}: {ex.Message}";
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

            // ── SA/PA 側も同様に、ルートから大きく離れた無関係な施設を排除 ──
            // ❗【修正点】ひるが野高原SAや飛騨白川PAのように、地形の制約で本線から大きく奥まった場所に
            // 配置されている主要な施設がフィルターで誤って消滅してしまうのを防ぐため、
            // 距離判定の防壁を 300m から 800m へ拡張したが、東海北陸自動車道での実測で
            // それでも2件とも弾かれてしまうケースを確認した（情報一覧デバッグタブ：
            // SA/PA:2件取得済みなのにNearbySaPasが0件）。
            // 【今回修正】トンネルと違いSA/PAは構造上「本線から外れた場所にある」のが前提のため、
            // 距離だけで無関係施設を排除しようとすること自体に無理がある。距離の防壁は
            // 「明らかに無関係な遠方施設・並走する下道の道の駅」を弾く程度まで大きく緩め（2.0km）、
            // 本当の絞り込みは後段のUpdateSaPaStateが既に行っている道路名一致判定
            // （s.RoadName.Contains(State.HighwayName) 等）に委ねる。
            var validSaPas = new List<ProjectedSaPa>();
            foreach (var s in _bundle.SaPas)
            {
                var proj = ProjectToRoute(s.Lat, s.Lng);

                if (proj.DistKm > 2.0) // 0.3 → 0.8 → 2.0 へ拡張（道路名一致判定を主な絞り込みとするため）
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

            if (!continuous)
            {
                // ❗【追加】シーク直後・再生開始直後は、直近のIC/JCTランプ通過履歴がそもそも無意味
                // （途中再生の場合、実際にはICを通過済みでも履歴を持ちようがない）。この場合はゲートを
                // 無効化し、次の1フレームだけ現在地の候補判定をそのまま直接採用して基準値を作り直す。
                _hasExpresswayBaseline = false;
                _lastNearMotorwayLinkAt = null;
                _pendingExpresswayStreak = 0;
            }

            if (!frame.HasGpsFix)
            {
                if (EntryDetectionMode == TunnelEntryDetectionMode.Experimental)
                {
                    UpdateFrameGpsLostDynamic(frame, continuous);
                    _lastFrame = frame;
                    return;
                }

                // ── Legacy方式（従来・既定） ──
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
            _isTrackingLossDynamic = false; // Experimental方式の推定起点もリセットする
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

        /// <summary>【実験的】Experimental方式でのGPSロスト時処理。Legacy方式はロスト直後の1フレームだけ
        /// 「直前位置から0.5km以内」を判定して以降は再判定しないが、こちらはロストが続く間、毎フレーム
        /// 「ロスト直前の速度×経過時間」で推定累積距離(estimatedCumKm)を計算し直し、UpdateTunnelStateと
        /// 同じ判定ロジック（接近中/入口〜出口の間なら通過中）に通し続ける。これにより、入口の数km手前
        /// からGPSが落ちる長い山岳トンネルでも、時間経過とともに推定位置がトンネル入口に追いつき次第
        /// 検出できる。速度が変化した場合（トンネル内で減速等）は誤差が蓄積するため、あくまで実験的機能
        /// として位置づける。</summary>
        private void UpdateFrameGpsLostDynamic(DashcamSensorFrame frame, bool continuous)
        {
            if (!continuous)
            {
                // シーク等で不連続になった場合、推定の起点（ロスト直前の実測位置）自体が信用できないため打ち切る
                _isTrackingLossDynamic = false;
                ShouldShowOverlay = false;
                return;
            }

            if (!_isTrackingLossDynamic)
            {
                // ロストが始まった最初のフレーム。直前の実測位置・速度・時刻を推定の起点として記録する。
                if (_lastFrame != null && _lastFrame.HasGpsFix)
                {
                    _lostAtCumKm = _lastCumKm;
                    _lostAtSpeedKmh = Math.Clamp(_lastFrame.SpeedKmh, 0.0, MaxAssumedTunnelApproachSpeedKmh);
                    _lostAtTimestamp = _lastFrame.Timestamp;
                    _isTrackingLossDynamic = true;
                }
                else
                {
                    // 起点になる直前の実測フレームが無い（再生開始直後からロスト状態等）→推定不能
                    ShouldShowOverlay = false;
                    return;
                }
            }

            double elapsedSeconds = (frame.Timestamp - _lostAtTimestamp).TotalSeconds;
            if (elapsedSeconds < 0) elapsedSeconds = 0; // 念のための防御（タイムスタンプの逆行は想定しない）
            double estimatedCumKm = _lostAtCumKm + (_lostAtSpeedKmh / 3600.0) * elapsedSeconds;
            _lastCumKm = estimatedCumKm; // 「情報一覧」タブの表示はロスト中も推定値で追従させる

            UpdateTunnelState(estimatedCumKm); // 既存の「接近中/通過中」判定ロジックをそのまま再利用する

            if (State.HasUpcomingTunnel)
            {
                // 接近中・通過中いずれも表示対象。通過中（DistanceToTunnelMeters==0）の場合は
                // Legacy方式のApplyPassingTunnelStateと同様、利用中道路名をトンネルの道路名で維持する。
                if (State.DistanceToTunnelMeters == 0 && _passingTunnel != null && !string.IsNullOrEmpty(_passingTunnel.RoadName))
                    State.HighwayName = _passingTunnel.RoadName;
                ShouldShowOverlay = true;
            }
            else
            {
                // 推定位置でもどのトンネルの範囲にも入らない＝地下駐車場等の無関係なロストとみなし非表示
                ShouldShowOverlay = false;
            }
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
            // ❗【IC/JCTゲート・前段】現在地がIC/JCTランプ(motorway_link)の近傍かどうかを毎フレーム
            // チェックし、直近通過時刻を更新しておく。150m以内に候補が無いフレームでもここだけは
            // 継続して評価する（ランプ自体は無名wayのことが多く、Highways候補には出てこないため独立処理）。
            bool nearLinkNow = _bundle.MotorwayLinks.Any(l =>
                HaversineKm(frame.Latitude, frame.Longitude, l.Lat, l.Lng) <= MotorwayLinkGateRadiusKm);
            if (nearLinkNow) _lastNearMotorwayLinkAt = frame.Timestamp;

            // 150m以内にある名称付き道路のうち、motorway(高速道路本線)をtrunk(国道等)より常に
            // 優先する。同格(motorway同士/trunk同士)の場合のみHeadingに最も近い向きのものを選ぶ
            // （並走区間・ジャンクション付近の絞り込み用途）。
            var candidates = _bundle.Highways
                .Select(h => (Way: h, DistM: HaversineKm(frame.Latitude, frame.Longitude, h.NearLat, h.NearLng) * 1000.0))
                .Where(x => x.DistM <= 150.0)
                .ToList();

            // ❗【再修正】150m以内に道路候補が1件も無いフレームでは、HighwayNameだけでなく
            // IsOnExpresswayも直前値を保持したまま何もせずreturnする。
            // 【今回判明した実害】以前はここでIsOnExpresswayだけ毎フレーム再評価していたため、
            // 候補0件のフレーム（トンネル入口付近のカーブ等、OSMの線形が薄い区間で普通に起こる）
            // が1回でも挟まると、その時点でIsOnExpresswayがfalseへ落ちてしまっていた。
            // トンネル進入直前にこれが起きると、以後（GPSロスト中はそもそも再評価の機会が無い）
            // ずっとfalseのまま固定されてしまい、FilterTunnelCandidatesForCurrentRoadが
            // 「一般道走行中」の絞り込み（!t.Raw.IsMotorwayTunnelを要求）に切り替わって、
            // 本来の高速道路トンネル（IsMotorwayTunnel=true）を全て除外してしまい、
            // 接近中・通過中とも一切表示されなくなる不具合の原因になっていた
            // （HighwayName自体は直前値を保持していたので一見おかしく見えないのが厄介だった）。
            if (candidates.Count == 0)
                return;

            // ── ここから高速道路の再チェック処理（1件以上候補があるフレームでのみ実行）──

            // motorway だけでなく、名称に「高速」や「有料」が含まれる trunk も高速道路扱いにする
            bool anyMotorway = candidates.Any(x => x.Way.IsMotorway || x.Way.Name.Contains("高速") || x.Way.Name.Contains("有料"));

            // ❗【IC/JCTゲート・本体】GPSジッターで高速走行中に一瞬「国道」側の座標が最近傍になっても、
            // 実際にIC/JCTのランプを通っていない限りIsOnExpresswayを切り替えない。
            // 切替を許可するのは次のいずれか：
            //   ①まだ基準値が無い（再生開始直後・シーク直後）→ゲート無しで直接確定させる
            //   ②直近MotorwayLinkGateWindow(30秒)以内にランプの近傍(250m以内)を通過した
            // どちらでも無い場合、かつ「そもそもこの範囲にランプ情報が1件も無い」（データ欠損地域）
            // 場合に限り、フォールバックとして「同じ判定がExpresswayHysteresisFrameCount回連続」
            // した時だけ切替を許可するヒステリシス方式に切り替える。
            if (!_hasExpresswayBaseline)
            {
                IsOnExpressway = anyMotorway;
                _hasExpresswayBaseline = true;
                _pendingExpresswayValue = anyMotorway;
                _pendingExpresswayStreak = 1;
            }
            else if (anyMotorway != IsOnExpressway)
            {
                bool gateOpen = _lastNearMotorwayLinkAt != null &&
                    (frame.Timestamp - _lastNearMotorwayLinkAt.Value).Duration() <= MotorwayLinkGateWindow;

                if (gateOpen)
                {
                    // 実際にIC/JCTを通過した形跡がある→正規の切替として即座に反映する
                    IsOnExpressway = anyMotorway;
                    _pendingExpresswayValue = anyMotorway;
                    _pendingExpresswayStreak = 1;
                }
                else if (_bundle.MotorwayLinks.Count == 0)
                {
                    // ランプ情報が1件も無い地域（データ欠損）向けのフォールバック：ヒステリシス
                    if (anyMotorway == _pendingExpresswayValue) _pendingExpresswayStreak++;
                    else { _pendingExpresswayValue = anyMotorway; _pendingExpresswayStreak = 1; }

                    if (_pendingExpresswayStreak >= ExpresswayHysteresisFrameCount)
                        IsOnExpressway = anyMotorway;
                    // 届いていない間はIsOnExpresswayを直前値のまま維持（ジッターとみなす）
                }
                // else: ランプ情報はあるがゲートが閉じている＝ICを通過していない→ジッターとして無視し、
                // IsOnExpresswayは直前値を維持する
            }
            else
            {
                // 判定が変わっていない（=ジッターではなく実際に現状維持）ので、暫定値もリセットしておく
                _pendingExpresswayValue = anyMotorway;
                _pendingExpresswayStreak = 1;
            }

            var filtered = IsOnExpressway
                ? candidates.Where(x => x.Way.IsMotorway || x.Way.Name.Contains("高速") || x.Way.Name.Contains("有料")).ToList()
                : candidates;

            // ⭕【例外クラッシュガード】OSMの名称未設定区間などでfilteredが空(0件)になった場合の配列空っぽエラー(例外落ち)を完全に防ぐ
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

            // ⭕ 正しい構造：構文エラーによる強制遮断を完全に撤廃し、方位が確定した後の並走時の絞り込みを毎フレーム確実に実行させます
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

        private async Task<OverpassBundle> FetchOverpassAsync(double minLat, double minLng, double maxLat, double maxLng, CancellationToken ct)
        {
            string bbox = string.Create(CultureInfo.InvariantCulture, $"{minLat:F5},{minLng:F5},{maxLat:F5},{maxLng:F5}");
            string query =
                "[out:json][timeout:20];" +
                "(" +
                $"way[\"tunnel\"=\"yes\"][\"highway\"]({bbox});" +
                $"nwr[\"highway\"~\"^(services|rest_area)$\"]({bbox});" +
                // ❗【追加】JA:Tag:highway=rest_areaの慣例により、SA/PAの敷地(polygon/ノード)自体には
                // name タグが付かず、代わりに分岐点の highway=motorway_junction ノード側に付与されて
                // いることが非常に多い（ひるがの高原SA・飛騨白川PA・川島PA等、実測で確認済み）。
                // 敷地側の名前が空の場合の救済用に、名前付きjunctionノードも別途取得しておく。
                $"node[\"highway\"=\"motorway_junction\"][\"name\"]({bbox});" +
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
            var motorwayLinks = new List<MotorwayLinkCandidate>();
            var junctions = new List<(string Name, double Lat, double Lng)>(); // 名前補完用（下記参照）
            var rawSaPas = new List<(string Name, string Type, double Lat, double Lng)>(); // 名前が空でも一旦保持

            if (!doc.RootElement.TryGetProperty("elements", out var elements))
                return new OverpassBundle(tunnels, saPas, places, highways, motorwayLinks);

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
                        // ❗【修正点】name タグがこの要素自体に無くても、ここでは捨てずに座標だけ保持する。
                        // JA:Tag:highway=rest_areaの慣例で、名前は分岐点のhighway=motorway_junction
                        // ノード側にしか付いていないケースが多いため（ループ後にjunctionsから名前を補完する）。
                        string name = GetTag("name");
                        string spType = highway == "services" ? "SA" : "PA";
                        rawSaPas.Add((name, spType, lat.Value, lng.Value));
                    }
                }
                // 2.5 SA/PA名の補完用：分岐点(motorway_junction)ノード、または
                // SA/PA的な名前パターンを持つノードの名前を収集（タグの種類は問わない）
                else if (type == "node")
                {
                    string jName = GetTag("name");
                    bool looksLikeSaPaLabel = !string.IsNullOrEmpty(jName) &&
                        (jName.Contains("SA") || jName.Contains("PA") || jName.Contains("サービスエリア") ||
                         jName.Contains("パーキングエリア") || jName.Contains("ハイウェイオアシス") || jName.Contains("スマートIC"));
                    if (!string.IsNullOrEmpty(jName) && (highway == "motorway_junction" || looksLikeSaPaLabel))
                    {
                        double? jlat = TryGetLat(el), jlng = TryGetLng(el);
                        if (jlat != null && jlng != null)
                            junctions.Add((jName, jlat.Value, jlng.Value));
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

            // ❗【今回追加】SA/PA名パターンに一致する名前付きノードの広域検索は、bbox内の名前付き
            // ノードを絞り込み条件無しで総当たりするため重く、motorway_linkと同じ理由でOverpassの
            // タイムアウトを誘発しやすい。本体クエリに同居させると巻き添えで全滅するため、
            // motorway_link同様に独立した別リクエストに分離し、失敗してもここだけ諦めて続行する。
            try
            {
                junctions.AddRange(await FetchNamedSaPaNodesAsync(bbox, ct).ConfigureAwait(false));
                _namedSaPaFetchError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _namedSaPaFetchError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[MapInfoProvider] SA/PA名パターン検索失敗（motorway_junction分のみで続行）: {ex.Message}");
            }

            // ── SA/PA名の補完（要素走査ループの外で、全junction収集後にまとめて行う） ──
            // ❗【今回の主修正】現地検証で確認：ひるがの高原SA・飛騨白川PA・川島PAはいずれも
            // highway=services/rest_area の敷地(polygon)自体にはnameタグが付いておらず、
            // 分岐点のhighway=motorway_junctionノード側にのみ名前が付与されていた（OSM日本の
            // 慣例通り）。そのため従来コードの「nameが空なら即座に捨てる」実装では、実在する
            // SA/PAでも_bundle.SaPasに1件も入らず、名前すら内部的に取得できていなかった。
            // ここでは敷地側nameが空の場合、400m以内の最寄りjunction名を採用して救済する。
            foreach (var raw in rawSaPas)
            {
                string name = raw.Name;
                if (string.IsNullOrEmpty(name) && junctions.Count > 0)
                {
                    var nearest = junctions
                        .Select(j => (j.Name, DistKm: HaversineKm(raw.Lat, raw.Lng, j.Lat, j.Lng)))
                        .Where(x => x.DistKm <= 0.4)
                        .OrderBy(x => x.DistKm)
                        .FirstOrDefault();
                    if (nearest.Name != null) name = nearest.Name;
                }

                if (string.IsNullOrEmpty(name)) continue; // 敷地側・junction側どちらにも名前が無ければ諦める

                string spType = raw.Type;
                if (name.Contains("SA", StringComparison.OrdinalIgnoreCase)) spType = "SA";
                else if (name.Contains("PA", StringComparison.OrdinalIgnoreCase)) spType = "PA";
                saPas.Add(new SaPaCandidate(name, spType, raw.Lat, raw.Lng));
            }

            // ── 敷地(polygon)自体が存在せず、名前付きノード単体でしかSA/PAが表現されていない
            //    ケースの救済（川島PA等、オアシス一体型施設で実測確認） ──
            // ❗【今回追加】上のループはrawSaPas（highway=services/rest_area要素）が存在することが
            // 前提の「名前の補完」処理だが、川島PAのようにそもそも敷地側にhighway=services/rest_area
            // タグが一切付いていない（amenity=parking等、別タグで表現されている）施設は、
            // junctions（motorway_junctionノード、またはSA/PA名パターン一致ノード）の中にしか
            // 情報が存在しない。この場合、宛先(rawSaPas)が無いため上のループでは一生救済されない。
            // ここではjunctionsのうちSA/PAらしい名前を持つものを、直接候補として採用する
            // （既にrawSaPas由来で登録済みの近傍(400m以内)候補と重複する場合は追加しない）。
            foreach (var j in junctions)
            {
                bool looksLikeSaPa = j.Name.Contains("SA") || j.Name.Contains("PA") ||
                    j.Name.Contains("サービスエリア") || j.Name.Contains("パーキングエリア");
                if (!looksLikeSaPa) continue;

                bool alreadyCovered = saPas.Any(s => HaversineKm(s.Lat, s.Lng, j.Lat, j.Lng) <= 0.4);
                if (alreadyCovered) continue;

                string spType = j.Name.Contains("SA") ? "SA" : "PA";
                saPas.Add(new SaPaCandidate(j.Name, spType, j.Lat, j.Lng));
            }

            // ❗【今回修正】motorway_link(IC/JCTランプ)の取得は、区間によっては件数が膨らみ
            // Overpassのタイムアウトを誘発しやすいため、本体クエリとは独立した別リクエストに分離した。
            // これにより、ランプ取得だけが失敗してもトンネル・SA/PA等の本体データは影響を受けない
            // （失敗時はmotorwayLinksが空のままなので、UpdateHighwayName側は自動的にヒステリシス
            // 方式へフォールバックする設計になっている）。
            try
            {
                motorwayLinks.AddRange(await FetchMotorwayLinksAsync(bbox, ct).ConfigureAwait(false));
                _motorwayLinkFetchError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _motorwayLinkFetchError = $"{ex.GetType().Name}: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[MapInfoProvider] motorway_link取得失敗（IC/JCTゲート無しで続行）: {ex.Message}");
            }

            return new OverpassBundle(tunnels, saPas, places, highways, motorwayLinks);
        }

        /// <summary>IC/JCTランプ(highway=motorway_link)だけを取得する軽量な別リクエスト。
        /// geom(全ジオメトリ)は不要なので center のみ要求し、サーバー負荷を最小限にする。</summary>
        private async Task<List<MotorwayLinkCandidate>> FetchMotorwayLinksAsync(string bbox, CancellationToken ct)
        {
            string query =
                "[out:json][timeout:20];" +
                $"way[\"highway\"=\"motorway_link\"]({bbox});" +
                "out center;";
            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var result = new List<MotorwayLinkCandidate>();
            if (!doc.RootElement.TryGetProperty("elements", out var elements)) return result;
            foreach (var el in elements.EnumerateArray())
            {
                double? lat = TryGetLat(el), lng = TryGetLng(el);
                if (lat != null && lng != null) result.Add(new MotorwayLinkCandidate(lat.Value, lng.Value));
            }
            return result;
        }

        /// <summary>SA/PA名パターン（SA/PA/サービスエリア/パーキングエリア/ハイウェイオアシス/スマートIC）に
        /// 一致する名前付きノードを広く取得する独立リクエスト。タグ条件が無い総当たり正規表現検索のため
        /// motorway_linkと同様に重く、タイムアウトしやすい。本体クエリとは切り離し、失敗してもここだけ
        /// 諦めて続行する（motorway_junction分の名前だけで補完処理は続行できる）。</summary>
        private async Task<List<(string Name, double Lat, double Lng)>> FetchNamedSaPaNodesAsync(string bbox, CancellationToken ct)
        {
            string query =
                "[out:json][timeout:20];" +
                $"node[\"name\"~\"(SA|PA|サービスエリア|パーキングエリア|ハイウェイオアシス|スマートIC)\"]({bbox});" +
                "out center tags;";
            using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
            using var resp = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var result = new List<(string Name, double Lat, double Lng)>();
            if (!doc.RootElement.TryGetProperty("elements", out var elements)) return result;
            foreach (var el in elements.EnumerateArray())
            {
                var tags = el.TryGetProperty("tags", out var tg) ? tg : default;
                string name = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("name", out var v) ? v.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;
                double? lat = TryGetLat(el), lng = TryGetLng(el);
                if (lat != null && lng != null) result.Add((name, lat.Value, lng.Value));
            }
            return result;
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
        private sealed record MotorwayLinkCandidate(double Lat, double Lng); // IC/JCTのランプ（ジッター対策ゲート用）
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
            public List<MotorwayLinkCandidate> MotorwayLinks { get; } // IC/JCTジャンクション判定ゲート用

            public OverpassBundle(List<TunnelCandidate> tunnels, List<SaPaCandidate> saPas,
                List<PlaceCandidate> places, List<HighwayCandidate> highways, List<MotorwayLinkCandidate> motorwayLinks)
            {
                Tunnels = tunnels; SaPas = saPas; Places = places; Highways = highways; MotorwayLinks = motorwayLinks;
            }

            public static OverpassBundle Empty { get; } = new(new(), new(), new(), new(), new());
        }
    }
}
