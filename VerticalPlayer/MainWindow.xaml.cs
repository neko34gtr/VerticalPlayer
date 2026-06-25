using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
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
        private const string ConfigPath = "VerticalPlayer.json";

        // ── 状態フラグ ──
        private bool _isDragging = false;
        private bool _isMuted = false;
        private double _prevVolume = 0.7;
        private bool _isPlaying = false;
        private double _currentRotation = 0;

        // ── タイマー ──
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        // ── プリセットコレクション（バインド用） ──
        private readonly ObservableCollection<PresetSettings> _presets = new();

        // ─────────────────────────────────────────────────────────────────
        // 初期化
        // ─────────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();

            // ドラッグ＆ドロップを有効化
            this.AllowDrop = true;
            this.Drop += Window_Drop;

            PresetList.ItemsSource = _presets;

            _timer.Tick += Timer_Tick;

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
        // 動画読み込み共通処理
        // ─────────────────────────────────────────────────────────────────
        private void LoadVideo(string path, double seekSeconds = 0)
        {
            // 1. 既存再生の停止
            Player.Stop();
            Player.LoadedBehavior = MediaState.Stop; // 停止状態に明示固定
            Player.Source = null;

            DropHint.Visibility = Visibility.Collapsed;

            // 2. ソースの割り当て
            // ここでLoadedBehaviorをManualにすると、内部で再生準備のみが非同期に行われる
            Player.LoadedBehavior = MediaState.Manual;
            Player.Source = new Uri(path);
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
                    File.AppendAllText("play_error.txt", $"{DateTime.Now} | Retry-Play Error: {ex.Message}{Environment.NewLine}");
                }
            }), DispatcherPriority.Loaded); // ここを Background から Loaded に変更

            // シーク（MediaOpened 後に行う必要があるため一時保存）
            _pendingSeek = seekSeconds;
        }

        private double _pendingSeek = 0;

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
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
            if (_isDragging) return;
            if (!Player.NaturalDuration.HasTimeSpan) return;

            double total = Player.NaturalDuration.TimeSpan.TotalSeconds;
            if (total <= 0) return;

            SeekBar.Maximum = total;
            SeekBar.Value = Player.Position.TotalSeconds;
            TimeDisplay.Text = $"{Fmt(Player.Position)} / {Fmt(Player.NaturalDuration.TimeSpan)}";
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
            if (SpeedLabel == null) return;
            double v = Math.Round(SpeedSlider.Value * 4) / 4.0; // 0.25 刻み
            SpeedLabel.Text = $"{v:F2}×";
            Player.SpeedRatio = v;
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

        // ─────────────────────────────────────────────────────────────────
        // キーボードショートカット
        // ─────────────────────────────────────────────────────────────────
        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            // Alt + Enter のトグル判定
            if ((e.Key == Key.System && e.SystemKey == Key.Enter) &&
                (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Escape:
                    // 全画面表示中の場合のみ解除
                    if (TitleBar.Visibility == Visibility.Collapsed)
                    {
                        ToggleFullScreen();
                        e.Handled = true;
                    }
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
            }
        }

        // ── 全画面表示用の状態退避変数 ──
        private double _preFullScreenWidth = 460;
        private double _preFullScreenHeight = 860;
        private double _preFullScreenLeft = 0;
        private double _preFullScreenTop = 0;

        private void ToggleFullScreen()
        {
            if (TitleBar == null || ControlPanel == null) return;

            if (TitleBar.Visibility == Visibility.Visible)
            {
                // ── 全画面化 ──
                _preFullScreenWidth = this.Width;
                _preFullScreenHeight = this.Height;
                _preFullScreenLeft = this.Left;
                _preFullScreenTop = this.Top;

                this.ResizeMode = ResizeMode.NoResize;

                TitleBar.Visibility = Visibility.Collapsed;
                ControlPanel.Visibility = Visibility.Collapsed;

                this.WindowState = WindowState.Maximized;
            }
            else
            {
                // ── 全画面解除 ──
                this.WindowState = WindowState.Normal;

                TitleBar.Visibility = Visibility.Visible;
                ControlPanel.Visibility = Visibility.Visible;

                this.ResizeMode = ResizeMode.CanResizeWithGrip;

                this.Width = _preFullScreenWidth;
                this.Height = _preFullScreenHeight;
                this.Left = _preFullScreenLeft;
                this.Top = _preFullScreenTop;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // エラーログ出力機能 ---
        // ─────────────────────────────────────────────────────────────────
        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string logPath = "play_error.txt";
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | Error: {e.ErrorException.Message}{Environment.NewLine}";

            try
            {
                // UTF-8(BOM無し)で追記
                File.AppendAllText(logPath, logMessage, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // ログ書き込み自体が失敗した場合の予備処理（必要に応じて）
            }
        }
    }
}
