using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VlcLib = LibVLCSharp.Shared.LibVLC;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcFrom = LibVLCSharp.Shared.FromType;

namespace VerticalPlayer
{
    public partial class FullScreenWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly VlcLib _libVlc;
        private readonly VlcPlayer _mp;

        private bool _isPlaying = false;
        private bool _isMuted = false;
        private double _prevVol = 0.7;
        private bool _isDragging = false;
        private double _frameMs = 100;
        private bool _vlcLoaded = false;  // Loaded完了前のイベント誤発火ガード

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        public FullScreenWindow(MainWindow owner, VlcLib libVlc, string filePath,
                                long positionMs, double volume, bool isMuted,
                                float speed, double frameMs, double rotationAngle)
        {
            InitializeComponent();
            _owner = owner;
            _libVlc = libVlc;
            _frameMs = frameMs;
            _isMuted = isMuted;
            _prevVol = volume;

            _mp = new VlcPlayer(_libVlc);
            VideoView.MediaPlayer = _mp;

            VolSlider.Value = volume;
            SpeedLabel.Text = $"{speed:F1}×";

            _mp.Playing += (s, e) => Dispatcher.Invoke(() => { _isPlaying = true; UpdateIcon(); _timer.Start(); });
            _mp.Paused += (s, e) => Dispatcher.Invoke(() => { _isPlaying = false; UpdateIcon(); _timer.Stop(); });
            _mp.EndReached += (s, e) => Dispatcher.Invoke(() => { _isPlaying = false; UpdateIcon(); _timer.Stop(); });

            _timer.Tick += Timer_Tick;
            _osdTimer.Tick += (s, e) => { _osdTimer.Stop(); Osd.Visibility = Visibility.Collapsed; };
            RootGrid.MouseMove += (s, e) => { Osd.Visibility = Visibility.Visible; _osdTimer.Stop(); _osdTimer.Start(); };

            if (rotationAngle != 0)
                VideoView.LayoutTransform = new RotateTransform(rotationAngle);

            Loaded += (s, e) =>
            {
                using var media = new VlcMedia(_libVlc, filePath, VlcFrom.FromPath);
                _mp.Media = media;
                _mp.Volume = (int)Math.Round(volume * 100);
                _mp.Mute = isMuted;
                _mp.SetRate(speed);
                _mp.Play();
                _mp.Time = positionMs;
                _vlcLoaded = true;
                MainWindow.Trace($"FS Loaded: {filePath} pos={positionMs}ms");
            };
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_mp.Length <= 0) return;
            long ms = _mp.Time; long len = _mp.Length;
            if (!_isDragging)
            {
                SeekBar.Maximum = len / 1000.0;
                SeekBar.Value = ms / 1000.0;
                TimeText.Text = $"{Fmt(TimeSpan.FromMilliseconds(ms))} / {Fmt(TimeSpan.FromMilliseconds(len))}";
            }
        }

        private static string Fmt(TimeSpan ts)
            => ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;
        private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!_vlcLoaded) return;
            _mp.Time = (long)(SeekBar.Value * 1000); _isDragging = false;
        }

        private void Seek_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_vlcLoaded || sender is not Slider sl) return;
            double t = sl.Maximum * Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            sl.Value = t; _mp.Time = (long)(t * 1000);
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (!_vlcLoaded) return;
            if (_isPlaying) _mp.Pause(); else _mp.Play();
        }

        private void UpdateIcon()
        {
            string d = _isPlaying ? "M4,3 H8 V17 H4 Z M12,3 H16 V17 H12 Z" : "M5,3 L19,10 L5,17 Z";
            if (PlayIcon is System.Windows.Shapes.Path p) p.Data = Geometry.Parse(d);
        }

        private void Rewind_Click(object sender, RoutedEventArgs e)
        { if (_vlcLoaded) _mp.Time = Math.Max(0, _mp.Time - 10000); }
        private void FastForward_Click(object sender, RoutedEventArgs e)
        { if (_vlcLoaded) _mp.Time = Math.Min(_mp.Length, _mp.Time + 10000); }

        private void FrameStep_Click(object sender, RoutedEventArgs e) => _ = StepFrame(+1);
        private void FrameBack_Click(object sender, RoutedEventArgs e) => _ = StepFrame(-1);

        private async Task StepFrame(int dir)
        {
            if (!_vlcLoaded) return;
            if (_isPlaying) { _mp.Pause(); await Task.Delay(50); }
            _mp.Time = Math.Clamp(_mp.Time + (long)(_frameMs * dir), 0, _mp.Length);
        }

        private void PrevFile_Click(object sender, RoutedEventArgs e) => JumpFile(-1);
        private void NextFile_Click(object sender, RoutedEventArgs e) => JumpFile(+1);

        private void JumpFile(int delta)
        {
            if (!_vlcLoaded) return;
            string? path = _owner.GetAdjacentFile(delta);
            if (path == null) return;
            using var media = new VlcMedia(_libVlc, path, VlcFrom.FromPath);
            _mp.Media = media;
            _mp.Play();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (!_vlcLoaded) return;
            _isMuted = !_isMuted; _mp.Mute = _isMuted;
        }

        private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_vlcLoaded || VolSlider == null) return;
            _prevVol = VolSlider.Value;
            if (!_isMuted) _mp.Volume = (int)Math.Round(_prevVol * 100);
        }

        private void Osd_MouseEnter(object sender, MouseEventArgs e) => _osdTimer.Stop();
        private void Osd_MouseLeave(object sender, MouseEventArgs e) => _osdTimer.Start();

        private void Exit_Click(object sender, RoutedEventArgs e) => ExitFs();

        private void ExitFs()
        {
            MainWindow.Trace("FS ExitFs");
            _timer.Stop(); _osdTimer.Stop();
            long pos = _mp.Time;
            float speed = _mp.Rate;
            double vol = VolSlider.Value;
            bool playing = _isPlaying;
            string? mrl = _mp.Media?.Mrl;
            string? path = null;
            if (mrl != null)
            {
                path = Uri.TryCreate(mrl, UriKind.Absolute, out var u)
                    ? u.LocalPath : mrl;
            }
            _mp.Stop(); _mp.Dispose();
            _owner.ReturnFromFullScreen(path, pos, vol, speed, playing);
            this.Close();
        }

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
                    else _mp.Time = Math.Max(0, _mp.Time - 10000);
                    e.Handled = true; break;
                case Key.Right:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrame(+1);
                    else _mp.Time = Math.Min(_mp.Length, _mp.Time + 10000);
                    e.Handled = true; break;
                case Key.Up:
                    VolSlider.Value = Math.Min(VolSlider.Value + 0.05, 1.0); e.Handled = true; break;
                case Key.Down:
                    VolSlider.Value = Math.Max(VolSlider.Value - 0.05, 0.0); e.Handled = true; break;
            }
        }
    }
}
