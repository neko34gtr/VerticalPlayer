using System;
using System.Collections.Generic;
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
                EnsureInitialized();

                var ticks = new long[videoPaths.Count];
                System.Threading.Tasks.Parallel.For(0, videoPaths.Count, i =>
                {
                    try { ticks[i] = File.GetLastWriteTimeUtc(videoPaths[i]).Ticks; }
                    catch { ticks[i] = -1; }
                });

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
            }
            catch
            {
                // 途中まで取れた分だけ返す（残りは生成し直すだけなので致命的にしない）
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

                foreach (var (path, data) in items)
                {
                    long ticks = File.GetLastWriteTimeUtc(path).Ticks;
                    delP.Value = path;
                    del.ExecuteNonQuery();
                    insP.Value = path;
                    insT.Value = ticks;
                    insD.Value = data;
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                // 保存に失敗しても再生自体には影響しない（次回また生成し直すだけ）
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
            catch
            {
                // 保存に失敗しても再生自体には影響しないので握りつぶす（次回また生成し直すだけ）
            }
        }
    }
}
