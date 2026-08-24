using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
using VlcCore = LibVLCSharp.Shared.Core;
using VlcAdj = LibVLCSharp.Shared.VideoAdjustOption;
using VlcFrom = LibVLCSharp.Shared.FromType;

namespace VerticalPlayer
{
    public class PresetSettings
    {
        public string Name { get; set; } = "Preset";
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
    }

    public class AppSettings
    {
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; } = 460;
        public double WindowHeight { get; set; } = 860;
        public bool AlwaysOnTop { get; set; }
        public double Volume { get; set; } = 0.7;
        public bool IsMuted { get; set; }
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool Loop { get; set; }
        public string? LastFilePath { get; set; }
        public double LastPosition { get; set; }
        public bool IsForceVertical { get; set; }
        public double Rotation { get; set; }
        public bool HwAccel { get; set; }
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
        public double ZoomScaleX { get; set; } = 1.0;
        public double ZoomScaleY { get; set; } = 1.0;
        public List<PresetSettings> Presets { get; set; } = new();
    }

    public partial class MainWindow : Window
    {
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "VerticalPlayer.json");

        // ── LibVLC ──
        private VlcLib _libVlc = null!;
        private VlcPlayer _mp = null!;
        private bool _vlcReady = false;

        // ── 状態フラグ ──
        private bool _isDragging = false;
        private bool _isMuted = false;
        private double _prevVolume = 0.7;
        private bool _isPlaying = false;
        private double _currentRotation = 0;

        // ── タイマー ──
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        // ── ファイル管理 ──
        private string _lastFilePath = string.Empty;

        // ── OSDシークドラッグ ──
        private bool _isOsdDragging = false;

        // ── プリセット ──
        private readonly ObservableCollection<PresetSettings> _presets = new();

        // ── コマ送り ──
        private bool _isAutoFraming = false;
        private double _frameIntervalMs = 100;
        private double _prevSpeed = 1.0;

        // ── MediaInfo ──
        private MediaInfoNative? _mediaInfo = null;

        // ── トレース ──
        private static readonly string TracePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "trace.log");

        internal static void Trace(string msg)
        {
            try
            {
                File.AppendAllText(TracePath,
                $"{DateTime.Now:HH:mm:ss.fff} | {msg}{Environment.NewLine}",
                new UTF8Encoding(false));
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────
        // 初期化
        // ─────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            Trace("=== MainWindow() start ===");
            InitializeComponent();
            Trace("InitializeComponent done");

            this.AllowDrop = true;
            this.Drop += Window_Drop;

            PresetList.ItemsSource = _presets;

            _timer.Tick += Timer_Tick;
            _osdTimer.Tick += OsdTimer_Tick;

            VideoArea.MouseMove += VideoArea_MouseMove;
            this.KeyDown += MainWindow_KeyDown;
        }

        // ─────────────────────────────────────────────────────────────────
        // Loaded
        // ─────────────────────────────────────────────────────────────────
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Trace("Window_Loaded: init LibVLC");
            VlcCore.Initialize();
            _libVlc = new VlcLib("--audio-time-stretch", "--no-video-title-show");
            _mp = new VlcPlayer(_libVlc);
            VideoView.MediaPlayer = _mp;
            _vlcReady = true;

            _mp.Playing += (s, ev) => Dispatcher.BeginInvoke(new Action(() => { _isPlaying = true; UpdatePlayIcon(); _timer.Start(); StatusText.Text = ""; }));
            _mp.Paused += (s, ev) => Dispatcher.BeginInvoke(new Action(() => { _isPlaying = false; UpdatePlayIcon(); _timer.Stop(); }));
            _mp.Stopped += (s, ev) => Dispatcher.BeginInvoke(new Action(() => { _isPlaying = false; UpdatePlayIcon(); _timer.Stop(); SeekBar.Value = 0; TimeDisplay.Text = "0:00 / 0:00"; }));
            _mp.EndReached += (s, ev) => Dispatcher.BeginInvoke(new Action(OnMediaEnded), System.Windows.Threading.DispatcherPriority.Background);

            if (File.Exists(ConfigPath))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
                    if (s != null) { RestoreSettings(s); return; }
                }
                catch { }
            }
            CenterOnScreen();
        }

        // ─────────────────────────────────────────────────────────────────
        // 設定復元
        // ─────────────────────────────────────────────────────────────────
        private void RestoreSettings(AppSettings s)
        {
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            this.Width = Math.Clamp(s.WindowWidth, 320, sw);
            this.Height = Math.Clamp(s.WindowHeight, 300, sh);
            this.Left = Math.Clamp(s.WindowLeft, 0, sw - this.Width);
            this.Top = Math.Clamp(s.WindowTop, 0, sh - this.Height);

            VolumeSlider.Value = s.Volume;
            _mp.Volume = (int)Math.Round(s.Volume * 100);
            _isMuted = s.IsMuted;
            if (_isMuted) _mp.Mute = true;
            SpeedSlider.Value = Math.Clamp(s.PlaybackSpeed, 0.1, 4.0);
            LoopCheck.IsChecked = s.Loop;
            ForceVerticalMode.IsChecked = s.IsForceVertical;
            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            this.Topmost = s.AlwaysOnTop;
            _currentRotation = s.Rotation;
            HwAccelCheck.IsChecked = s.HwAccel;
            ContrastSlider.Value = s.Contrast;
            SaturationSlider.Value = s.Saturation;
            GammaSlider.Value = s.Gamma;
            UpdateEffectLabels();

            _presets.Clear();
            foreach (var p in s.Presets) _presets.Add(p);

            if (!string.IsNullOrEmpty(s.LastFilePath) && File.Exists(s.LastFilePath))
                LoadVideo(s.LastFilePath, s.LastPosition);
        }

        // ─────────────────────────────────────────────────────────────────
        // Closing
        // ─────────────────────────────────────────────────────────────────
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _mediaInfo?.Dispose();
            double lastPos = _vlcReady && _mp.Length > 0 ? _mp.Time / 1000.0 : 0;

            var s = new AppSettings
            {
                WindowLeft = this.Left,
                WindowTop = this.Top,
                WindowWidth = this.Width,
                WindowHeight = this.Height,
                AlwaysOnTop = this.Topmost,
                Volume = VolumeSlider.Value,
                IsMuted = _isMuted,
                PlaybackSpeed = SpeedSlider.Value,
                Loop = LoopCheck.IsChecked ?? false,
                LastFilePath = _lastFilePath,
                LastPosition = lastPos,
                IsForceVertical = ForceVerticalMode.IsChecked ?? false,
                Rotation = _currentRotation,
                HwAccel = HwAccelCheck.IsChecked ?? false,
                Contrast = ContrastSlider.Value,
                Saturation = SaturationSlider.Value,
                Gamma = GammaSlider.Value,
                ZoomScaleX = 1.0,
                ZoomScaleY = 1.0,
                Presets = new List<PresetSettings>(_presets),
            };
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            if (_vlcReady) { _mp.Stop(); _mp.Dispose(); _libVlc.Dispose(); }
        }

        // ─────────────────────────────────────────────────────────────────
        // 動画読み込み
        // ─────────────────────────────────────────────────────────────────
        public void LoadVideoFromArg(string path) => LoadVideo(path);

        private double _pendingSeek = 0;

        private void LoadVideo(string path, double seekSeconds = 0)
        {
            if (!_vlcReady) return;
            Trace($"LoadVideo: {path} seek={seekSeconds}");
            try
            {
                _lastFilePath = path;
                _pendingSeek = seekSeconds;

                // 新ファイル読み込み時：特殊再生状態をリセット
                if (_isAutoFraming) StopAutoFrame();
                // 再生速度も通常に戻す（スロー再生を引き継がない）
                if (SpeedSlider.Value < 0.9 || SpeedSlider.Value > 1.1)
                {
                    SpeedSlider.Value = 1.0;
                }

                var media = new VlcMedia(_libVlc, path, VlcFrom.FromPath);
                _mp.Media = media;
                media.Dispose(); // Mediaセット後は即破棄してOK（VLCが内部でコピーを保持する）

                _mp.SetRate((float)Math.Clamp(SpeedSlider.Value, 0.1, 4.0));
                _mp.Volume = (int)Math.Round(VolumeSlider.Value * 100);
                _mp.Mute = _isMuted;
                _mp.Play();

                FileNameText.Text = Path.GetFileName(path);
                DropHint.Visibility = Visibility.Collapsed;

                // MediaInfo を別スレッドで解析
                Task.Run(() =>
                {
                    try
                    {
                        var mi = new MediaInfoNative(path);
                        if (!mi.Success) return;
                        double fps = mi.VideoFrameRate;
                        Dispatcher.Invoke(() =>
                        {
                            _mediaInfo?.Dispose();
                            _mediaInfo = mi;
                            if (fps > 0)
                            {
                                _frameIntervalMs = 1000.0 / fps;
                                Trace($"fps={fps} frameMs={_frameIntervalMs:F2}");
                                UpdateFrameRateCombo(fps);
                            }
                            UpdateVideoInfo();
                            ResizeToVideo(); // 動画サイズに合わせてウィンドウをリサイズ
                        });
                    }
                    catch (Exception ex) { Trace($"MediaInfo error: {ex.Message}"); }
                });

                Trace("LoadVideo: done");
            }
            catch (Exception ex) { Trace($"LoadVideo EXCEPTION: {ex}"); }
        }

        // ─────────────────────────────────────────────────────────────────
        // メディアイベント
        // ─────────────────────────────────────────────────────────────────
        private void OnMediaEnded()
        {
            // EndReached は VLCの内部スレッドから Dispatcher.Invoke で来るが、
            // ここでさらに _mp.Stop()/_mp.Play() を呼ぶとデッドロックする場合がある。
            // BeginInvoke（非同期）で次フレームに遅延させる。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LoopCheck.IsChecked == true)
                {
                    _mp.Stop();
                    _mp.Play();
                }
                else
                {
                    if (!NavigateFile(+1))
                    {
                        _isPlaying = false;
                        UpdatePlayIcon();
                        _timer.Stop();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ─────────────────────────────────────────────────────────────────
        // タイマー
        // ─────────────────────────────────────────────────────────────────
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_vlcReady || _mp.Length <= 0) return;
            long ms = _mp.Time;
            long len = _mp.Length;

            if (_pendingSeek > 0)
            {
                _mp.Time = (long)(_pendingSeek * 1000);
                _pendingSeek = 0;
            }

            double pos = ms / 1000.0;
            double tot = len / 1000.0;
            string ts = $"{Fmt(TimeSpan.FromSeconds(pos))} / {Fmt(TimeSpan.FromSeconds(tot))}";

            if (!_isDragging)
            {
                SeekBar.Maximum = tot;
                SeekBar.Value = pos;
                TimeDisplay.Text = ts;
            }
            if (!_isOsdDragging)
            {
                OsdSeekBar.Maximum = tot;
                OsdSeekBar.Value = pos;
                OsdTimeDisplay.Text = ts;
            }
        }

        private static string Fmt(TimeSpan ts)
            => ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        // ─────────────────────────────────────────────────────────────────
        // 再生コントロール
        // ─────────────────────────────────────────────────────────────────
        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (!_vlcReady) return;
            if (_isPlaying) _mp.Pause(); else _mp.Play();
        }

        private void UpdatePlayIcon()
        {
            string d = _isPlaying ? "M4,3 H8 V17 H4 Z M12,3 H16 V17 H12 Z" : "M5,3 L19,10 L5,17 Z";
            if (PlayIcon is System.Windows.Shapes.Path p1) p1.Data = Geometry.Parse(d);
            if (OsdPlayIcon is System.Windows.Shapes.Path p2) p2.Data = Geometry.Parse(d);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!_vlcReady) return;
            _mp.Stop();
            _timer.Stop();
            _isPlaying = false;
            UpdatePlayIcon();
            SeekBar.Value = 0;
            TimeDisplay.Text = "0:00 / 0:00";
            StatusText.Text = "";
        }

        private void Rewind_Click(object sender, RoutedEventArgs e)
        { if (_vlcReady) _mp.Time = Math.Max(0, _mp.Time - 10000); }

        private void FastForward_Click(object sender, RoutedEventArgs e)
        { if (_vlcReady) _mp.Time = Math.Min(_mp.Length, _mp.Time + 10000); }

        // ─────────────────────────────────────────────────────────────────
        // 音量
        // ─────────────────────────────────────────────────────────────────
        private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_vlcReady || VolumeSlider == null) return;
            if (!_isMuted) _mp.Volume = (int)Math.Round(VolumeSlider.Value * 100);
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (!_vlcReady) return;
            _isMuted = !_isMuted;
            if (_isMuted)
            {
                _prevVolume = VolumeSlider.Value;
                _mp.Mute = true;
            }
            else
            {
                _mp.Mute = false;
                _mp.Volume = (int)Math.Round(_prevVolume * 100);
                VolumeSlider.Value = _prevVolume;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // シークバー
        // ─────────────────────────────────────────────────────────────────
        private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !_vlcReady) return;
            double t = sl.Maximum * Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            sl.Value = t;
            _mp.Time = (long)(t * 1000);
        }

        private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;

        private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_vlcReady) _mp.Time = (long)(SeekBar.Value * 1000);
            _isDragging = false;
        }

        private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !_vlcReady) return;
            double t = sl.Maximum * Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            sl.Value = t;
            _mp.Time = (long)(t * 1000);
        }

        // ─────────────────────────────────────────────────────────────────
        // ドラッグ＆ドロップ
        // ─────────────────────────────────────────────────────────────────
        private void Window_Drop(object sender, DragEventArgs e)
        {
            DragGlow.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files?.Length > 0) LoadVideo(files[0]);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            DragGlow.Visibility = Visibility.Visible;
            e.Handled = true;
        }

        // ─────────────────────────────────────────────────────────────────
        // ファイルを開く
        // ─────────────────────────────────────────────────────────────────
        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "動画ファイル|*.mp4;*.mov;*.avi;*.mkv;*.wmv;*.flv;*.webm;*.m4v|すべてのファイル|*.*",
                Title = "動画ファイルを開く"
            };
            if (dlg.ShowDialog() == true) LoadVideo(dlg.FileName);
        }

        // ─────────────────────────────────────────────────────────────────
        // 回転・ズーム（VideoView.ContentのGridにLayoutTransformを適用）
        // ─────────────────────────────────────────────────────────────────
        private void Rotate_Click(object sender, RoutedEventArgs e) => RotateVideo();

        private void RotateVideo()
        {
            _currentRotation = (_currentRotation + 90) % 360;
            // VideoView.Content 内の VideoArea GridにLayoutTransformを適用
            if (VideoArea != null)
                VideoArea.LayoutTransform = new RotateTransform(_currentRotation);
            ResizeToVideo();
        }

        private void Player_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var st = VideoView.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            double f = e.Delta > 0 ? 1.1 : 0.9;
            st.ScaleX = Math.Clamp(st.ScaleX * f, 0.2, 5.0);
            st.ScaleY = Math.Clamp(st.ScaleY * f, 0.2, 5.0);
            VideoView.RenderTransform = st;
            VideoView.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
            => VideoView.RenderTransform = new ScaleTransform(1, 1);

        // ─────────────────────────────────────────────────────────────────
        // レイアウト（VLCはVideoViewが自動でAspectRatio管理）
        // ─────────────────────────────────────────────────────────────────
        private void Mode_Checked(object sender, RoutedEventArgs e) { }
        private void ApplyLayout() { }

        private void ResizeToVideo()
        {
            if (_mediaInfo == null || !_mediaInfo.Success) return;
            double vw = _mediaInfo.VideoWidth;
            double vh = _mediaInfo.VideoHeight;
            if (vw <= 0 || vh <= 0) return;

            // 90/270度回転時は縦横反転
            bool rotated = (_currentRotation == 90 || _currentRotation == 270);
            double dispW = rotated ? vh : vw;
            double dispH = rotated ? vw : vh;
            double ratio = dispW / dispH;

            // 利用可能な画面領域（タスクバー除く）
            var workArea = SystemParameters.WorkArea;
            // タイトルバーとコントロールパネルの高さを引く
            double titleH = 48;
            double controlH = 180; // コントロールパネル概算
            double maxW = workArea.Width * 0.95;
            double maxH = (workArea.Height - titleH - controlH) * 0.95;

            // 動画の自然サイズを最大として収まる範囲で最大化
            double targetW = Math.Min(vw, maxW);
            double targetH = targetW / ratio;
            if (targetH > maxH)
            {
                targetH = maxH;
                targetW = targetH * ratio;
            }

            this.Width = targetW;
            this.Height = targetW / ratio + titleH + controlH;
            EnsureOnScreen();
            Trace($"ResizeToVideo: {vw}x{vh} rot={_currentRotation} → window {this.Width:F0}x{this.Height:F0}");
        }

        private void EnsureOnScreen()
        {
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            if (this.Width > sw) this.Width = sw;
            if (this.Height > sh) this.Height = sh;
            if (this.Left < 0) this.Left = 0;
            if (this.Top < 0) this.Top = 0;
            if (this.Left + this.Width > sw) this.Left = sw - this.Width;
            if (this.Top + this.Height > sh) this.Top = sh - this.Height;
        }

        private void CenterOnScreen()
        {
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2;
        }

        private void CenterWindow_Click(object sender, RoutedEventArgs e) => CenterOnScreen();

        // ─────────────────────────────────────────────────────────────────
        // 再生速度
        // ─────────────────────────────────────────────────────────────────
        private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_vlcReady || SpeedLabel == null) return;
            double v = Math.Round(SpeedSlider.Value * 10) / 10.0;
            SpeedLabel.Text = $"{v:F1}×";
            if (!_isAutoFraming) _mp.SetRate((float)Math.Clamp(v, 0.1, 4.0));
            Trace($"Speed_Changed: {v}");
        }

        // ─────────────────────────────────────────────────────────────────
        // エフェクト（VLC VideoAdjust）
        // ─────────────────────────────────────────────────────────────────
        private void Effect_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateEffectLabels();
            ApplyVideoAdjust();
        }

        private void UpdateEffectLabels()
        {
            if (ContrastLabel != null) ContrastLabel.Text = $"{ContrastSlider.Value:+0.0;-0.0; 0.0}";
            if (SaturationLabel != null) SaturationLabel.Text = $"{SaturationSlider.Value:+0.0;-0.0; 0.0}";
            if (GammaLabel != null) GammaLabel.Text = $"{GammaSlider.Value:+0.0;-0.0; 0.0}";
        }

        private void ApplyVideoAdjust()
        {
            if (!_vlcReady) return;
            bool any = ContrastSlider.Value != 0 || SaturationSlider.Value != 0 || GammaSlider.Value != 0;
            _mp.SetAdjustInt(VlcAdj.Enable, any ? 1 : 0);
            _mp.SetAdjustFloat(VlcAdj.Contrast, (float)(1.0 + ContrastSlider.Value));
            _mp.SetAdjustFloat(VlcAdj.Saturation, (float)(1.0 + SaturationSlider.Value));
            _mp.SetAdjustFloat(VlcAdj.Gamma, (float)Math.Pow(2.0, -GammaSlider.Value));
        }

        private void ResetEffects_Click(object sender, RoutedEventArgs e)
        {
            ContrastSlider.Value = 0; SaturationSlider.Value = 0; GammaSlider.Value = 0;
            UpdateEffectLabels(); ApplyVideoAdjust();
        }

        // ─────────────────────────────────────────────────────────────────
        // プリセット
        // ─────────────────────────────────────────────────────────────────
        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_presets.Count >= 10) { StatusText.Text = "プリセット上限（10件）に達しています"; return; }
            string name = PresetNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = $"Preset {_presets.Count + 1}";
            _presets.Add(new PresetSettings
            {
                Name = name,
                Contrast = ContrastSlider.Value,
                Saturation = SaturationSlider.Value,
                Gamma = GammaSlider.Value
            });
            StatusText.Text = $"「{name}」を保存しました";
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PresetSettings p)
            {
                ContrastSlider.Value = p.Contrast; SaturationSlider.Value = p.Saturation; GammaSlider.Value = p.Gamma;
                UpdateEffectLabels(); ApplyVideoAdjust();
                StatusText.Text = $"「{p.Name}」を適用しました";
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PresetSettings p)
            { _presets.Remove(p); StatusText.Text = $"「{p.Name}」を削除しました"; }
        }

        // ─────────────────────────────────────────────────────────────────
        // 常に最前面
        // ─────────────────────────────────────────────────────────────────
        private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
            => this.Topmost = AlwaysOnTopCheck.IsChecked ?? false;

        // ─────────────────────────────────────────────────────────────────
        // 動画情報タブ
        // ─────────────────────────────────────────────────────────────────
        private void UpdateVideoInfo()
        {
            if (VideoInfoStack == null) return;
            VideoInfoStack.Children.Clear();

            void Row(string label, string val, bool accent = false)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var t1 = new TextBlock { Text = label, Foreground = (Brush)FindResource("TextMuted"), FontSize = 10 };
                var t2 = new TextBlock
                {
                    Text = val,
                    Foreground = accent ? (Brush)FindResource("AccentCyan") : (Brush)FindResource("TextPrimary"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1);
                g.Children.Add(t1); g.Children.Add(t2);
                VideoInfoStack.Children.Add(g);
            }
            void Sep() => VideoInfoStack.Children.Add(new Border
            { Height = 1, Background = (Brush)FindResource("TextFaint"), Margin = new Thickness(0, 5, 0, 5), Opacity = 0.3 });

            if (string.IsNullOrEmpty(_lastFilePath)) { Row("状態", "未読み込み"); return; }

            Row("ファイル名", Path.GetFileName(_lastFilePath));
            var fi = new FileInfo(_lastFilePath);
            if (fi.Exists) Row("ファイルサイズ", FormatBytes(fi.Length));
            if (_vlcReady && _mp.Length > 0) Row("長さ", Fmt(TimeSpan.FromMilliseconds(_mp.Length)));

            if (_mediaInfo != null && _mediaInfo.Success)
            {
                Row("解像度",
                    $"{_mediaInfo.VideoWidth} × {_mediaInfo.VideoHeight}" +
                    (_mediaInfo.VideoWidth > 0
                        ? $"  ({(_mediaInfo.VideoWidth > _mediaInfo.VideoHeight ? "横型" : "縦型")})" : ""));
                Sep();
                double fps = _mediaInfo.VideoFrameRate;
                Row("フレームレート", fps > 0 ? $"{fps:F3} fps" : "不明", accent: true);
                Row("映像コーデック", string.IsNullOrEmpty(_mediaInfo.VideoCodec) ? "不明" : _mediaInfo.VideoCodec, accent: true);
                long vBr = _mediaInfo.VideoBitRate;
                Row("映像ビットレート", vBr > 0 ? $"{vBr / 1000:N0} kbps" : "不明");
                string ci = string.Join(" / ", new[]{
                    _mediaInfo.VideoColorSpace??"", _mediaInfo.VideoChromaSubsampling??"",
                    _mediaInfo.VideoBitDepth > 0 ? $"{_mediaInfo.VideoBitDepth}bit" : ""}
                    .Where(s => s.Length > 0));
                Row("カラー情報", string.IsNullOrEmpty(ci) ? "不明" : ci);
                if (!string.IsNullOrEmpty(_mediaInfo.VideoHdrFormat)) Row("HDR", _mediaInfo.VideoHdrFormat);
                Sep();
                Row("音声コーデック", string.IsNullOrEmpty(_mediaInfo.AudioCodec) ? "不明" : _mediaInfo.AudioCodec);
                int sr = _mediaInfo.AudioSampleRate;
                Row("サンプルレート", sr > 0 ? $"{sr:N0} Hz" : "不明");
                int ch = _mediaInfo.AudioChannelCount;
                Row("音声チャンネル", ch switch { 1 => "1ch (Mono)", 2 => "2ch (Stereo)", 6 => "5.1ch", 8 => "7.1ch", _ => ch > 0 ? $"{ch}ch" : "不明" });
                long aBr = _mediaInfo.AudioBitRate;
                Row("音声ビットレート", aBr > 0 ? $"{aBr / 1000:N0} kbps" : "不明");
                Sep();
                long totalBr = fi.Exists && _mp.Length > 0
                    ? (long)(fi.Length * 8 / (_mp.Length / 1000.0)) : 0;
                Row("総ビットレート", totalBr > 0 ? $"{totalBr / 1000:N0} kbps" : "不明");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        // ─────────────────────────────────────────────────────────────────
        // サイドパネル
        // ─────────────────────────────────────────────────────────────────
        private void Menu_Click(object sender, RoutedEventArgs e) => TogglePanel();
        private void PanelOverlay_Click(object sender, MouseButtonEventArgs e) => TogglePanel();
        private void TogglePanel()
        {
            bool open = SidePanel.Visibility != Visibility.Visible;
            SidePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            PanelOverlay.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────────────
        // ウィンドウクロム
        // ─────────────────────────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        { if (e.ClickCount == 2) MaxRestore_Click(sender, e); else DragMove(); }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => this.WindowState = WindowState.Minimized;
        private void MaxRestore_Click(object sender, RoutedEventArgs e)
            => this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
        private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

        // ─────────────────────────────────────────────────────────────────
        // キーボードショートカット
        // ─────────────────────────────────────────────────────────────────
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11 ||
                (e.Key == Key.System && e.SystemKey == Key.Enter &&
                 (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt))
            { ToggleFullScreen(); e.Handled = true; return; }

            if (!_vlcReady) return;
            switch (e.Key)
            {
                case Key.Escape: break;
                case Key.Space: TogglePlayPause(); e.Handled = true; break;
                case Key.Left:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrame(-1);
                    else _mp.Time = Math.Max(0, _mp.Time - 10000);
                    e.Handled = true; break;
                case Key.Right:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrame(+1);
                    else _mp.Time = Math.Min(_mp.Length, _mp.Time + 10000);
                    e.Handled = true; break;
                case Key.Up:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                        SpeedSlider.Value = Math.Min(SpeedSlider.Value + 0.1, 4.0);
                    else VolumeSlider.Value = Math.Min(VolumeSlider.Value + 0.05, 1.0);
                    e.Handled = true; break;
                case Key.Down:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                        SpeedSlider.Value = Math.Max(SpeedSlider.Value - 0.1, 0.1);
                    else VolumeSlider.Value = Math.Max(VolumeSlider.Value - 0.05, 0.0);
                    e.Handled = true; break;
                case Key.M: Mute_Click(sender, e); e.Handled = true; break;
                case Key.F: MaxRestore_Click(sender, e); e.Handled = true; break;
                case Key.R: RotateVideo(); e.Handled = true; break;
                case Key.H:
                    // 左右反転（VideoViewのScaleX反転）
                    if (VideoView.RenderTransform is ScaleTransform st2)
                        st2.ScaleX = st2.ScaleX == 1 ? -1 : 1;
                    else
                    {
                        var st3 = new ScaleTransform(-1, 1);
                        VideoView.RenderTransform = st3;
                        VideoView.RenderTransformOrigin = new Point(0.5, 0.5);
                    }
                    e.Handled = true; break;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 全画面（FullScreenWindow）
        // ─────────────────────────────────────────────────────────────────
        private void ToggleFullScreen()
        {
            Trace($"ToggleFullScreen: {_lastFilePath}");
            if (!_vlcReady || string.IsNullOrEmpty(_lastFilePath)) return;
            try
            {
                long pos = _mp.Time;
                float speed = _mp.Rate;
                double vol = VolumeSlider.Value;

                _mp.Pause(); _timer.Stop();

                var fs = new FullScreenWindow(
                    owner: this,
                    libVlc: _libVlc,
                    filePath: _lastFilePath,
                    positionMs: pos,
                    volume: vol,
                    isMuted: _isMuted,
                    speed: speed,
                    frameMs: _frameIntervalMs,
                    rotationAngle: _currentRotation);

                fs.ShowDialog();
                Trace("ToggleFullScreen: FullScreenWindow closed");
            }
            catch (Exception ex) { Trace($"ToggleFullScreen EXCEPTION: {ex}"); }
        }

        public string? GetAdjacentFile(int delta)
        {
            if (string.IsNullOrEmpty(_lastFilePath)) return null;
            string? dir = Path.GetDirectoryName(_lastFilePath);
            if (string.IsNullOrEmpty(dir)) return null;
            string[] exts = { "*.mp4", "*.mkv", "*.avi", "*.wmv", "*.mov", "*.webm", "*.m4v" };
            var files = new List<string>();
            foreach (var ext in exts) files.AddRange(Directory.GetFiles(dir, ext));
            files.Sort();
            int idx = files.FindIndex(f => f.Equals(_lastFilePath, StringComparison.OrdinalIgnoreCase));
            int next = idx + delta;
            return (next >= 0 && next < files.Count) ? files[next] : null;
        }

        public void ReturnFromFullScreen(string? filePath, long posMs, double vol, float speed, bool isPlaying)
        {
            Trace($"ReturnFromFullScreen path={filePath} pos={posMs} playing={isPlaying}");
            if (!string.IsNullOrEmpty(filePath) && !filePath.Equals(_lastFilePath, StringComparison.OrdinalIgnoreCase))
            {
                LoadVideo(filePath); _pendingSeek = posMs / 1000.0; return;
            }
            if (!_vlcReady) return;
            _mp.SetRate(speed);
            _mp.Volume = (int)Math.Round(vol * 100);
            VolumeSlider.Value = vol;
            SpeedSlider.Value = Math.Clamp(speed, 0.1, 4.0);
            _mp.Time = posMs;
            if (isPlaying) _mp.Play();
            else UpdatePlayIcon();
        }

        // ─────────────────────────────────────────────────────────────────
        // OSD
        // ─────────────────────────────────────────────────────────────────
        private void VideoArea_MouseMove(object sender, MouseEventArgs e) { }

        private void OsdTimer_Tick(object? sender, EventArgs e)
        {
            _osdTimer.Stop();
            if (_isOsdDragging) return;
            OsdPanel.Visibility = Visibility.Collapsed;
        }

        private void Osd_MouseEnter(object sender, MouseEventArgs e) => _osdTimer.Stop();
        private void Osd_MouseLeave(object sender, MouseEventArgs e)
        { if (!_isOsdDragging) _osdTimer.Start(); }

        private void OsdSeekBar_DragStarted(object sender, DragStartedEventArgs e) => _isOsdDragging = true;
        private void OsdSeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_vlcReady) _mp.Time = (long)(OsdSeekBar.Value * 1000);
            SeekBar.Value = OsdSeekBar.Value;
            _isOsdDragging = false;
        }

        private void OsdSeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !_vlcReady) return;
            double t = sl.Maximum * Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            sl.Value = t; SeekBar.Value = t;
            _mp.Time = (long)(t * 1000);
        }

        private void OsdVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_vlcReady || VolumeSlider == null || OsdVolumeSlider == null) return;
            VolumeSlider.Value = OsdVolumeSlider.Value;
            if (!_isMuted) _mp.Volume = (int)Math.Round(OsdVolumeSlider.Value * 100);
        }

        // ─────────────────────────────────────────────────────────────────
        // コマ送り
        // ─────────────────────────────────────────────────────────────────
        private void FrameStep_Click(object sender, RoutedEventArgs e) => _ = StepFrame(+1);
        private void FrameBack_Click(object sender, RoutedEventArgs e) => _ = StepFrame(-1);

        private async Task StepFrame(int direction)
        {
            if (!_vlcReady) return;
            Trace($"StepFrame dir={direction}");
            if (_isAutoFraming) StopAutoFrame();
            if (_isPlaying) { _mp.Pause(); await Task.Delay(50); }
            long t = Math.Clamp(_mp.Time + (long)(_frameIntervalMs * direction), 0, _mp.Length);
            _mp.Time = t;
            Trace($"StepFrame done: {t}ms");
        }

        private void AutoFrame_Click(object sender, RoutedEventArgs e)
        { if (_isAutoFraming) StopAutoFrame(); else StartAutoFrame(); }

        private void StartAutoFrame()
        {
            if (!_vlcReady) return;
            Trace($"StartAutoFrame fps={1000 / _frameIntervalMs:F1}");
            _isAutoFraming = true;
            _prevSpeed = _mp.Rate;
            float speed = (float)Math.Clamp(1000.0 / _frameIntervalMs / 30.0, 0.01, 1.0);
            _mp.SetRate(speed);
            SpeedSlider.Value = speed;
            if (!_isPlaying) _mp.Play();
            if (AutoFrameLabel != null) AutoFrameLabel.Foreground = (Brush)FindResource("AccentBlue");
            StatusText.Text = $"スロー {speed:F2}×";
        }

        private void StopAutoFrame()
        {
            if (!_vlcReady) return;
            Trace("StopAutoFrame");
            _isAutoFraming = false;
            float restore = (float)Math.Clamp(_prevSpeed > 0 ? _prevSpeed : 1.0, 0.1, 4.0);
            _mp.SetRate(restore);
            SpeedSlider.Value = restore;
            if (AutoFrameLabel != null) AutoFrameLabel.Foreground = (Brush)FindResource("TextFaint");
            StatusText.Text = "";
        }

        private void FrameRate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (FrameRateCombo?.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Tag?.ToString(), out double fps) && fps > 0)
            {
                _frameIntervalMs = 1000.0 / fps;
                Trace($"FrameRate_Changed: {fps}fps");
                if (_isAutoFraming) StartAutoFrame();
            }
        }

        private void UpdateFrameRateCombo(double fps)
        {
            if (FrameRateCombo == null) return;
            double[] cands = { 1, 2, 5, 10, 15, 24, 30 };
            double best = cands.OrderBy(c => Math.Abs(c - fps)).First();
            foreach (ComboBoxItem item in FrameRateCombo.Items)
                if (double.TryParse(item.Tag?.ToString(), out double v) && v == best)
                { FrameRateCombo.SelectedItem = item; break; }
        }

        // ─────────────────────────────────────────────────────────────────
        // 前後ファイル
        // ─────────────────────────────────────────────────────────────────
        private void NextFile_Click(object sender, RoutedEventArgs e)
        { Trace("NextFile_Click"); if (!NavigateFile(+1)) StatusText.Text = "次のファイルはありません"; }

        private void PrevFile_Click(object sender, RoutedEventArgs e)
        { Trace("PrevFile_Click"); if (!NavigateFile(-1)) StatusText.Text = "前のファイルはありません"; }

        private bool NavigateFile(int delta)
        {
            if (string.IsNullOrEmpty(_lastFilePath)) return false;
            string? dir = Path.GetDirectoryName(_lastFilePath);
            if (string.IsNullOrEmpty(dir)) return false;
            string[] exts = { "*.mp4", "*.mkv", "*.avi", "*.wmv", "*.mov", "*.webm", "*.m4v" };
            var files = new List<string>();
            foreach (var ext in exts) files.AddRange(Directory.GetFiles(dir, ext));
            files.Sort();
            int idx = files.FindIndex(f => f.Equals(_lastFilePath, StringComparison.OrdinalIgnoreCase));
            int next = idx + delta;
            Trace($"NavigateFile delta={delta} idx={idx} next={next} total={files.Count}");
            if (next < 0 || next >= files.Count) return false;
            LoadVideo(files[next]);
            StatusText.Text = $"{next + 1}/{files.Count}  {Path.GetFileName(files[next])}";
            return true;
        }
    }
}
