using System;
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
