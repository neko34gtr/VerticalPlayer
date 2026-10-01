using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// サムネイル画像(PNGバイト列)をSQLiteへキャッシュする。
    /// キーは「ファイルパス＋最終更新日時」。動画ファイルが差し替わって更新日時が変われば
    /// 自動的にキャッシュミス扱いになり、再生成される。
    ///
    /// 前提: NuGetパッケージ Microsoft.Data.Sqlite の参照が必要（.csproj未確認のため、
    /// こちらで追加してください）。
    /// </summary>
    public static class DashcamThumbnailCache
    {
        private static readonly object InitLock = new();
        private static bool _initialized;
        private static string DbPath => Path.Combine(AppContext.BaseDirectory, "DashcamThumbnails.sqlite");

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitLock)
            {
                if (_initialized) return;
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Thumbnails (
                        Path TEXT NOT NULL,
                        LastWriteTicks INTEGER NOT NULL,
                        ImageData BLOB NOT NULL,
                        PRIMARY KEY (Path, LastWriteTicks)
                    );";
                cmd.ExecuteNonQuery();
                _initialized = true;
            }
        }

        private static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }

        /// <summary>
        /// 多数ファイルの更新日時(UTC Ticks)をまとめて取得する。戻り値は入力と同じ並び（取得できなければ-1）。
        /// ファイルごとに File.GetLastWriteTimeUtc を呼ぶと、FATのSDカード＋カードリーダーでは
        /// 1件ごとにディレクトリ内を名前で線形探索する問い合わせになり、件数が多いほど極端に遅くなる
        /// （ドライブによって表示までの時間が大きく違う原因の候補）。ここではフォルダごとに1回だけ
        /// 一覧(EnumerateFiles)を読み、そこに含まれる更新日時を使う（ファイル単位の追加I/Oなし）。
        /// </summary>
        private static long[] GetLastWriteTicksMany(IReadOnlyList<string> paths)
        {
            var ticks = new long[paths.Count];
            Array.Fill(ticks, -1L);

            var byDir = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Count; i++)
            {
                string? dir = Path.GetDirectoryName(paths[i]);
                if (string.IsNullOrEmpty(dir)) continue;
                if (!byDir.TryGetValue(dir, out var list)) byDir[dir] = list = new List<int>();
                list.Add(i);
            }

            foreach (var (dir, indexes) in byDir)
            {
                var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var fi in new DirectoryInfo(dir).EnumerateFiles())
                        map[fi.Name] = fi.LastWriteTimeUtc.Ticks;
                }
                catch
                {
                    // 列挙できなければ下の個別取得へフォールバックする
                }

                foreach (int i in indexes)
                {
                    if (map.TryGetValue(Path.GetFileName(paths[i]), out long t)) ticks[i] = t;
                    else
                    {
                        try { ticks[i] = File.GetLastWriteTimeUtc(paths[i]).Ticks; }
                        catch { ticks[i] = -1; }
                    }
                }
            }
            return ticks;
        }

        /// <summary>キャッシュ済みのPNGバイト列を取得する。無ければnull。</summary>
        public static byte[]? TryGet(string videoPath)
        {
            try
            {
                EnsureInitialized();
                long ticks = File.GetLastWriteTimeUtc(videoPath).Ticks;

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ImageData FROM Thumbnails WHERE Path = $p AND LastWriteTicks = $t LIMIT 1;";
                cmd.Parameters.AddWithValue("$p", videoPath);
                cmd.Parameters.AddWithValue("$t", ticks);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return (byte[])reader["ImageData"];
                return null;
            }
            catch
            {
                return null; // キャッシュが読めなくても生成し直せば良いだけなので致命的にしない
            }
        }

        /// <summary>
        /// 多数のファイル分を1回でまとめて取得する（一覧の初期表示用）。ファイルの更新日時取得は並列で先に済ませ、
        /// DBは1本の接続・1つのコマンドを使い回して引く。キャッシュが無い/更新日時が違うパスは結果に含まれない。
        /// </summary>
        public static Dictionary<string, byte[]> TryGetMany(IReadOnlyList<string> videoPaths)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sw = Stopwatch.StartNew();
                EnsureInitialized();

                var ticks = GetLastWriteTicksMany(videoPaths);
                long statMs = sw.ElapsedMilliseconds;

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ImageData FROM Thumbnails WHERE Path = $p AND LastWriteTicks = $t LIMIT 1;";
                var pp = cmd.Parameters.Add("$p", SqliteType.Text);
                var pt = cmd.Parameters.Add("$t", SqliteType.Integer);

                for (int i = 0; i < videoPaths.Count; i++)
                {
                    if (ticks[i] < 0) continue;
                    pp.Value = videoPaths[i];
                    pt.Value = ticks[i];
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                        result[videoPaths[i]] = (byte[])reader["ImageData"];
                }
                DashcamPlayErrorLogger.Log($"[Thumb] キャッシュ照会 {videoPaths.Count}件: 更新日時取得{statMs}ms + DB照会{sw.ElapsedMilliseconds - statMs}ms（命中{result.Count}件）");
            }
            catch (Exception ex)
            {
                // 途中まで取れた分だけ返す（残りは生成し直すだけなので致命的にしない）。原因調査のためログには残す。
                DashcamPlayErrorLogger.Log($"[Thumb] キャッシュ照会で例外（{result.Count}件まで取得）: {ex.GetType().Name}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 多数のファイル分を1トランザクションでまとめて保存する（1件ずつ保存すると毎回コミット＝ディスク同期が走り遅い）。
        /// キャッシュなので同期は緩めて(synchronous=NORMAL)高速化している。
        /// </summary>
        public static void SaveBatch(IEnumerable<(string path, byte[] data)> items)
        {
            try
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA synchronous=NORMAL;";
                    pragma.ExecuteNonQuery();
                }

                using var tx = conn.BeginTransaction();
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM Thumbnails WHERE Path = $p;";
                var delP = del.Parameters.Add("$p", SqliteType.Text);

                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO Thumbnails (Path, LastWriteTicks, ImageData) VALUES ($p, $t, $d);";
                var insP = ins.Parameters.Add("$p", SqliteType.Text);
                var insT = ins.Parameters.Add("$t", SqliteType.Integer);
                var insD = ins.Parameters.Add("$d", SqliteType.Blob);

                var list = new List<(string path, byte[] data)>(items);
                var ticksAll = GetLastWriteTicksMany(list.ConvertAll(x => x.path));
                for (int k = 0; k < list.Count; k++)
                {
                    var (path, data) = list[k];
                    long ticks = ticksAll[k] >= 0 ? ticksAll[k] : File.GetLastWriteTimeUtc(path).Ticks;
                    delP.Value = path;
                    del.ExecuteNonQuery();
                    insP.Value = path;
                    insT.Value = ticks;
                    insD.Value = data;
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                // 保存に失敗しても再生自体には影響しない（次回また生成し直すだけ）。ただし毎回未命中になる
                // 原因になり得るため、失敗はログに残す。
                DashcamPlayErrorLogger.Log($"[Thumb] DB保存(SaveBatch)で例外: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>生成したPNGバイト列を保存する。同じPath+更新日時の古いレコードは置き換える。</summary>
        public static void Save(string videoPath, byte[] pngBytes)
        {
            try
            {
                EnsureInitialized();
                long ticks = File.GetLastWriteTimeUtc(videoPath).Ticks;

                using var conn = OpenConnection();
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM Thumbnails WHERE Path = $p;";
                del.Parameters.AddWithValue("$p", videoPath);
                del.ExecuteNonQuery();

                using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO Thumbnails (Path, LastWriteTicks, ImageData) VALUES ($p, $t, $d);";
                ins.Parameters.AddWithValue("$p", videoPath);
                ins.Parameters.AddWithValue("$t", ticks);
                ins.Parameters.AddWithValue("$d", pngBytes);
                ins.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // 保存に失敗しても再生自体には影響しない（次回また生成し直すだけ）。失敗はログに残す。
                DashcamPlayErrorLogger.Log($"[Thumb] DB保存(Save)で例外: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
