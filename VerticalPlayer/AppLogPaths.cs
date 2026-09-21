using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VerticalPlayer
{
    /// <summary>
    /// ログ(trace.log / play_error.txt 等)の出力先フォルダを一元管理する（通常/ドラレコ両モード共通）。
    /// 出力先の決定順:
    ///   1) 設定(AppSettings.LogDirectory)で指定されたフォルダ
    ///   2) Xドライブ(RAMディスク)があれば X:\temp\VerticalPlayer
    ///   3) 実行ファイル直下
    /// 作成・書き込みできないフォルダは飛ばして次の候補へフォールバックする。
    /// 決定結果はキャッシュする（Traceは毎フレーム級に呼ばれるため、呼び出しごとにファイルシステムを
    /// 確認しない）。設定を変更したときは ConfiguredDirectory の代入で再決定される。
    /// </summary>
    public static class AppLogPaths
    {
        /// <summary>Xドライブ(RAMディスク)がある場合の既定の出力先。</summary>
        public const string RamDiskDirectory = @"X:\temp\VerticalPlayer";

        private static readonly object Gate = new();
        private static string _configured = "";
        private static volatile string? _resolved;

        /// <summary>設定で指定された出力先（空＝自動）。代入すると出力先を再決定する。</summary>
        public static string ConfiguredDirectory
        {
            get { lock (Gate) return _configured; }
            set
            {
                lock (Gate)
                {
                    _configured = value?.Trim() ?? "";
                    _resolved = null;
                }
            }
        }

        /// <summary>現在のログ出力先フォルダ（作成・書き込み確認済み）。</summary>
        public static string LogDirectory
        {
            get
            {
                var r = _resolved;
                if (r != null) return r;
                lock (Gate)
                {
                    _resolved ??= Resolve();
                    return _resolved;
                }
            }
        }

        public static string GetPath(string fileName) => Path.Combine(LogDirectory, fileName);

        /// <summary>設定ファイル(JSON)から LogDirectory だけを先読みする。MainWindowのコンストラクタ冒頭
        /// （最初のtrace.log初期化より前）に呼び、起動直後のログも指定した出力先へ書くために使う。</summary>
        public static void LoadFromConfigFile(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.TryGetProperty("LogDirectory", out var el) && el.ValueKind == JsonValueKind.String)
                    ConfiguredDirectory = el.GetString() ?? "";
            }
            catch
            {
                // 読めなければ自動決定のまま
            }
        }

        private static string Resolve()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_configured)) candidates.Add(_configured);
            if (Directory.Exists(@"X:\")) candidates.Add(RamDiskDirectory);
            candidates.Add(AppDomain.CurrentDomain.BaseDirectory);

            foreach (var dir in candidates)
                if (TryPrepare(dir)) return dir;

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static bool TryPrepare(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".vp_write_test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
