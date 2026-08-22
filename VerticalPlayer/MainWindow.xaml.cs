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

namespace VerticalPlayer
{
    // ─────────────────────────────────────────────────────────────────────────
    // データモデル
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>エフェクトプリセット 1 件分</summary>
    public class PresetSettings
    {
        public string Name { get; set; } = "Preset";
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
    }

    /// <summary>アプリ全設定（起動・終了時に JSON 保存）</summary>
    public class AppSettings
    {
        // ── ウィンドウ ──
        public double WindowLeft { get; set; }
        public double WindowTop { get; set; }
        public double WindowWidth { get; set; } = 460;
        public double WindowHeight { get; set; } = 860;
        public bool AlwaysOnTop { get; set; }

        // ── 再生 ──
        public double Volume { get; set; } = 0.7;
        public bool IsMuted { get; set; }
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool Loop { get; set; }
        public string? LastFilePath { get; set; }
        public double LastPosition { get; set; }   // 秒

        // ── 表示 ──
        public bool IsForceVertical { get; set; }
        public double Rotation { get; set; }
        public bool HwAccel { get; set; }

        // ── エフェクト ──
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Gamma { get; set; }
        public double ZoomScaleX { get; set; } = 1.0;
        public double ZoomScaleY { get; set; } = 1.0;

        // ── プリセット（複数） ──
        public List<PresetSettings> Presets { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // メインウィンドウ
    // ─────────────────────────────────────────────────────────────────────────
    public partial class MainWindow : Window
    {
        // ── 定数 ──
        // 引数渡しで実行した場合に、カレントディレクトリが変わることがあるため、常に実行ファイルのあるディレクトリを基準にする
        private static readonly string ConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "VerticalPlayer.json"
        );

        // ── 状態フラグ ──
        private bool _isDragging = false;
        private bool _isMuted = false;
        private double _prevVolume = 0.7;
        private bool _isPlaying = false;
        private double _currentRotation = 0;

        // ── タイマー ──
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        // ── 全画面状態退避 ──
        // ── 最後に開いたファイルパス（GetAdjacentFile用） ──
        private string _lastFilePath = string.Empty;

        // ── OSDシークドラッグ ──
        private bool _isOsdDragging = false;
        // ── プリセットコレクション（バインド用） ──
        private readonly ObservableCollection<PresetSettings> _presets = new();

        // ── コマ送り ──
        private bool _isAutoFraming = false;
        // （100ms = 10fps / 30fps動画で約3フレーム送り）
        private double _frameIntervalMs = 100;  // 10fps デフォルト
        private double _prevSpeed = 1.0;

        // ── トレースログ ──
        private static readonly string TracePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "trace.log");

        private static void Trace(string msg)
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

            // ドラッグ＆ドロップを有効化
            this.AllowDrop = true;
            this.Drop += Window_Drop;

            PresetList.ItemsSource = _presets;

            _timer.Tick += Timer_Tick;
            _osdTimer.Tick += OsdTimer_Tick;

            // VideoArea マウス移動でOSD表示
            VideoArea.MouseMove += VideoArea_MouseMove;

            // キーボードショートカット
            this.KeyDown += MainWindow_KeyDown;
        }

