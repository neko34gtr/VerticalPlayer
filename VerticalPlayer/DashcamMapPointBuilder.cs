using System;
using System.Collections.Generic;
using System.Linq;

namespace VerticalPlayer.Dashcam
{
    public readonly record struct MapPoint(double Lat, double Lng);

    /// <summary>1本のポリラインとして描画する区間。Dashed=trueはGPSロスト（トンネル等）区間の接続線。</summary>
    public sealed record MapSegment(bool Dashed, List<MapPoint> Points);

    /// <summary>
    /// センサーフレーム列から地図描画用の軌跡データを作る。
    /// - HasGpsFix==falseのフレームは座標が信頼できない（生値がプレースホルダの可能性が高い）ため
    ///   そのまま点として使わず、有効な測位点だけを間引いて繋ぐ。
    /// - 有効な測位点同士の時間差が大きく開いている箇所（GPSロスト区間があったと推定される）は
    ///   前後の点を結ぶ2点だけの区間としてDashed=trueで返す。
    /// </summary>
    public static class DashcamMapPointBuilder
    {
        public static List<MapSegment> BuildSegments(
            IReadOnlyList<DashcamSensorFrame> frames,
            TimeSpan? minInterval = null)
        {
            var interval = minInterval ?? TimeSpan.FromSeconds(0.7);
            var gapThreshold = TimeSpan.FromTicks(Math.Max(interval.Ticks * 3, TimeSpan.FromSeconds(3).Ticks));

            var valid = frames
                .Where(f => f.HasGpsFix)
                .OrderBy(f => f.VideoOffset)
                .ToList();

            // 座標(0,0)近辺は明らかな異常値として除外（プレースホルダの読み違い等の保険）
            valid = valid.Where(f => Math.Abs(f.Latitude) > 0.0001 || Math.Abs(f.Longitude) > 0.0001).ToList();

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

            var segments = new List<MapSegment>();
            if (downsampled.Count == 0)
                return segments;

            var currentPoints = new List<MapPoint> { new(downsampled[0].Latitude, downsampled[0].Longitude) };
            var lastOffset = downsampled[0].VideoOffset;

            for (int i = 1; i < downsampled.Count; i++)
            {
                var f = downsampled[i];
                var point = new MapPoint(f.Latitude, f.Longitude);

                if (f.VideoOffset - lastOffset > gapThreshold)
                {
                    // 通常区間をここで一旦区切り、直前点→今回点を結ぶ破線区間を挟む
                    if (currentPoints.Count >= 2)
                        segments.Add(new MapSegment(false, currentPoints));

                    var prevPoint = currentPoints[^1];
                    segments.Add(new MapSegment(true, new List<MapPoint> { prevPoint, point }));

                    currentPoints = new List<MapPoint> { point };
                }
                else
                {
                    currentPoints.Add(point);
                }

                lastOffset = f.VideoOffset;
            }

            if (currentPoints.Count >= 2)
                segments.Add(new MapSegment(false, currentPoints));

            return segments;
        }
    }
}
