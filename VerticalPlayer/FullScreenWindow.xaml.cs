using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VerticalPlayer
{
    public partial class FullScreenWindow : Window
    {
        private readonly MainWindow _owner;
        private bool _isPlaying = false;
        private bool _isMuted = false;
        private double _prevVol = 0.7;
        private bool _isDragging = false;
        private double _frameMs = 100;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        // ── コンストラクタ ──
        public FullScreenWindow(MainWindow owner, Uri source, TimeSpan position,
                                double volume, bool isMuted, double speed,
                                double frameMs, double rotationAngle)
        {
            InitializeComponent();
            _owner = owner;
            _frameMs = frameMs;
            _isMuted = isMuted;
            _prevVol = volume;

            VolSlider.Value = volume;
            SpeedLabel.Text = $"{speed:F1}×";
            Player.Volume = isMuted ? 0 : volume;
            Player.SpeedRatio = speed;
            Player.Source = source;

            if (rotationAngle != 0)
                Player.LayoutTransform = new RotateTransform(rotationAngle);
            Player.DisplayRotation = rotationAngle;

            _timer.Tick += Timer_Tick;
            _osdTimer.Tick += (s, e) => { _osdTimer.Stop(); Osd.Visibility = Visibility.Collapsed; };

            Loaded += (s, e) =>
            {
                Player.Play();
                Player.Position = position;
                _isPlaying = true;
                UpdateIcon();
                _timer.Start();
                Trace("FullScreenWindow Loaded: play started");
            };
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            Trace($"FS MediaOpened: {Player.NaturalVideoWidth}x{Player.NaturalVideoHeight}");
            if (Player.NaturalDuration.HasTimeSpan)
                SeekBar.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            _isPlaying = false; UpdateIcon(); _timer.Stop();
        }

        // ── タイマー ──
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging || !Player.NaturalDuration.HasTimeSpan) return;
            double total = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (total <= 0) return;
            SeekBar.Maximum = total;
            SeekBar.Value = Player.Position.TotalSeconds;
            TimeText.Text = $"{Fmt(Player.Position)} / {Fmt(Player.NaturalDuration.TimeSpan)}";
        }

        private static string Fmt(TimeSpan ts)
            => ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        // ── シーク ──
        private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;
        private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
        { Player.Position = TimeSpan.FromSeconds(SeekBar.Value); _isDragging = false; }

        private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !Player.NaturalDuration.HasTimeSpan) return;
            double t = sl.Maximum * Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            sl.Value = t;
            Player.Position = TimeSpan.FromSeconds(t);
        }

        // ── 再生 ──
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying) { Player.Pause(); _isPlaying = false; _timer.Stop(); }
            else { Player.Play(); _isPlaying = true; _timer.Start(); }
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            string d = _isPlaying ? "M4,3 H8 V17 H4 Z M12,3 H16 V17 H12 Z" : "M5,3 L19,10 L5,17 Z";
            if (PlayIcon is System.Windows.Shapes.Path p) p.Data = Geometry.Parse(d);
        }

        private void Rewind_Click(object sender, RoutedEventArgs e)
            => Player.Position -= TimeSpan.FromSeconds(10);
        private void FastForward_Click(object sender, RoutedEventArgs e)
            => Player.Position += TimeSpan.FromSeconds(10);

        // ── コマ送り ──
        private void FrameStep_Click(object sender, RoutedEventArgs e) => _ = StepFrame(+1);
        private void FrameBack_Click(object sender, RoutedEventArgs e) => _ = StepFrame(-1);

        private async Task StepFrame(int dir)
        {
            if (_isPlaying) { Player.Pause(); _isPlaying = false; UpdateIcon(); _timer.Stop(); }
            var t = Player.Position + TimeSpan.FromMilliseconds(_frameMs * dir);
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (Player.NaturalDuration.HasTimeSpan && t > Player.NaturalDuration.TimeSpan)
                t = Player.NaturalDuration.TimeSpan;
            Player.Play();
            Player.Position = t;
            await Task.Delay(80);
            Player.Pause();
        }

        // ── 前/次ファイル ──
        private void PrevFile_Click(object sender, RoutedEventArgs e) => JumpFile(-1);
        private void NextFile_Click(object sender, RoutedEventArgs e) => JumpFile(+1);

        private void JumpFile(int delta)
        {
            string? path = _owner.GetAdjacentFile(delta);
            if (path == null) return;
            Player.Source = new Uri(path);
            Player.Play();
            _isPlaying = true;
            UpdateIcon();
            _timer.Start();
        }

        // ── 音量 ──
        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            Player.Volume = _isMuted ? 0 : _prevVol;
        }

        private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _prevVol = VolSlider.Value;
            if (!_isMuted) Player.Volume = _prevVol;
        }

        // ── OSD ──
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            Osd.Visibility = Visibility.Visible;
            _osdTimer.Stop();
            _osdTimer.Start();
        }

        // ── 全画面解除 ──
        private void Exit_Click(object sender, RoutedEventArgs e) => ExitFs();

        private void ExitFs()
        {
            Trace("FullScreenWindow: ExitFs");
            _timer.Stop(); _osdTimer.Stop();
            _owner.ReturnFromFullScreen(
                Player.Source, Player.Position,
                Player.Volume, Player.SpeedRatio, _isPlaying);
            this.Close();
        }

        // ── キーボード ──
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                case Key.F11:
                    ExitFs(); e.Handled = true; break;
                case Key.Space:
                    PlayPause_Click(sender, new RoutedEventArgs()); e.Handled = true; break;
                case Key.Left:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrame(-1);
                    else Player.Position -= TimeSpan.FromSeconds(10);
                    e.Handled = true; break;
                case Key.Right:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrame(+1);
                    else Player.Position += TimeSpan.FromSeconds(10);
                    e.Handled = true; break;
                case Key.Up:
                    VolSlider.Value = Math.Min(VolSlider.Value + 0.05, 1.0); e.Handled = true; break;
                case Key.Down:
                    VolSlider.Value = Math.Max(VolSlider.Value - 0.05, 0.0); e.Handled = true; break;
            }
        }

        private static void Trace(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }
    }
}
