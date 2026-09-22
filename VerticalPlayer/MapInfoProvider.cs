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
        private const double SaPaApproachThresholdKm = 30.0;         // SA/PA案内を出し始める手前距離
        private const double PlaceMatchRadiusKm = 6.0;               // Overpassのplaceノードを採用する最大距離
        private const double MinBearingSampleMeters = 8.0;           // Heading再計算に使う最小移動量
        private const double HeadingLockSpeedKmh = 3.0;              // これ未満の速度ではHeadingをロック
        private const double BboxPaddingDeg = 0.03;                  // 約3km相当の余白（トンネル入口が測位断続範囲の外に出るのを防ぐ）
        private static readonly TimeSpan FrameContinuityThreshold = TimeSpan.FromSeconds(2); // seek判定用

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

        private ProjectedTunnel? _passingTunnel;
        private DashcamSensorFrame? _lastFrame;

        /// <summary>常に同じインスタンスを使い回す（毎フレームのGC割り当てを避けるため）。
        /// UpdateFrame()の戻り値ではなく、このプロパティを都度読み直して使うこと。</summary>
        public MapInfoState State { get; } = new();

        /// <summary>オーバーレイ全体を表示すべきか。falseの間はStateの中身を見ずに
        /// Collapsedにしてよい（直前の値は保持されるが表示はしない）。</summary>
        public bool ShouldShowOverlay { get; private set; }

        /// <summary>ファイル（または前後を連結したルート）を開いた直後に1回だけ呼ぶ。
        /// Overpassへの通信はこの中でのみ発生し、失敗・タイムアウトしても例外は投げず、
        /// 以後のUpdateFrame()は常にShouldShowOverlay=falseを返す（サイレントに非表示）。</summary>
        public async Task LoadRouteAsync(IReadOnlyList<DashcamSensorFrame> mergedFrames, CancellationToken ct = default)
        {
            _routeReady = false;
            _passingTunnel = null;
            _prevFixFrame = null;
            _lastHeadingDeg = null;
            _lastFrame = null;
            _route = BuildRoutePolyline(mergedFrames);

            if (_route.Count < 2)
            {
                _bundle = OverpassBundle.Empty;
                _projectedTunnels = new();
                _projectedSaPas = new();
                _routeReady = true; // ルートが無いだけで「失敗」ではないので、以後は静かに非表示のまま動く
                return;
            }

            double minLat = _route.Min(p => p.Lat) - BboxPaddingDeg;
            double maxLat = _route.Max(p => p.Lat) + BboxPaddingDeg;
            double minLng = _route.Min(p => p.Lng) - BboxPaddingDeg;
            double maxLng = _route.Max(p => p.Lng) + BboxPaddingDeg;
            string cacheKey = string.Create(CultureInfo.InvariantCulture,
                $"{minLat:F2},{minLng:F2},{maxLat:F2},{maxLng:F2}");

            if (RouteCache.TryGetValue(cacheKey, out var cached))
            {
                _bundle = cached;
            }
            else
            {
                try
                {
                    _bundle = await FetchOverpassAsync(minLat, minLng, maxLat, maxLng, ct).ConfigureAwait(false);
                    RouteCache[cacheKey] = _bundle;
                }
                catch (Exception ex)
                {
                    // 【要件4】ネットワークエラー・タイムアウト時は例外を投げずサイレントに非表示のまま続行する
                    System.Diagnostics.Debug.WriteLine($"[MapInfoProvider] Overpass取得失敗（オーバーレイ非表示のまま続行）: {ex.Message}");
                    _bundle = OverpassBundle.Empty;
                }
            }

            ProjectBundleOntoRoute();
            _routeReady = true;
        }

        /// <summary>_bundle（生の地理座標）から、現在のルート折れ線上の累積距離(km)を都度計算し直す。
        /// キャッシュされたOverpassの生データは使い回せても、ルート折れ線側は連結ファイルの
        /// 組み合わせによって毎回変わり得るため、この投影だけはLoadRouteAsyncのたびに必ず行う。</summary>
        private void ProjectBundleOntoRoute()
        {
            _projectedTunnels = _bundle.Tunnels.Select(t =>
            {
                double cumA = ProjectToRoute(t.EntryLat, t.EntryLng).CumKm;
                double cumB = ProjectToRoute(t.ExitLat, t.ExitLng).CumKm;
                return new ProjectedTunnel(t, Math.Min(cumA, cumB));
            }).ToList();

            _projectedSaPas = _bundle.SaPas.Select(s =>
                new ProjectedSaPa(s, ProjectToRoute(s.Lat, s.Lng).CumKm)
            ).ToList();
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
                (frame.VideoOffset - _lastFrame.VideoOffset).Duration() <= FrameContinuityThreshold;

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
                    State.HasUpcomingTunnel && State.DistanceToTunnelMeters <= TunnelApproachThresholdMeters &&
                    TryFindTunnelNear(_lastFrame, out var enteredTunnel))
                {
                    // 【要件5】接近中の直後にGPSロスト→トンネル進入とみなし、通過中表示へ切り替える
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

            UpdateLocationName(frame);
            UpdateTunnelState(cumKm);
            UpdateSaPaState(cumKm);
            UpdateHighwayName(frame);

            ShouldShowOverlay = State.HasNextSaPa || State.HasUpcomingTunnel || !string.IsNullOrEmpty(State.CurrentLocationName);
            _lastFrame = frame;
        }

        private void ApplyPassingTunnelState()
        {
            if (_passingTunnel == null) { ShouldShowOverlay = false; return; }
            State.HasUpcomingTunnel = true;
            State.NextTunnelName = _passingTunnel.Raw.Name;
            State.NextTunnelLengthMeters = _passingTunnel.Raw.LengthMeters;
            State.DistanceToTunnelMeters = 0; // 0＝MapInfoOverlayControl側で「通過中」表記に切り替える合図
            // SA/PA・地名はGPSが無いため更新できない。直前値をそのまま保持して表示を続ける。
            ShouldShowOverlay = true;
        }

        private bool TryFindTunnelNear(DashcamSensorFrame lastFixFrame, out ProjectedTunnel? tunnel)
        {
            double cumKm = ProjectToRoute(lastFixFrame.Latitude, lastFixFrame.Longitude).CumKm;
            tunnel = _projectedTunnels
                .Where(t => t.EntryCumKm >= cumKm - 0.05) // 直前フレーム位置より少し手前まで許容
                .OrderBy(t => t.EntryCumKm)
                .FirstOrDefault();
            return tunnel != null;
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

        private void UpdateTunnelState(double cumKm)
        {
            var next = _projectedTunnels
                .Where(t => t.EntryCumKm > cumKm)
                .OrderBy(t => t.EntryCumKm)
                .FirstOrDefault();

            if (next == null)
            {
                State.HasUpcomingTunnel = false;
                return;
            }

            double distM = (next.EntryCumKm - cumKm) * 1000.0;
            if (distM > TunnelApproachThresholdMeters)
            {
                State.HasUpcomingTunnel = false;
                return;
            }

            State.HasUpcomingTunnel = true;
            State.NextTunnelName = next.Raw.Name;
            State.NextTunnelLengthMeters = next.Raw.LengthMeters;
            State.DistanceToTunnelMeters = Math.Max(1, (int)Math.Round(distM)); // 0は「通過中」の合図と衝突するため最小1にする
        }

        private void UpdateSaPaState(double cumKm)
        {
            var next = _projectedSaPas
                .Where(s => s.CumKm > cumKm)
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
            // 150m以内にある名称付き道路のうち、Headingに最も近い向きのものを採用する
            // （ジャンクション付近で並走する道路が複数候補に挙がる場合の絞り込み用途）。
            var candidates = _bundle.Highways
                .Select(h => (Way: h, DistM: HaversineKm(frame.Latitude, frame.Longitude, h.NearLat, h.NearLng) * 1000.0))
                .Where(x => x.DistM <= 150.0)
                .ToList();

            if (candidates.Count == 0) { State.HighwayName = string.Empty; return; }

            if (candidates.Count == 1 || _lastHeadingDeg == null)
            {
                State.HighwayName = candidates.OrderBy(x => x.DistM).First().Way.Name;
                return;
            }

            State.HighwayName = candidates
                .OrderBy(x => AngleDiffDeg(x.Way.BearingDeg, _lastHeadingDeg.Value))
                .ThenBy(x => x.DistM)
                .First().Way.Name;
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

                bool isTunnelWay = type == "way" && GetTag("tunnel") == "yes" && !string.IsNullOrEmpty(GetTag("highway"));
                if (isTunnelWay)
                {
                    if (!el.TryGetProperty("geometry", out var geom) || geom.GetArrayLength() < 2) continue;
                    var pts = geom.EnumerateArray()
                        .Select(g => (Lat: g.GetProperty("lat").GetDouble(), Lng: g.GetProperty("lon").GetDouble()))
                        .ToList();
                    int lengthM = 0;
                    for (int i = 1; i < pts.Count; i++)
                        lengthM += (int)Math.Round(HaversineKm(pts[i - 1].Lat, pts[i - 1].Lng, pts[i].Lat, pts[i].Lng) * 1000.0);
                    string name = GetTag("name");
                    if (string.IsNullOrEmpty(name)) name = GetTag("tunnel:name");
                    tunnels.Add(new TunnelCandidate(name, lengthM, pts[0].Lat, pts[0].Lng, pts[^1].Lat, pts[^1].Lng));
                    continue;
                }

                string highway = GetTag("highway");
                if (highway is "services" or "rest_area")
                {
                    double? lat = TryGetLat(el), lng = TryGetLng(el);
                    if (lat == null || lng == null) continue;
                    string name = GetTag("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    string spType = highway == "services" ? "SA" : "PA";
                    if (name.Contains("SA", StringComparison.OrdinalIgnoreCase)) spType = "SA";
                    else if (name.Contains("PA", StringComparison.OrdinalIgnoreCase)) spType = "PA";
                    saPas.Add(new SaPaCandidate(name, spType, lat.Value, lng.Value));
                }
                else if (!string.IsNullOrEmpty(GetTag("place")))
                {
                    double? lat = TryGetLat(el), lng = TryGetLng(el);
                    if (lat == null || lng == null) continue;
                    string name = GetTag("name");
                    if (string.IsNullOrEmpty(name)) continue;
                    places.Add(new PlaceCandidate(name, lat.Value, lng.Value));
                }
                else if (highway is "motorway" or "trunk" && !string.IsNullOrEmpty(GetTag("name")))
                {
                    if (!el.TryGetProperty("geometry", out var geom) || geom.GetArrayLength() < 2) continue;
                    var pts = geom.EnumerateArray()
                        .Select(g => (Lat: g.GetProperty("lat").GetDouble(), Lng: g.GetProperty("lon").GetDouble()))
                        .ToList();
                    int mid = pts.Count / 2;
                    double bearing = BearingDeg(pts[0].Lat, pts[0].Lng, pts[^1].Lat, pts[^1].Lng);
                    highways.Add(new HighwayCandidate(GetTag("name"), pts[mid].Lat, pts[mid].Lng, bearing));
                }
            }

            return new OverpassBundle(tunnels, saPas, places, highways);
        }

        private static double? TryGetLat(JsonElement el)
        {
            if (el.TryGetProperty("lat", out var lat)) return lat.GetDouble();
            if (el.TryGetProperty("center", out var c) && c.TryGetProperty("lat", out var cl)) return cl.GetDouble();
            return null;
        }

        private static double? TryGetLng(JsonElement el)
        {
            if (el.TryGetProperty("lon", out var lng)) return lng.GetDouble();
            if (el.TryGetProperty("center", out var c) && c.TryGetProperty("lon", out var cl)) return cl.GetDouble();
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

        private sealed record TunnelCandidate(string Name, int LengthMeters, double EntryLat, double EntryLng, double ExitLat, double ExitLng);
        private sealed record ProjectedTunnel(TunnelCandidate Raw, double EntryCumKm);

        private sealed record SaPaCandidate(string Name, string Type, double Lat, double Lng);
        private sealed record ProjectedSaPa(SaPaCandidate Raw, double CumKm);

        private sealed record PlaceCandidate(string Name, double Lat, double Lng);

        private sealed record HighwayCandidate(string Name, double NearLat, double NearLng, double BearingDeg);

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
