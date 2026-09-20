using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// 再生中のOS画面スリープ・システムスリープの抑制（通常モード/ドラレコモード/フルスクリーン共通）。
    /// FfmpegMediaElementのPlay/Pause/Stop/EOF/Unloadedから再生中の要素を登録・解除し、
    /// 1つでも再生中の要素があれば SetThreadExecutionState で抑制、無くなれば解除する。
    /// SetThreadExecutionStateはスレッド単位のため、設定・解除は必ずUIスレッドで行う。
    /// 他アプリやOS全体の電源設定は変更しない。
    /// </summary>
    public static class PlaybackPowerGuard
    {
        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_CONTINUOUS = 0x80000000,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private static readonly object _lock = new();
        private static readonly HashSet<object> _playing = new();
        private static bool _enabled = true;
        private static bool _applied;

        /// <summary>AppSettings.EnableSleepPrevention。falseなら再生中でも抑制しない。</summary>
        public static bool Enabled
        {
            get => _enabled;
            set { _enabled = value; RequestApply(); }
        }

        /// <summary>いずれかのプレイヤーが再生中か（カーソル自動隠蔽の判定にも使う）。</summary>
        public static bool IsAnyPlaying
        {
            get { lock (_lock) return _playing.Count > 0; }
        }

        public static void SetPlaying(object owner, bool playing)
        {
            lock (_lock)
            {
                if (playing) _playing.Add(owner);
                else _playing.Remove(owner);
            }
            RequestApply();
        }

        /// <summary>アプリ終了時に呼ぶ。登録を全て破棄して抑制を解除する。</summary>
        public static void Shutdown()
        {
            lock (_lock) _playing.Clear();
            RequestApply();
        }

        private static void RequestApply()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) Apply();
            else dispatcher.BeginInvoke(new Action(Apply));
        }

        private static void Apply()
        {
            bool desired;
            lock (_lock) desired = _enabled && _playing.Count > 0;
            if (desired == _applied) return;
            _applied = desired;

            if (desired)
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS
                    | EXECUTION_STATE.ES_DISPLAY_REQUIRED
                    | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            else
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }
}
