using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// Front/Rearのペアリング結果を保存するDB(SD_DBLIST.DB)。
    /// SDカードのファイル構造は書き込まれた後は基本的に変化しない(静的)という前提のもと、
    /// 一度計算したFront→Rearの組み合わせをここに保存し、次回以降の同じドライブ/フォルダの
    /// スキャンでは（新規追加されたファイル分を除いて）タイムスタンプ最近傍探索を省略する。
    /// NMEAはFront自身のファイル名と1:1のキーで既に同期が取れているため対象外。
    ///
    /// キー設計: No(自動採番・一意)は行の識別用（GUIでの編集対象特定に使う）。
    /// 実際の検索・重複防止キーはUNIQUE(DriveRoot, FolderInfo, FrontFileName)。
    /// DriveRootはドライブレター等(例: "E:\")または任意フォルダのフルパス。SDカードの
    /// 抜き差しでドライブレターが変わると別レコード扱いになる点は既知の制約（データが
    /// 壊れるわけではなく、その場合は単に再計算されて新しい行が増えるだけ）。
    ///
    /// DashcamThumbnailCacheと同じMicrosoft.Data.Sqliteを使用。
    /// </summary>
    public static class DashcamPairDatabase
    {
        private static readonly object InitLock = new();
        private static bool _initialized;
        private static string DbPath => Path.Combine(AppContext.BaseDirectory, "SD_DBLIST.DB");

        public sealed class PairRow
        {
            public long No;
            public string DriveRoot = "";
            public string? VolumeLabel;
            public string FolderInfo = "";
            public string FrontFileName = "";
            public string? RearFileName;
            public string UpdatedAt = "";
        }

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (InitLock)
            {
                if (_initialized) return;
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS PairMap (
                        No INTEGER PRIMARY KEY AUTOINCREMENT,
                        DriveRoot TEXT NOT NULL,
                        VolumeLabel TEXT,
                        FolderInfo TEXT NOT NULL,
                        FrontFileName TEXT NOT NULL,
                        RearFileName TEXT,
                        UpdatedAt TEXT NOT NULL,
                        UNIQUE(DriveRoot, FolderInfo, FrontFileName)
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
        /// 指定ドライブ/フォルダの既知マッピングを取得する。
        /// キー=FrontFileName、値=RearFileName（nullは「Rear無しと判定済み」を意味し、
        /// 辞書にキー自体が無い場合は「まだ未計算」を意味する）。
        /// </summary>
        public static Dictionary<string, string?> LoadMapping(string driveRoot, string folderInfo)
        {
            var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT FrontFileName, RearFileName FROM PairMap WHERE DriveRoot = $d AND FolderInfo = $f;";
                cmd.Parameters.AddWithValue("$d", driveRoot);
                cmd.Parameters.AddWithValue("$f", folderInfo);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string front = reader.GetString(0);
                    string? rear = reader.IsDBNull(1) ? null : reader.GetString(1);
                    result[front] = rear;
                }
            }
            catch
            {
                // 読めなければ「全部未計算」として扱う＝従来通りのライブ探索にフォールバックするだけ
            }
            return result;
        }

        /// <summary>複数件をまとめてUPSERTする（スキャン直後の新規分保存用。トランザクションでまとめて高速化）。</summary>
        public static void SaveMappingBatch(string driveRoot, string? volumeLabel, string folderInfo, IEnumerable<(string front, string? rear)> items)
        {
            try
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO PairMap (DriveRoot, VolumeLabel, FolderInfo, FrontFileName, RearFileName, UpdatedAt)
                    VALUES ($d, $v, $f, $ff, $rf, $u)
                    ON CONFLICT(DriveRoot, FolderInfo, FrontFileName)
                    DO UPDATE SET RearFileName = excluded.RearFileName, VolumeLabel = excluded.VolumeLabel, UpdatedAt = excluded.UpdatedAt;";
                var pd = cmd.Parameters.Add("$d", SqliteType.Text);
                var pv = cmd.Parameters.Add("$v", SqliteType.Text);
                var pf = cmd.Parameters.Add("$f", SqliteType.Text);
                var pff = cmd.Parameters.Add("$ff", SqliteType.Text);
                var prf = cmd.Parameters.Add("$rf", SqliteType.Text);
                var pu = cmd.Parameters.Add("$u", SqliteType.Text);
                string now = DateTime.UtcNow.ToString("O");

                foreach (var (front, rear) in items)
                {
                    pd.Value = driveRoot;
                    pv.Value = (object?)volumeLabel ?? DBNull.Value;
                    pf.Value = folderInfo;
                    pff.Value = front;
                    prf.Value = (object?)rear ?? DBNull.Value;
                    pu.Value = now;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch
            {
                // 保存に失敗しても再生自体には影響しない（次回また探索し直すだけ）
            }
        }

        /// <summary>GUI表示用に、指定ドライブ/フォルダの全行を取得する。</summary>
        public static List<PairRow> LoadRows(string driveRoot, string folderInfo)
        {
            var rows = new List<PairRow>();
            try
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT No, DriveRoot, VolumeLabel, FolderInfo, FrontFileName, RearFileName, UpdatedAt FROM PairMap WHERE DriveRoot = $d AND FolderInfo = $f ORDER BY FrontFileName;";
                cmd.Parameters.AddWithValue("$d", driveRoot);
                cmd.Parameters.AddWithValue("$f", folderInfo);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new PairRow
                    {
                        No = reader.GetInt64(0),
                        DriveRoot = reader.GetString(1),
                        VolumeLabel = reader.IsDBNull(2) ? null : reader.GetString(2),
                        FolderInfo = reader.GetString(3),
                        FrontFileName = reader.GetString(4),
                        RearFileName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        UpdatedAt = reader.GetString(6)
                    });
                }
            }
            catch
            {
                // 読めなければ空のまま返す
            }
            return rows;
        }

        /// <summary>GUIでの手動編集: 指定Noの行のRearFileNameを更新する。</summary>
        public static void UpdateRearByNo(long no, string? rearFileName)
        {
            try
            {
                EnsureInitialized();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE PairMap SET RearFileName = $rf, UpdatedAt = $u WHERE No = $no;";
                cmd.Parameters.AddWithValue("$rf", (object?)rearFileName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$no", no);
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // 保存に失敗しても致命的ではない
            }
        }
    }
}
