using System;
using System.IO;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコモード専用の診断ログ。trace.log（DEBUGビルドのみ・ホットパス禁止方針）とは別に、
    /// Front/Rearペアリング結果や次シーンへ進めなかった理由など、低頻度イベントだけを
    /// "play_error.txt"（出力先は設定で指定、既定はXドライブがあれば X:\temp\VerticalPlayer、無ければexe直下。
    /// Releaseビルドでも常に出力）へ追記する。
    /// </summary>
    public static class DashcamPlayErrorLogger
    {
        private static readonly object _lock = new();
        private static string LogPath => AppLogPaths.GetPath("play_error.txt");

        public static void Log(string message)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // ログ書き込み失敗が再生自体に影響しないようにする
            }
        }
    }
}