        // ─────────────────────────────────────────────────────────────────
        // ウィンドウ Loaded：設定復元
        // ─────────────────────────────────────────────────────────────────
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (File.Exists(ConfigPath))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(ConfigPath));
                    if (s != null) { RestoreSettings(s); return; }
                }
                catch { /* 壊れていたらデフォルト */ }
            }
            CenterOnScreen();
        }

        private void RestoreSettings(AppSettings s)
        {
            // ── ウィンドウ位置・サイズ ──
            double sw = SystemParameters.PrimaryScreenWidth;
            double sh = SystemParameters.PrimaryScreenHeight;
            this.Width = Math.Clamp(s.WindowWidth, 320, sw);
            this.Height = Math.Clamp(s.WindowHeight, 300, sh);
            this.Left = Math.Clamp(s.WindowLeft, 0, sw - this.Width);
            this.Top = Math.Clamp(s.WindowTop, 0, sh - this.Height);

            // ── 再生設定 ──
            VolumeSlider.Value = s.Volume;
            Player.Volume = s.Volume;
            _isMuted = s.IsMuted;
            if (_isMuted) { Player.Volume = 0; }
            SpeedSlider.Value = Math.Clamp(s.PlaybackSpeed, 0.25, 3.0);
            LoopCheck.IsChecked = s.Loop;

            // ── 表示設定 ──
            ForceVerticalMode.IsChecked = s.IsForceVertical;
            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            this.Topmost = s.AlwaysOnTop;
            _currentRotation = s.Rotation;
            PlayerRotation.Angle = _currentRotation;
            HwAccelCheck.IsChecked = s.HwAccel;

            // ── エフェクト ──
            ContrastSlider.Value = s.Contrast;
            SaturationSlider.Value = s.Saturation;
            GammaSlider.Value = s.Gamma;
            PlayerScale.ScaleX = s.ZoomScaleX;
            PlayerScale.ScaleY = s.ZoomScaleY;

            UpdateEffectLabels();

            // ── プリセット ──
            _presets.Clear();
            foreach (var p in s.Presets) _presets.Add(p);

            // ── 前回ファイル復元 ──
            if (!string.IsNullOrEmpty(s.LastFilePath) && File.Exists(s.LastFilePath))
            {
                LoadVideo(s.LastFilePath, s.LastPosition);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // ウィンドウ Closing：設定保存
        // ─────────────────────────────────────────────────────────────────
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 現在の再生位置を保存
            double lastPos = 0;
            if (Player.NaturalDuration.HasTimeSpan)
                lastPos = Player.Position.TotalSeconds;

            var s = new AppSettings
            {
                // ウィンドウ
                WindowLeft = this.Left,
                WindowTop = this.Top,
                WindowWidth = this.Width,
                WindowHeight = this.Height,
                AlwaysOnTop = this.Topmost,

                // 再生
                Volume = VolumeSlider.Value,
                IsMuted = _isMuted,
                PlaybackSpeed = SpeedSlider.Value,
                Loop = LoopCheck.IsChecked ?? false,
                LastFilePath = Player.Source?.LocalPath,
                LastPosition = lastPos,

                // 表示
                IsForceVertical = ForceVerticalMode.IsChecked ?? false,
                Rotation = _currentRotation,
                HwAccel = HwAccelCheck.IsChecked ?? false,

                // エフェクト
                Contrast = ContrastSlider.Value,
                Saturation = SaturationSlider.Value,
                Gamma = GammaSlider.Value,
                ZoomScaleX = PlayerScale.ScaleX,
                ZoomScaleY = PlayerScale.ScaleY,

                // プリセット
                Presets = new List<PresetSettings>(_presets),
            };

            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }

        // ─────────────────────────────────────────────────────────────────
        // 外部からの動画読み込み（コマンドライン引数 / 関連付け）
        // ─────────────────────────────────────────────────────────────────
        public void LoadVideoFromArg(string path) => LoadVideo(path);

        // ─────────────────────────────────────────────────────────────────
        // 動画読み込み共通処理
        // ─────────────────────────────────────────────────────────────────
        private void LoadVideo(string path, double seekSeconds = 0)
        {
            Trace($"LoadVideo: {path} seek={seekSeconds}");
            try
            {
                Player.LoadedBehavior = MediaState.Stop; // 停止状態に明示固定
                Player.Source = null;

                DropHint.Visibility = Visibility.Collapsed;

                // 2. ソースの割り当て
                // ここでLoadedBehaviorをManualにすると、内部で再生準備のみが非同期に行われる
                Player.LoadedBehavior = MediaState.Manual;
                Player.Source = new Uri(path);
                _lastFilePath = path;
                FileNameText.Text = Path.GetFileName(path);

                // 3. 次のUIスレッドの処理（＝デコード準備完了）を待つため、DispatcherPriority.Loaded を使用
                // Background よりも優先度を高くし、読み込みが「完了」した瞬間に乗せる
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (Player.NaturalVideoWidth >= 1920)
                        {
                            PlayerScale.ScaleX = 2.0;
                            PlayerScale.ScaleY = 2.0;
                        }
                        else
                        {
                            // それ以外はリセット（または必要に応じて別の初期値）
                            PlayerScale.ScaleX = 1.0;
                            PlayerScale.ScaleY = 1.0;
                        }
                        Player.Play();
                        _isPlaying = true;
                        UpdatePlayIcon();
                        _timer.Start();

                        if (seekSeconds > 0)
                        {
                            Player.Position = TimeSpan.FromSeconds(seekSeconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        // ここでエラーが出ればログへ書き出す
                        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "play_error.txt");
                        File.AppendAllText(logPath, $"{DateTime.Now} | Retry-Play Error: {ex.Message}{Environment.NewLine}", new System.Text.UTF8Encoding(false));
                    }
                }), DispatcherPriority.Loaded); // ここを Background から Loaded に変更

                // シーク（MediaOpened 後に行う必要があるため一時保存）
                _pendingSeek = seekSeconds;
                Trace("LoadVideo: source set, pending seek queued");
            }
            catch (Exception ex)
            {
                Trace($"LoadVideo EXCEPTION: {ex}");
            }
        }

        private double _pendingSeek = 0;

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            Trace($"MediaOpened: {Player.Source} {Player.NaturalVideoWidth}x{Player.NaturalVideoHeight} dur={Player.NaturalDuration}");
            ApplyLayout();
            Player.SpeedRatio = SpeedSlider.Value;

            // 動画情報を情報タブへ反映
            UpdateVideoInfo();

            // 前回位置へシーク
            if (_pendingSeek > 0 && Player.NaturalDuration.HasTimeSpan)
            {
                Player.Position = TimeSpan.FromSeconds(
                    Math.Min(_pendingSeek, Player.NaturalDuration.TimeSpan.TotalSeconds - 1));
                _pendingSeek = 0;
            }
        }
        // XAML側で <MediaElement x:Name="Player" MediaEnded="Player_MediaEnded" ... /> と設定されている前提です。
        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (LoopCheck.IsChecked == true)
            {
                Player.Position = TimeSpan.Zero;
                Player.Play();
            }
            else
            {
                // フォルダ内の次のファイルを再生して止めるかを判定
                if (!PlayNextVideoInFolder())
                {
                    _isPlaying = false;
                    UpdatePlayIcon();
                    _timer.Stop();
                }
            }
        }

        private bool PlayNextVideoInFolder()
        {
            if (string.IsNullOrEmpty(Player.Source?.LocalPath)) return false;

            string currentPath = Player.Source.LocalPath;
            string? directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(directory)) return false;

            string[] extensions = { "*.mp4", "*.mkv", "*.avi", "*.wmv", "*.mov" };
            List<string> fileList = new List<string>();
            foreach (var ext in extensions)
            {
                fileList.AddRange(Directory.GetFiles(directory, ext).OrderBy(f => f));
            }

            if (fileList.Count == 0) return false;

            int currentIndex = fileList.FindIndex(f => f.Equals(currentPath, StringComparison.OrdinalIgnoreCase));

            // 次のファイルが存在するか確認（リストの最後ではない場合）
            if (currentIndex >= 0 && currentIndex < fileList.Count - 1)
            {
                string nextPath = fileList[currentIndex + 1];
                LoadVideo(nextPath);
                return true;
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────
        // タイマー（シークバー / 時刻更新）
        // ─────────────────────────────────────────────────────────────────
        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!Player.NaturalDuration.HasTimeSpan) return;
            double total = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (total <= 0) return;

            double pos = Player.Position.TotalSeconds;
            string timeStr = $"{Fmt(Player.Position)} / {Fmt(Player.NaturalDuration.TimeSpan)}";

            if (!_isDragging)
            {
                SeekBar.Maximum = total;
                SeekBar.Value = pos;
                TimeDisplay.Text = timeStr;
            }
            if (!_isOsdDragging)
            {
                OsdSeekBar.Maximum = total;
                OsdSeekBar.Value = pos;
                OsdTimeDisplay.Text = timeStr;
            }
        }

        private static string Fmt(TimeSpan ts)
            => ts.TotalHours >= 1
               ? ts.ToString(@"h\:mm\:ss")
               : ts.ToString(@"m\:ss");

        // ─────────────────────────────────────────────────────────────────
        // 再生コントロール
        // ─────────────────────────────────────────────────────────────────
        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

        private void TogglePlayPause()
        {
            if (_isPlaying)
            {
                Player.Pause();
                _isPlaying = false;
                _timer.Stop();
            }
            else
            {
                Player.Play();
                _isPlaying = true;
                _timer.Start();
            }
            UpdatePlayIcon();
        }

        private void UpdatePlayIcon()
        {
            // Play ▶ / Pause ⏸ のパスデータを切り替え
            if (PlayIcon is System.Windows.Shapes.Path p)
            {
                p.Data = Geometry.Parse(
                    _isPlaying
                        ? "M4,3 H8 V17 H4 Z M12,3 H16 V17 H12 Z"   // Pause
                        : "M5,3 L19,10 L5,17 Z");                     // Play
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            Player.Stop();
            _timer.Stop();
            _isPlaying = false;
            UpdatePlayIcon();
            SeekBar.Value = 0;
            TimeDisplay.Text = "0:00 / 0:00";
            StatusText.Text = "";
        }

        private void Rewind_Click(object sender, RoutedEventArgs e)
            => Player.Position -= TimeSpan.FromSeconds(10);

        private void FastForward_Click(object sender, RoutedEventArgs e)
            => Player.Position += TimeSpan.FromSeconds(10);

        // ─────────────────────────────────────────────────────────────────
        // 音量
        // ─────────────────────────────────────────────────────────────────
        private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isMuted)
                Player.Volume = VolumeSlider.Value;
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            if (_isMuted)
            {
                _prevVolume = VolumeSlider.Value;
                Player.Volume = 0;
            }
            else
            {
                Player.Volume = _prevVolume;
                VolumeSlider.Value = _prevVolume;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // シークバー
        // ─────────────────────────────────────────────────────────────────
        // クリックした瞬間に即シーク（Thumbを掴む前でも反応）
        private void SeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !Player.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            Player.Position = TimeSpan.FromSeconds(t);
        }
        private void SeekBar_DragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;

        private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            Player.Position = TimeSpan.FromSeconds(SeekBar.Value);
            _isDragging = false;
        }

        private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !Player.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            Player.Position = TimeSpan.FromSeconds(t);
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
            if (dlg.ShowDialog() == true)
                LoadVideo(dlg.FileName);
        }

        // ─────────────────────────────────────────────────────────────────
        // 回転
        // ─────────────────────────────────────────────────────────────────
        private void Rotate_Click(object sender, RoutedEventArgs e)
        {
            _currentRotation = (_currentRotation + 90) % 360;
            PlayerRotation.Angle = _currentRotation;
            if (Player.NaturalVideoWidth > 0) ResizeToVideo();
        }

        // ─────────────────────────────────────────────────────────────────
        // ズーム
        // ─────────────────────────────────────────────────────────────────
        private void Player_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double f = e.Delta > 0 ? 1.1 : 0.9;
            PlayerScale.ScaleX = Math.Clamp(PlayerScale.ScaleX * f, 0.2, 5.0);
            PlayerScale.ScaleY = Math.Clamp(PlayerScale.ScaleY * f, 0.2, 5.0);
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            PlayerScale.ScaleX = 1.0;
            PlayerScale.ScaleY = 1.0;
        }

        // ─────────────────────────────────────────────────────────────────
        // レイアウト計算
        // ─────────────────────────────────────────────────────────────────
        private void Mode_Checked(object sender, RoutedEventArgs e) => ApplyLayout();

        private void ApplyLayout()
        {
            if (Player.NaturalVideoWidth == 0) return;

            if (ForceVerticalMode?.IsChecked == true)
            {
                _currentRotation = 0;
                PlayerRotation.Angle = 0;
            }
            ResizeToVideo();
        }

        private void ResizeToVideo()
        {
            if (Player.NaturalVideoWidth == 0 || Player.NaturalVideoHeight == 0) return;

            double vw = Player.NaturalVideoWidth;
            double vh = Player.NaturalVideoHeight;

            // 90 / 270 度の場合は縦横反転
            double dispRatio = (_currentRotation == 90 || _currentRotation == 270)
                ? vh / vw
                : vw / vh;

            // タスクバーを除いた最大利用可能領域を取得
            double waWidth = SystemParameters.WorkArea.Width;
            double waHeight = SystemParameters.WorkArea.Height;

            // 現在のウィンドウ幅を基準に高さを計算
            double newH = this.Width / dispRatio;

            // 【修正】計算された高さがタスクバーの限界を超える場合、縦に収まるよう幅を再計算
            if (newH > waHeight)
            {
                newH = waHeight;
                this.Width = newH * dispRatio;
            }

            if (newH < 300) { newH = 300; this.Width = newH * dispRatio; }
            this.Height = newH;
            EnsureOnScreen();
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
            if (SpeedLabel == null || Player == null) return;
            // _isAutoFraming中はStartAutoFrameがSpeedRatioを管理するためスキップ
            double v = Math.Round(SpeedSlider.Value * 10) / 10.0;
            SpeedLabel.Text = $"{v:F1}×";
            if (!_isAutoFraming)
                Player.SpeedRatio = Math.Clamp(v, 0.1, 4.0);
            Trace($"Speed_Changed: {v}");
        }

        // ─────────────────────────────────────────────────────────────────
        // エフェクト（WPF MediaElement は BitmapEffect 非対応のため UI 表示のみ）
        // ─────────────────────────────────────────────────────────────────
        private void Effect_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
            => UpdateEffectLabels();

        private void UpdateEffectLabels()
        {
            if (ContrastLabel != null) ContrastLabel.Text = $"{ContrastSlider.Value:+0.0;-0.0; 0.0}";
            if (SaturationLabel != null) SaturationLabel.Text = $"{SaturationSlider.Value:+0.0;-0.0; 0.0}";
            if (GammaLabel != null) GammaLabel.Text = $"{GammaSlider.Value:+0.0;-0.0; 0.0}";
        }

        private void ResetEffects_Click(object sender, RoutedEventArgs e)
        {
            ContrastSlider.Value = 0;
            SaturationSlider.Value = 0;
            GammaSlider.Value = 0;
            PlayerScale.ScaleX = 1.0;
            PlayerScale.ScaleY = 1.0;
            UpdateEffectLabels();
        }

        // ─────────────────────────────────────────────────────────────────
        // プリセット管理
        // ─────────────────────────────────────────────────────────────────
        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            if (_presets.Count >= 10)
            {
                StatusText.Text = "プリセット上限（10件）に達しています";
                return;
            }
            string name = PresetNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = $"Preset {_presets.Count + 1}";

            _presets.Add(new PresetSettings
            {
                Name = name,
                Contrast = ContrastSlider.Value,
                Saturation = SaturationSlider.Value,
                Gamma = GammaSlider.Value,
            });
            StatusText.Text = $"「{name}」を保存しました";
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PresetSettings p)
            {
                ContrastSlider.Value = p.Contrast;
                SaturationSlider.Value = p.Saturation;
                GammaSlider.Value = p.Gamma;
                UpdateEffectLabels();
                StatusText.Text = $"「{p.Name}」を適用しました";
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is PresetSettings p)
            {
                _presets.Remove(p);
                StatusText.Text = $"「{p.Name}」を削除しました";
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // 常に最前面
        // ─────────────────────────────────────────────────────────────────
        private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
            => this.Topmost = AlwaysOnTopCheck.IsChecked ?? false;

        // ─────────────────────────────────────────────────────────────────
        // 動画情報タブ更新
        // ─────────────────────────────────────────────────────────────────
        private void UpdateVideoInfo()
        {
            VideoInfoStack.Children.Clear();
            void Row(string label, string val)
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var t1 = new TextBlock { Text = label, Foreground = (Brush)FindResource("TextMuted"), FontSize = 10 };
                var t2 = new TextBlock
                {
                    Text = val,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(t1, 0); Grid.SetColumn(t2, 1);
                g.Children.Add(t1); g.Children.Add(t2);
                VideoInfoStack.Children.Add(g);
            }

            if (Player.Source == null) { Row("状態", "未読み込み"); return; }
            Row("ファイル名", Path.GetFileName(Player.Source.LocalPath));
            Row("解像度", $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight}");
            if (Player.NaturalDuration.HasTimeSpan)
                Row("長さ", Fmt(Player.NaturalDuration.TimeSpan));
        }

        // ─────────────────────────────────────────────────────────────────
        // サイドパネル開閉
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
        // カスタムウィンドウクロム操作
        // ─────────────────────────────────────────────────────────────────
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) MaxRestore_Click(sender, e);
            else DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => this.WindowState = WindowState.Minimized;

        private void MaxRestore_Click(object sender, RoutedEventArgs e)
            => this.WindowState = this.WindowState == WindowState.Maximized
               ? WindowState.Normal : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e)
            => this.Close();

        private void FullScreen_Click(object sender, RoutedEventArgs e)
            => ToggleFullScreen();

        // ─────────────────────────────────────────────────────────────────
        // キーボードショートカット
        // ─────────────────────────────────────────────────────────────────
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // F11 / Alt+Enter で全画面トグル
            if (e.Key == Key.F11 ||
                (e.Key == Key.System && e.SystemKey == Key.Enter &&
                 (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt))
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    // 全画面はFullScreenWindowが処理するためここでは不要
                    break;
                case Key.Space:
                    TogglePlayPause(); e.Handled = true; break;
                case Key.Left:
                    Player.Position -= TimeSpan.FromSeconds(10); e.Handled = true; break;
                case Key.Right:
                    Player.Position += TimeSpan.FromSeconds(10); e.Handled = true; break;
                case Key.Up:
                    VolumeSlider.Value = Math.Min(VolumeSlider.Value + 0.05, 1.0);
                    e.Handled = true; break;
                case Key.Down:
                    VolumeSlider.Value = Math.Max(VolumeSlider.Value - 0.05, 0.0);
                    e.Handled = true; break;
                case Key.M:
                    Mute_Click(sender, e); e.Handled = true; break;
                case Key.F:
                    MaxRestore_Click(sender, e); e.Handled = true; break;
                case Key.H:
                    // 動画の左右反転（鏡状態）をトグル切り替え
                    if (PlayerFlipTransform != null)
                    {
                        PlayerFlipTransform.ScaleX = (PlayerFlipTransform.ScaleX == 1) ? -1 : 1;
                    }
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        ///  WPFの MediaElement は、ウィンドウの状態（WindowState）が最大化や通常サイズへ切り替わる際のレイアウト再評価に伴い、
        ///  内部の再生コンポーネントがリセットされて再生位置が先頭（0秒）に戻ってしまう既知の挙動があります。
        /// 切り替え直前に現在の再生位置を退避させ、切り替え後に再適用（および再ロード発火に備えた一時変数への退避）を行うことで、この巻き戻り現象を確実に潰します。
        /// </summary>

        private void ToggleFullScreen()
        {
            Trace($"ToggleFullScreen called. Source={Player.Source}");
            if (Player.Source == null) { Trace("ToggleFullScreen: no source, abort"); return; }

            try
            {
                // MainWindow側を停止・Sourceをnullにしてから別Windowで再生
                var src = Player.Source;
                var pos = Player.Position;
                var vol = Player.Volume;
                var speed = Player.SpeedRatio;
                var angle = PlayerRotation?.Angle ?? 0;

                Player.Pause();
                Player.Source = null;
                _isPlaying = false;
                _timer.Stop();
                Trace($"ToggleFullScreen: opening FullScreenWindow src={src} pos={pos}");

                var fs = new FullScreenWindow(
                    owner: this,
                    source: src,
                    position: pos,
                    volume: vol,
                    isMuted: _isMuted,
                    speed: speed,
                    frameMs: _frameIntervalMs,
                    rotationAngle: angle);

                fs.ShowDialog(); // 閉じるまでここでブロック
                Trace("ToggleFullScreen: FullScreenWindow closed");
            }
            catch (Exception ex)
            {
                Trace($"ToggleFullScreen EXCEPTION: {ex}");
            }
        }

        // FullScreenWindowから隣接ファイルパスを取得
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

        // FullScreenWindow終了時に再生状態を受け取る
        public void ReturnFromFullScreen(Uri? source, TimeSpan position,
                                         double volume, double speed, bool isPlaying)
        {
            Trace($"ReturnFromFullScreen src={source} pos={position} playing={isPlaying}");
            if (source != null)
            {
                _lastFilePath = source.LocalPath;
                Player.Source = source;
                FileNameText.Text = Path.GetFileName(source.LocalPath);
                _pendingSeek = position.TotalSeconds;
            }
            Player.SpeedRatio = speed;
            Player.Volume = volume;
            VolumeSlider.Value = volume;
            SpeedSlider.Value = Math.Clamp(speed, 0.1, 4.0);

            if (isPlaying) { Player.Play(); _isPlaying = true; _timer.Start(); }
            else { _isPlaying = false; }
            UpdatePlayIcon();
        }

        // ─────────────────────────────────────────────────────────────────
        // OSD 制御
        // ─────────────────────────────────────────────────────────────────
        private void VideoArea_MouseMove(object sender, MouseEventArgs e)
        {
            // OSDはFullScreenWindowが管理するためMainWindowでは何もしない
        }

        private void OsdTimer_Tick(object? sender, EventArgs e)
        {
            _osdTimer.Stop();
            if (_isOsdDragging) return;
            OsdPanel.Visibility = Visibility.Collapsed;
        }

        private void Osd_MouseEnter(object sender, MouseEventArgs e) => _osdTimer.Stop();

        private void Osd_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isOsdDragging) _osdTimer.Start();
        }

        private void OsdSeekBar_DragStarted(object sender, DragStartedEventArgs e) => _isOsdDragging = true;

        private void OsdSeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            Player.Position = TimeSpan.FromSeconds(OsdSeekBar.Value);
            SeekBar.Value = OsdSeekBar.Value;
            _isOsdDragging = false;
        }

        private void OsdSeekBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !Player.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            SeekBar.Value = t;
            Player.Position = TimeSpan.FromSeconds(t);
        }

        private void OsdVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 起動時の InitializeComponent 実行中は、他コントロールが未生成(null)のため処理を逃げる
            if (VolumeSlider == null || OsdVolumeSlider == null || Player == null) return;

            VolumeSlider.Value = OsdVolumeSlider.Value;
            if (!_isMuted) Player.Volume = OsdVolumeSlider.Value;
        }


        // ─────────────────────────────────────────────────────────────────
        // コマ送り / 前後ファイル
        // ─────────────────────────────────────────────────────────────────
        private void FrameStep_Click(object sender, RoutedEventArgs e) => StepFrame(+1);
        private void FrameBack_Click(object sender, RoutedEventArgs e) => StepFrame(-1);

        private async void StepFrame(int direction)
        {
            Trace($"StepFrame dir={direction} isPlaying={_isPlaying} pos={Player.Position}");
            if (_isAutoFraming) StopAutoFrame();
            if (_isPlaying) { Player.Pause(); _isPlaying = false; UpdatePlayIcon(); _timer.Stop(); }

            var target = Player.Position + TimeSpan.FromMilliseconds(_frameIntervalMs * direction);
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            if (Player.NaturalDuration.HasTimeSpan && target > Player.NaturalDuration.TimeSpan)
                target = Player.NaturalDuration.TimeSpan;

            // Play → シーク → 描画完了を待ってから Pause
            // 同一フレームで連続実行すると映像が更新されないためawaitで1フレーム待機
            Player.Play();
            Player.Position = target;
            await Task.Delay(80); // 描画サイクル待ち（約2フレーム分）
            Player.Pause();

            Trace($"StepFrame done: target={target}");
        }

        private void AutoFrame_Click(object sender, RoutedEventArgs e)
        {
            if (_isAutoFraming) StopAutoFrame();
            else StartAutoFrame();
        }

        private void StartAutoFrame()
        {
            Trace($"StartAutoFrame fps={1000 / _frameIntervalMs:F1}");
            _isAutoFraming = true;
            _prevSpeed = Player.SpeedRatio;
            double fps = 1000.0 / _frameIntervalMs;
            double speed = Math.Clamp(fps / 30.0, 0.01, 1.0);
            Player.SpeedRatio = speed;
            SpeedSlider.Value = speed;
            if (!_isPlaying) { Player.Play(); _isPlaying = true; UpdatePlayIcon(); _timer.Start(); }
            if (AutoFrameLabel != null) AutoFrameLabel.Foreground = (Brush)FindResource("AccentBlue");
            StatusText.Text = $"スロー {speed:F2}×";
            Trace($"StartAutoFrame: speed={speed}");
        }

        private void StopAutoFrame()
        {
            Trace("StopAutoFrame");
            _isAutoFraming = false;
            double restoreSpeed = Math.Clamp(_prevSpeed > 0 ? _prevSpeed : 1.0, 0.1, 4.0);
            Player.SpeedRatio = restoreSpeed;
            // SliderをSpeed_Changedを経由せず直接更新（_isAutoFraming=falseなので二重適用なし）
            SpeedSlider.Value = restoreSpeed;
            if (AutoFrameLabel != null) AutoFrameLabel.Foreground = (Brush)FindResource("TextFaint");
            StatusText.Text = "";
            Trace($"StopAutoFrame: restored speed={restoreSpeed}");
        }

        private void FrameRate_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (FrameRateCombo?.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Tag?.ToString(), out double fps) && fps > 0)
            {
                _frameIntervalMs = 1000.0 / fps;
                Trace($"FrameRate_Changed: {fps}fps interval={_frameIntervalMs}ms");
                if (_isAutoFraming) StartAutoFrame(); // 速度再設定
            }
        }

        private void NextFile_Click(object sender, RoutedEventArgs e)
        {
            Trace("NextFile_Click");
            if (!NavigateFile(+1)) StatusText.Text = "次のファイルはありません";
        }

        private void PrevFile_Click(object sender, RoutedEventArgs e)
        {
            Trace("PrevFile_Click");
            if (!NavigateFile(-1)) StatusText.Text = "前のファイルはありません";
        }

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

        // ─────────────────────────────────────────────────────────────────
        // エラーログ出力機能
        // ─────────────────────────────────────────────────────────────────
        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Trace($"MediaFailed: {e.ErrorException?.GetType().Name}: {e.ErrorException?.Message}");
            Trace($"MediaFailed StackTrace: {e.ErrorException?.StackTrace}");
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "play_error.txt");
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | Error: {e.ErrorException?.Message}{Environment.NewLine}";
            try { File.AppendAllText(logPath, logMessage, new UTF8Encoding(false)); }
            catch { }
        }
    }
}
