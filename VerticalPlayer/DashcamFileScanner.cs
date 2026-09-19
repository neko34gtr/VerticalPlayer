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
    /// 実機のFront/Rearファイル名は完全に同一タイムスタンプにはならず、記録が非連動のため
    /// 数秒〜数十秒単位でズレることがある仕様のため、ファイル名キーの完全一致ではなく、
    /// タイムスタンプの許容誤差内での最近傍マッチングでペアリングする。
    ///
    /// SDカードのファイル構造は書き込まれた後は基本的に変化しない(静的)ため、一度計算した
    /// Front→Rearの組み合わせはDashcamPairDatabase(SD_DBLIST.DB)へ保存し、次回以降の同じ
    /// ドライブ/フォルダのスキャンでは、DBに無い(＝新規追加された)Frontファイルだけを対象に
    /// 最近傍探索を行う（既知のFrontは探索を省略し、DBの記録をそのまま使う）。
    /// </summary>
    public static class DashcamFileScanner
    {
        // Front/Rearのタイムスタンプが何秒までズレていれば同一録画とみなすか。
        // 実機で確認できた最大ズレは50秒程度のため、余裕を見て60秒とする
        // （録画間隔は約2分あるため誤マッチのリスクは無い）。
        private const int PairToleranceSeconds = 60;

        // "..._front.mp4" / "..._Rear.NMEA" のような末尾のfront/rear表記を除去してキー化する
        private static readonly Regex SuffixRegex =
            new(@"^(?<key>.+?)_(?<side>front|rear)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // キーからのタイムスタンプ推定（例: 20250821_095540）。合致しない場合はnull
        private static readonly Regex TimestampRegex =
            new(@"(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})[_-]?(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})",
                RegexOptions.Compiled);

        public static string FolderName(DashcamEventFolder folder) => folder switch
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

            return PairItems(items, rootDir, sub);
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

        /// <summary>クラスタの代表ファイル名（DBのキーに使う。動画があれば動画、無ければNMEA）。</summary>
        private static string GetPrimaryFileName(Cluster c)
        {
            var video = c.Items.FirstOrDefault(i => i.IsVideo);
            string path = video?.Path ?? c.Items[0].Path;
            return Path.GetFileName(path);
        }

        private static string? TryGetVolumeLabel(string driveRoot)
        {
            try
            {
                var di = new DriveInfo(driveRoot);
                return di.IsReady ? di.VolumeLabel : null;
            }
            catch
            {
                return null; // ドライブレターでない任意フォルダパスの場合はここに来る（想定内）
            }
        }

        private static List<DashcamMediaGroup> PairItems(List<ScannedItem> items, string driveRoot, string folderInfo)
        {
            // タイムスタンプが解析できた項目とできなかった項目を分ける。
            // 解析できなかった項目は最近傍マッチングができないため、従来通り
            // ファイル名キーの完全一致でペアリングする（フォールバック。DB対象外）。
            var withTs = items.Where(i => i.Timestamp.HasValue).ToList();
            var withoutTs = items.Where(i => !i.Timestamp.HasValue).ToList();

            var groups = new List<DashcamMediaGroup>();

            // Front由来（動画+NMEA）・Rear由来をそれぞれキー単位でクラスタ化。
            // 同じFront録画の動画とNMEAはファイル名キーが完全一致するため、
            // この段階ではまだ完全一致でまとめてよい。
            var frontClusters = ClusterByKey(withTs.Where(i => i.IsFront));
            var rearClusters = ClusterByKey(withTs.Where(i => !i.IsFront));
            frontClusters.Sort((a, b) => DateTime.Compare(a.Timestamp, b.Timestamp));

            var rearByFileName = new Dictionary<string, Cluster>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rearClusters)
                rearByFileName[GetPrimaryFileName(r)] = r;

            string? volumeLabel = TryGetVolumeLabel(driveRoot);
            var known = DashcamPairDatabase.LoadMapping(driveRoot, folderInfo);
            var usedRearFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var newlyComputed = new List<(string front, string? rear)>();

            foreach (var front in frontClusters)
            {
                string frontFileName = GetPrimaryFileName(front);

                var group = new DashcamMediaGroup
                {
                    TimestampKey = front.Key,
                    Timestamp = front.Timestamp
                };
                foreach (var item in front.Items)
                    ApplyItem(group, item);

                if (known.TryGetValue(frontFileName, out var knownRearFileName))
                {
                    // DB既知: 記録済みのRearファイル名が今回のスキャンにも実在すればそのまま使う
                    // （最近傍探索を省略）。knownがnull、またはファイルが実際には消えている場合は
                    // Rear無しのまま（再探索はしない＝DBの当該行を消せば次回また探索される）。
                    if (knownRearFileName != null
                        && rearByFileName.TryGetValue(knownRearFileName, out var rearCluster)
                        && !usedRearFileNames.Contains(knownRearFileName))
                    {
                        usedRearFileNames.Add(knownRearFileName);
                        foreach (var item in rearCluster.Items)
                            ApplyItem(group, item);
                    }
                }
                else
                {
                    // DB未登録のFront（新規ファイル）だけ、従来通りタイムスタンプ最近傍で探索する
                    Cluster? bestRear = null;
                    double bestDiffSec = double.MaxValue;
                    foreach (var r in rearClusters)
                    {
                        string rFileName = GetPrimaryFileName(r);
                        if (usedRearFileNames.Contains(rFileName)) continue;
                        double diffSec = Math.Abs((r.Timestamp - front.Timestamp).TotalSeconds);
                        if (diffSec <= PairToleranceSeconds && diffSec < bestDiffSec)
                        {
                            bestDiffSec = diffSec;
                            bestRear = r;
                        }
                    }

                    if (bestRear != null)
                    {
                        string bestRearFileName = GetPrimaryFileName(bestRear);
                        usedRearFileNames.Add(bestRearFileName);
                        foreach (var item in bestRear.Items)
                            ApplyItem(group, item);
                        newlyComputed.Add((frontFileName, bestRearFileName));
                        DashcamPlayErrorLogger.Log($"[Pair OK] Front={front.Key} Rear={bestRear.Key} diff={bestDiffSec:F1}s");
                    }
                    else
                    {
                        double closestAnyDiffSec = double.MaxValue;
                        foreach (var r in rearClusters)
                        {
                            string rFileName = GetPrimaryFileName(r);
                            if (usedRearFileNames.Contains(rFileName)) continue;
                            double d = Math.Abs((r.Timestamp - front.Timestamp).TotalSeconds);
                            if (d < closestAnyDiffSec) closestAnyDiffSec = d;
                        }
                        string detail = closestAnyDiffSec == double.MaxValue
                            ? "候補となるRearが1件もありません"
                            : $"最も近いRearとの差={closestAnyDiffSec:F1}s（許容誤差{PairToleranceSeconds}s超過）";
                        //DashcamPlayErrorLogger.Log($"[Pair NG] Front={front.Key} リアなし: {detail}");
                        newlyComputed.Add((frontFileName, null));
                    }
                }

                groups.Add(group);
            }

            if (newlyComputed.Count > 0)
                DashcamPairDatabase.SaveMappingBatch(driveRoot, volumeLabel, folderInfo, newlyComputed);

            // どのFrontともマッチしなかったRear（Front無しの単独録画）もグループとして残す
            // （DBはFront軸のキー設計のため、こちらはDB非対象）
            foreach (var r in rearClusters)
            {
                string rFileName = GetPrimaryFileName(r);
                if (usedRearFileNames.Contains(rFileName)) continue;
                //DashcamPlayErrorLogger.Log($"[Pair NG] Rear={r.Key} に対応するFrontなし（単独扱い）");

                var group = new DashcamMediaGroup
                {
                    TimestampKey = r.Key,
                    Timestamp = r.Timestamp
                };
                foreach (var item in r.Items)
                    ApplyItem(group, item);
                groups.Add(group);
            }

            // タイムスタンプ解析不能な項目は、従来通りキー完全一致でペアリングする（DB対象外）
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
