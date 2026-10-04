using System;
using System.Diagnostics;
using System.IO;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコモードのデバッグ用ログ（debug.log）。Debugビルド専用。
    /// Releaseビルドでは [Conditional("DEBUG")] により呼び出し（引数の評価を含む）ごとコンパイル時に除去される。
    /// 本当のエラー記録は DashcamPlayErrorLogger（play_error.txt）側に残す。
    /// 出力先は AppLogPaths（trace.log と同じ場所）。プログラム開始後の最初の書き込み時に1度だけ内容をクリアする。
    /// </summary>
    internal static class DashcamDebugLog
    {
#if DEBUG
        private static readonly object _lock = new();
        private static bool _initialized;
        private static string? _path;
#endif

        [Conditional("DEBUG")]
        public static void Log(string message)
        {
#if DEBUG
            try
            {
                lock (_lock)
                {
                    if (!_initialized)
                    {
                        _initialized = true;
                        _path = AppLogPaths.GetPath("debug.log");
                        File.WriteAllText(_path, string.Empty); // 起動後の初回にクリア（trace.logと同様）
                    }
                    if (_path == null) return;
                    File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // デバッグログの失敗でアプリを止めない
            }
#endif
        }
    }
}
