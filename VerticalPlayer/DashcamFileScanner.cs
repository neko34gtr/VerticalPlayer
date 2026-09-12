using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// [ROOT]/NORMAL/{Timestamp}_front.mp4 等のディレクトリ構造を走査し、
    /// Front/Rear動画とNMEAをタイムスタンプキーで自動ペアリングする。
    /// </summary>
    public static class DashcamFileScanner
    {
        // "..._front.mp4" / "..._Rear.NMEA" のような末尾のfront/rear表記を除去してキー化する
        private static readonly Regex SuffixRegex =
            new(@"^(?<key>.+?)_(?<side>front|rear)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // キーからのタイムスタンプ推定（例: 20250821_095540）。合致しない場合はnull
        private static readonly Regex TimestampRegex =
            new(@"(?<y>\d{4})(?<mo>\d{2})(?<d>\d{2})[_-]?(?<h>\d{2})(?<mi>\d{2})(?<s>\d{2})",
                RegexOptions.Compiled);

        public static List<DashcamMediaGroup> Scan(string rootDir)
        {
            var groups = new Dictionary<string, DashcamMediaGroup>(StringComparer.OrdinalIgnoreCase);

            string videoDir = Path.Combine(rootDir, "NORMAL");
            string nmeaDir = Path.Combine(rootDir, "SYSTEM", "NMEA", "NORMAL");

            if (Directory.Exists(videoDir))
            {
                foreach (var path in Directory.EnumerateFiles(videoDir, "*.mp4", SearchOption.TopDirectoryOnly))
                    Assign(groups, path, isVideo: true);
            }

            if (Directory.Exists(nmeaDir))
            {
                foreach (var path in Directory.EnumerateFiles(nmeaDir, "*.NMEA", SearchOption.TopDirectoryOnly))
                    Assign(groups, path, isVideo: false);

                // 環境によって拡張子が小文字の場合もケアする
                foreach (var path in Directory.EnumerateFiles(nmeaDir, "*.nmea", SearchOption.TopDirectoryOnly))
                    Assign(groups, path, isVideo: false);
            }

            var result = new List<DashcamMediaGroup>(groups.Values);
            result.Sort((a, b) => string.CompareOrdinal(a.TimestampKey, b.TimestampKey));
            return result;
        }

        private static void Assign(Dictionary<string, DashcamMediaGroup> groups, string path, bool isVideo)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(path);
            var m = SuffixRegex.Match(nameNoExt);
            if (!m.Success)
                return; // front/rear表記が無いファイルは対象外

            string key = m.Groups["key"].Value;
            bool isFront = string.Equals(m.Groups["side"].Value, "front", StringComparison.OrdinalIgnoreCase);

            if (!groups.TryGetValue(key, out var group))
            {
                group = new DashcamMediaGroup
                {
                    TimestampKey = key,
                    Timestamp = TryParseTimestamp(key)
                };
                groups[key] = group;
            }

            if (isVideo)
            {
                if (isFront) group.FrontVideoPath = path;
                else group.RearVideoPath = path;
            }
            else
            {
                if (isFront) group.FrontNmeaPath = path;
                else group.RearNmeaPath = path;
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
