using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
    /// <summary>
    /// 再生中に一定時間マウス操作が無ければアプリ内のマウスカーソルを隠し、マウスを動かす/クリックする/
    /// ホイールを回すと即座に復帰させる（通常モード/ドラレコモード/フルスクリーン共通、アプリ全体で1つ）。
    /// InputManagerでアプリ内の全ウィンドウの入力を監視するため、個別のウィンドウへの組み込みは不要。
    /// 隠すのは「いずれかのプレイヤーが再生中」「アプリがアクティブ」「マウスキャプチャ中でない
    /// （ドラッグ・ポップアップ・コンボ展開などのUI操作中ではない）」ときだけ。
    /// </summary>
    public static class CursorAutoHider
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        private static DispatcherTimer? _timer;
        private static bool _started;
        private static bool _hidden;
        private static long _lastActivityTick;
        private static POINT _lastPos;
        private static double _delaySec = 2.5;

        /// <summary>監視を開始（既に開始済みなら待ち時間だけ更新）。delaySec&lt;=0なら無効（停止）。</summary>
        public static void Start(double delaySec)
        {
            if (delaySec <= 0)
            {
                Stop();
                return;
            }

            _delaySec = delaySec;
            if (_started) return;
            _started = true;

            GetCursorPos(out _lastPos);
            _lastActivityTick = Environment.TickCount64;
            InputManager.Current.PreProcessInput += OnPreProcessInput;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            _timer?.Stop();
            _timer = null;
            Show();
        }

        private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is not MouseEventArgs me) return;

            // カーソルを隠した/レイアウト変化で発生する「位置が変わらない合成MouseMove」を活動とみなさない
            bool moved = false;
            if (GetCursorPos(out var p))
            {
                moved = p.X != _lastPos.X || p.Y != _lastPos.Y;
                _lastPos = p;
            }

            if (moved || me is MouseButtonEventArgs || me is MouseWheelEventArgs)
            {
                _lastActivityTick = Environment.TickCount64;
                Show();
            }
        }

        private static void OnTick(object? sender, EventArgs e)
        {
            bool appActive = IsAppActive();

            if (_hidden)
            {
                // 再生が止まった/アプリが非アクティブになったら復帰させる
                if (!PlaybackPowerGuard.IsAnyPlaying || !appActive) Show();
                return;
            }

            if (!PlaybackPowerGuard.IsAnyPlaying || !appActive) return;
            if (Environment.TickCount64 - _lastActivityTick < (long)(_delaySec * 1000)) return;

            // UI操作中（ドラッグ・ポップアップ・コンボ展開など）は隠さない
            if (Mouse.Captured != null) return;
            if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return;
            // 他のコードがWaitカーソル等でOverrideCursorを使用中なら触らない
            if (Mouse.OverrideCursor != null) return;

            Mouse.OverrideCursor = Cursors.None;
            _hidden = true;
        }

        private static void Show()
        {
            if (!_hidden) return;
            _hidden = false;
            Mouse.OverrideCursor = null;
        }

        private static bool IsAppActive()
        {
            var app = Application.Current;
            if (app == null) return false;
            foreach (Window w in app.Windows)
                if (w.IsActive) return true;
            return false;
        }
    }
}
