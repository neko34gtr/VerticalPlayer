using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VerticalPlayer.Dashcam
{
    /// <summary>DH5系ドラレコの録画フォルダ種別。PICTURE(静止画)とSYSTEM(NMEA格納専用)は対象外。</summary>
    public enum DashcamEventFolder
    {
        Normal,
        Manual,
        Event,
        Parking
    }

    /// <summary>
    /// [ROOT]/NORMAL/{Timestamp}_front.mp4 等のディレクトリ構造を走査し、
    /// Front/Rear動画とNMEAをタイムスタンプでペアリングする。
    /// 実機のFront/Rearファイル名は完全に同一タイムスタンプにはならず、数秒（実測2〜3秒）
    /// ズレて記録される仕様のため、ファイル名キーの完全一致ではなく、タイムスタンプの
    /// 許容誤差内での最近傍マッチングでペアリングする。
    /// </summary>
    public static class DashcamFileScanner
    {
        // Front/Rearのタイムスタンプが何秒までズレていれば同一録画とみなすか。
        // 実機で確認できた最大ズレはいまのところ50秒程度のため、余裕を見て60秒とする
        // （録画間隔は約2分あるため誤マッチのリスクは無い）。
        private const int PairToleranceSeconds = 60;

        // "..._front.mp4" / "..._Rear.NMEA" のような末尾のfront/rear表記を除去してキー化する
        private static readonly Regex SuffixRegex =
            new(@"^(?<key>.+?)_(?<side>front|rear)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // キーからのタイムスタンプ推定（例: 20250821_095540）。合致しない場合はnull
        private static readonly Regex TimestampRegex =
            new(@"(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})[_-]?(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})",
                RegexOptions.Compiled);

        private static string FolderName(DashcamEventFolder folder) => folder switch
        {
            DashcamEventFolder.Normal => "NORMAL",
            DashcamEventFolder.Manual => "MANUAL",
            DashcamEventFolder.Event => "EVENT",
            DashcamEventFolder.Parking => "PARKING",
            _ => "NORMAL"
        };

        /// <summary>指定した録画フォルダ種別（既定NORMAL）だけを対象に走査する。</summary>
        public static List<DashcamMediaGroup> Scan(string rootDir, DashcamEventFolder folder = DashcamEventFolder.Normal)
        {
            var items = new List<ScannedItem>();

            string sub = FolderName(folder);
            string videoDir = Path.Combine(rootDir, sub);
            string nmeaDir = Path.Combine(rootDir, "SYSTEM", "NMEA", sub);

            if (Directory.Exists(videoDir))
            {
                foreach (var path in Directory.EnumerateFiles(videoDir, "*.mp4", SearchOption.TopDirectoryOnly))
                    TryAddItem(items, path, isVideo: true);
            }

            if (Directory.Exists(nmeaDir))
            {
                foreach (var path in Directory.EnumerateFiles(nmeaDir, "*.NMEA", SearchOption.TopDirectoryOnly))
                    TryAddItem(items, path, isVideo: false);

                // 環境によって拡張子が小文字の場合もケアする
                foreach (var path in Directory.EnumerateFiles(nmeaDir, "*.nmea", SearchOption.TopDirectoryOnly))
                    TryAddItem(items, path, isVideo: false);
            }

            return PairItems(items);
        }

        private sealed class ScannedItem
        {
            public string Path = "";
            public bool IsVideo;
            public bool IsFront;
            public string Key = "";
            public DateTime? Timestamp;
        }

        private sealed class Cluster
        {
            public string Key = "";
            public DateTime Timestamp;
            public List<ScannedItem> Items { get; } = new();
        }

        private static void TryAddItem(List<ScannedItem> items, string path, bool isVideo)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(path);
            var m = SuffixRegex.Match(nameNoExt);
            if (!m.Success)
                return; // front/rear表記が無いファイルは対象外

            string key = m.Groups["key"].Value;
            bool isFront = string.Equals(m.Groups["side"].Value, "front", StringComparison.OrdinalIgnoreCase);

            items.Add(new ScannedItem
            {
                Path = path,
                IsVideo = isVideo,
                IsFront = isFront,
                Key = key,
                Timestamp = TryParseTimestamp(key)
            });
        }

        private static List<DashcamMediaGroup> PairItems(List<ScannedItem> items)
        {
            // タイムスタンプが解析できた項目とできなかった項目を分ける。
            // 解析できなかった項目は最近傍マッチングができないため、従来通り
            // ファイル名キーの完全一致でペアリングする（フォールバック）。
            var withTs = items.Where(i => i.Timestamp.HasValue).ToList();
            var withoutTs = items.Where(i => !i.Timestamp.HasValue).ToList();

            var groups = new List<DashcamMediaGroup>();

            // Front由来（動画+NMEA）・Rear由来をそれぞれキー単位でクラスタ化。
            // 同じFront録画の動画とNMEAはファイル名キーが完全一致するため、
            // この段階ではまだ完全一致でまとめてよい。
            var frontClusters = ClusterByKey(withTs.Where(i => i.IsFront));
            var rearClusters = ClusterByKey(withTs.Where(i => !i.IsFront));

            // Frontを時刻順に並べ、それぞれ許容誤差内で最も時刻が近い未使用のRearを貪欲にマッチングする
            frontClusters.Sort((a, b) => DateTime.Compare(a.Timestamp, b.Timestamp));
            var usedRear = new bool[rearClusters.Count];

            foreach (var front in frontClusters)
            {
                int bestIdx = -1;
                double bestDiffSec = double.MaxValue;
                for (int i = 0; i < rearClusters.Count; i++)
                {
                    if (usedRear[i]) continue;
                    double diffSec = Math.Abs((rearClusters[i].Timestamp - front.Timestamp).TotalSeconds);
                    if (diffSec <= PairToleranceSeconds && diffSec < bestDiffSec)
                    {
                        bestDiffSec = diffSec;
                        bestIdx = i;
                    }
                }

                var group = new DashcamMediaGroup
                {
                    TimestampKey = front.Key,
                    Timestamp = front.Timestamp
                };
                foreach (var item in front.Items)
                    ApplyItem(group, item);

                if (bestIdx >= 0)
                {
                    usedRear[bestIdx] = true;
                    foreach (var item in rearClusters[bestIdx].Items)
                        ApplyItem(group, item);
                    DashcamPlayErrorLogger.Log($"[Pair OK] Front={front.Key} Rear={rearClusters[bestIdx].Key} diff={bestDiffSec:F1}s");
                }
                else
                {
                    double closestAnyDiffSec = double.MaxValue;
                    for (int i = 0; i < rearClusters.Count; i++)
                    {
                        if (usedRear[i]) continue;
                        double d = Math.Abs((rearClusters[i].Timestamp - front.Timestamp).TotalSeconds);
                        if (d < closestAnyDiffSec) closestAnyDiffSec = d;
                    }
                    string detail = closestAnyDiffSec == double.MaxValue
                        ? "候補となるRearが1件もありません"
                        : $"最も近いRearとの差={closestAnyDiffSec:F1}s（許容誤差{PairToleranceSeconds}s超過）";
                    DashcamPlayErrorLogger.Log($"[Pair NG] Front={front.Key} リアなし: {detail}");
                }

                groups.Add(group);
            }

            // どのFrontともマッチしなかったRear（Front無しの単独録画）もグループとして残す
            for (int i = 0; i < rearClusters.Count; i++)
            {
                if (usedRear[i]) continue;
                var rear = rearClusters[i];
                DashcamPlayErrorLogger.Log($"[Pair NG] Rear={rear.Key} に対応するFrontなし（単独扱い）");

                var group = new DashcamMediaGroup
                {
                    TimestampKey = rear.Key,
                    Timestamp = rear.Timestamp
                };
                foreach (var item in rear.Items)
                    ApplyItem(group, item);
                groups.Add(group);
            }

            // タイムスタンプ解析不能な項目は、従来通りキー完全一致でペアリングする
            var fallback = new Dictionary<string, DashcamMediaGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in withoutTs)
            {
                if (!fallback.TryGetValue(item.Key, out var group))
                {
                    group = new DashcamMediaGroup { TimestampKey = item.Key, Timestamp = null };
                    fallback[item.Key] = group;
                }
                ApplyItem(group, item);
            }
            groups.AddRange(fallback.Values);

            groups.Sort((a, b) => string.CompareOrdinal(a.TimestampKey, b.TimestampKey));
            return groups;
        }

        private static List<Cluster> ClusterByKey(IEnumerable<ScannedItem> items)
        {
            var dict = new Dictionary<string, Cluster>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!dict.TryGetValue(item.Key, out var c))
                {
                    c = new Cluster { Key = item.Key, Timestamp = item.Timestamp!.Value };
                    dict[item.Key] = c;
                }
                c.Items.Add(item);
            }
            return dict.Values.ToList();
        }

        private static void ApplyItem(DashcamMediaGroup group, ScannedItem item)
        {
            if (item.IsVideo)
            {
                if (item.IsFront) group.FrontVideoPath = item.Path;
                else group.RearVideoPath = item.Path;
            }
            else
            {
                if (item.IsFront) group.FrontNmeaPath = item.Path;
                else group.RearNmeaPath = item.Path;
            }
        }

        private static DateTime? TryParseTimestamp(string key)
        {
            var m = TimestampRegex.Match(key);
            if (!m.Success)
                return null;

            try
            {
                int y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
                int mo = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture);
                int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
                int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                int mi = int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture);
                int s = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
                return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
            }
            catch
            {
                return null;
            }
        }
    }
}
