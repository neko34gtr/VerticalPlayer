using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 走行ファイル群のイベント索引を作る。ファイルを開かず、各ファイルのNMEA(センサー)だけを
    /// バックグラウンドで読み、Gセンサー急変点と測位ロスト区間(トンネル推定)を概算する。
    /// 地図由来のトンネル名・SA/PAは、再生で実際に開いたファイルの精密な解析結果
    /// （<see cref="FromMarkers"/>）が入った時点で、そのファイル分を置き換える。
    /// </summary>
    public sealed class DashcamEventIndexer
    {
        /// <summary>索引の対象1件。WPFの型(DashcamMediaGroup)に依存しないよう必要な値だけ持つ。</summary>
        public sealed record GroupInput(string GroupKey, string? FrontPath, string? RearPath, string? NmeaPath, DateTime? Timestamp);

        /// <summary>UIへ結果を届ける単位（ファイルを数件まとめて1回で通知する）。</summary>
        private const int PublishBatchSize = 12;

        /// <summary>マーカー列を一覧の行へ変換する。トンネルは進入(区間の開始)と、ファイル内で終わる場合のみ脱出の2行にする。</summary>
        public static List<EventItemViewModel> FromMarkers(string groupKey, string filePath, DateTime? baseTime,
            IReadOnlyList<DashcamEventMarker> markers, double fileDurationSeconds, bool precise)
        {
            var items = new List<EventItemViewModel>();
            foreach (var m in markers)
            {
                DateTime? ts = m.Timestamp ?? baseTime?.AddSeconds(m.VideoOffsetSeconds);
                switch (m.EventType)
                {
                    case DashcamEventType.Tunnel:
                        items.Add(new EventItemViewModel(groupKey, filePath, ts, m.VideoOffsetSeconds,
                            DashcamEventKind.TunnelEnter, m.Label) { IsPrecise = precise });
                        if (m.IsRange && (fileDurationSeconds <= 0 || m.EndOffsetSeconds < fileDurationSeconds - 0.5))
                        {
                            DateTime? tsEnd = ts?.AddSeconds(m.DurationSeconds);
                            items.Add(new EventItemViewModel(groupKey, filePath, tsEnd, m.EndOffsetSeconds,
                                DashcamEventKind.TunnelExit, m.Label) { IsPrecise = precise });
                        }
                        break;

                    case DashcamEventType.SaPa:
                        items.Add(new EventItemViewModel(groupKey, filePath, ts, m.VideoOffsetSeconds,
                            DashcamEventKind.SaPa, m.Label) { IsPrecise = precise });
                        break;

                    case DashcamEventType.GSensor:
                    {
                        var (kind, axis) = m.SubType switch
                        {
                            DashcamEventSubType.HardBrake => (DashcamEventKind.HardBrake, "前後"),
                            DashcamEventSubType.HardAccel => (DashcamEventKind.HardAccel, "前後"),
                            DashcamEventSubType.Steer => (DashcamEventKind.Steer, "左右"),
                            _ => (DashcamEventKind.Impact, "上下")
                        };
                        string detail = $"{axis}G: {m.SignedValue.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture)}G";
                        items.Add(new EventItemViewModel(groupKey, filePath, ts, m.VideoOffsetSeconds, kind, detail) { IsPrecise = precise });
                        break;
                    }
                }
            }
            return items;
        }

        /// <summary>
        /// 全ファイルを順に解析する（スレッドプール上で動き、UIには触れない）。
        /// <paramref name="publish"/>は数ファイルごとに呼ばれる（呼び出し側でUIスレッドへ載せ替えること）。
        /// 個々のファイルの読み込み失敗は、そのファイルだけ飛ばして続行する。
        /// </summary>
        public Task IndexAsync(IReadOnlyList<GroupInput> groups, double thresholdG,
            Action<IReadOnlyList<DashcamEventListViewModel.FileEvents>> publish,
            Action<int, int> progress, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                int total = groups.Count;
                var batch = new List<DashcamEventListViewModel.FileEvents>();
                int done = 0;
                foreach (var g in groups)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        string? file = g.FrontPath ?? g.RearPath;
                        if (file != null && g.NmeaPath != null)
                        {
                            // 動画長を開かずに知れないので、10Hz(1レコード=100ms)の仮定で位置を概算する。
                            // 再生で開いたファイルは動画長込みの精密な結果で置き換わる。
                            var frames = NmeaSensorParser.Parse(g.NmeaPath, g.Timestamp);
                            if (frames.Count > 0)
                            {
                                double dur = frames[^1].VideoOffset.TotalSeconds;
                                var markers = new List<DashcamEventMarker>();
                                markers.AddRange(DashcamEventAnalyzer.AnalyzeGSensor(frames, thresholdG));
                                markers.AddRange(DashcamEventAnalyzer.AnalyzeGpsLoss(frames, TimeSpan.FromSeconds(dur),
                                    Array.Empty<DashcamEventMarker>()));
                                markers.Sort((a, b) => a.VideoOffsetSeconds.CompareTo(b.VideoOffsetSeconds));
                                batch.Add(new DashcamEventListViewModel.FileEvents(g.GroupKey,
                                    FromMarkers(g.GroupKey, file, g.Timestamp, markers, dur, precise: false), false));
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        DashcamDebugLog.Log($"[EventIndex] {g.GroupKey} の解析をスキップ: {ex.GetType().Name}: {ex.Message}");
                    }

                    done++;
                    if (batch.Count >= PublishBatchSize || done == total)
                    {
                        if (batch.Count > 0) publish(batch.ToList());
                        batch.Clear();
                        progress(done, total);
                    }
                    await Task.Yield(); // SDカード等でのI/O競合を避けるため、1ファイルごとに他の処理へ譲る
                }
            }, ct);
        }
    }
}
