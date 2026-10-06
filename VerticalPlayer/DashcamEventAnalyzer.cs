using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// センサーフレーム列（NMEA）だけから算出できるイベント（Gセンサー急変点・測位ロスト区間）の解析。
    /// 地図データが必要なトンネル名・SA/PAはMapInfoProvider.BuildMapEventMarkersが担当する。
    /// いずれもファイルを開いた時にバックグラウンドスレッドで1回だけ呼ぶ想定で、
    /// 再生中のフレームごとの計算は行わない。UIには一切触れない。
    /// </summary>
    public static class DashcamEventAnalyzer
    {
        /// <summary>Gセンサーマーカーの上限数（路面の荒れた区間で大量に出てシークバーが埋まるのを防ぐ。強い順に残す）。</summary>
        private const int MaxGSensorMarkers = 80;

        /// <summary>連続して閾値を超えたサンプルを1つのイベントにまとめる最大の隙間(秒)。</summary>
        private const double GSensorClusterGapSec = 1.0;

        /// <summary>
        /// Gセンサーの急変点を検出する。ファイル全体の中央値を基準(0)とした偏差で、
        /// 前後(Y)・左右(X)・上下(Z)のいずれかが閾値以上になった点をイベントにする
        /// （重力成分や取り付け角のオフセットは中央値で打ち消される）。
        /// 閾値を超えた連続サンプルは1件にまとめ、ピーク位置をマーカー位置にする。
        /// 軸の割り当てと符号はNmeaSensorParser側で暫定のため、ラベルは
        /// 前後Yがマイナス＝急減速、プラス＝急加速として扱う。
        /// </summary>
        public static List<DashcamEventMarker> AnalyzeGSensor(IReadOnlyList<DashcamSensorFrame> frames, double thresholdG)
        {
            var result = new List<DashcamEventMarker>();
            if (frames == null || frames.Count < 3 || thresholdG <= 0) return result;

            var ordered = frames.OrderBy(f => f.VideoOffset).ToList();
            double bx = Median(ordered.Select(f => f.AccelX));
            double by = Median(ordered.Select(f => f.AccelY));
            double bz = Median(ordered.Select(f => f.AccelZ));

            var cluster = new List<(DashcamSensorFrame F, double Dx, double Dy, double Dz, double Mag)>();
            double lastOverSec = double.NegativeInfinity;

            void Flush()
            {
                if (cluster.Count == 0) return;
                var peak = cluster[0];
                foreach (var c in cluster)
                    if (c.Mag > peak.Mag) peak = c;
                result.Add(BuildGMarker(peak.F, peak.Dx, peak.Dy, peak.Dz, peak.Mag));
                cluster.Clear();
            }

            foreach (var f in ordered)
            {
                double dx = f.AccelX - bx, dy = f.AccelY - by, dz = f.AccelZ - bz;
                double mag = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
                double t = f.VideoOffset.TotalSeconds;
                if (mag < thresholdG) continue;

                if (cluster.Count > 0 && t - lastOverSec > GSensorClusterGapSec)
                    Flush();
                cluster.Add((f, dx, dy, dz, mag));
                lastOverSec = t;
            }
            Flush();

            if (result.Count > MaxGSensorMarkers)
            {
                result = result.OrderByDescending(m => m.Magnitude).Take(MaxGSensorMarkers)
                               .OrderBy(m => m.VideoOffsetSeconds).ToList();
            }
            return result;
        }

        private static DashcamEventMarker BuildGMarker(DashcamSensorFrame f, double dx, double dy, double dz, double mag)
        {
            string label;
            DashcamEventSubType sub;
            double signed;
            double ax = Math.Abs(dx), ay = Math.Abs(dy), az = Math.Abs(dz);
            if (ay >= ax && ay >= az)
            {
                label = $"{(dy < 0 ? "急減速" : "急加速")} {Signed(dy)}G";
                sub = dy < 0 ? DashcamEventSubType.HardBrake : DashcamEventSubType.HardAccel;
                signed = dy;
            }
            else if (ax >= az)
            {
                label = $"急ハンドル {Signed(dx)}G";
                sub = DashcamEventSubType.Steer;
                signed = dx;
            }
            else
            {
                label = $"上下衝撃 {Signed(dz)}G";
                sub = DashcamEventSubType.VerticalImpact;
                signed = dz;
            }

            return new DashcamEventMarker
            {
                Timestamp = f.Timestamp,
                VideoOffsetSeconds = f.VideoOffset.TotalSeconds,
                DurationSeconds = 0,
                EventType = DashcamEventType.GSensor,
                SubType = sub,
                SignedValue = signed,
                Label = label,
                Magnitude = mag
            };
        }

        /// <summary>
        /// 測位ロスト区間（GPSが途切れた区間）をトンネル候補として検出する。地図データ(Overpass)が
        /// 取得できない場合や、地図上にトンネルとして載っていない区間への保険。
        /// ファイル内で「前に測位あり→ロスト→後に測位あり」の形で完結する区間だけを対象にする
        /// （ファイル先頭の測位待ち[コールドスタート]を誤ってトンネル扱いしないため）。
        /// 地図由来のトンネル(mapMarkers)と重なる区間は重複するので出さない。
        /// </summary>
        public static List<DashcamEventMarker> AnalyzeGpsLoss(
            IReadOnlyList<DashcamSensorFrame> frames,
            TimeSpan duration,
            IReadOnlyList<DashcamEventMarker> mapMarkers,
            double minSeconds = 8.0)
        {
            var result = new List<DashcamEventMarker>();
            if (frames == null || frames.Count < 3) return result;

            var ordered = frames.OrderBy(f => f.VideoOffset).ToList();
            bool seenFix = false;
            int i = 0;
            while (i < ordered.Count)
            {
                var f = ordered[i];
                if (f.HasGpsFix) { seenFix = true; i++; continue; }

                int start = i;
                while (i < ordered.Count && !ordered[i].HasGpsFix) i++;
                if (!seenFix || i >= ordered.Count) continue; // 先頭の測位待ち／ファイル末尾まで続く区間は対象外

                double s = ordered[start].VideoOffset.TotalSeconds;
                double e = ordered[i].VideoOffset.TotalSeconds; // 測位が戻った最初のフレーム
                double len = e - s;
                if (len < minSeconds) continue;

                bool overlapsMap = mapMarkers != null && mapMarkers.Any(m =>
                    m.EventType == DashcamEventType.Tunnel && s < m.EndOffsetSeconds && e > m.VideoOffsetSeconds);
                if (overlapsMap) continue;

                result.Add(new DashcamEventMarker
                {
                    Timestamp = ordered[start].Timestamp,
                    VideoOffsetSeconds = s,
                    DurationSeconds = len,
                    EventType = DashcamEventType.Tunnel,
                    SubType = DashcamEventSubType.GpsLossTunnel,
                    Label = $"測位ロスト区間（トンネル推定 {len:F0}秒）",
                    Magnitude = 0
                });
            }
            return result;
        }

        private static double Median(IEnumerable<double> values)
        {
            var arr = values.OrderBy(v => v).ToArray();
            if (arr.Length == 0) return 0;
            int mid = arr.Length / 2;
            return arr.Length % 2 == 1 ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
        }

        private static string Signed(double v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
    }
}
