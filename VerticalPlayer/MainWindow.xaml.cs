using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public bool AutoPlayNext { get; set; } = true;

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
        public bool Denoise { get; set; }
        public bool DynamicContrast { get; set; }
        public int CompareViewMode { get; set; }
        public float SuperResolutionScale { get; set; } = 1f;
        public bool DnnSuperResolution { get; set; }
        public bool DnnWaitForBuild { get; set; } = true;
        public string? DnnModelFileName { get; set; }
        public double SharpAmount { get; set; } = 0.5;
        public bool ColorMatrix601To709 { get; set; }

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

        // ── 対応動画拡張子（一箇所にまとめて定義。増減はここだけ変更すればOK）──
        private static readonly string[] SupportedVideoExtensions =
        {
            "mp4", "mkv", "avi", "wmv", "mov", "webm", "m4v", "mpg", "ts", "flv", "ogv", "divx", "asf", "bc!"
        };

        /// <summary>Directory.GetFiles用の "*.ext" 形式パターン一覧</summary>
        private static IEnumerable<string> VideoGlobPatterns =>
            SupportedVideoExtensions.Select(ext => "*." + ext);

        /// <summary>OpenFileDialog用のFilter文字列</summary>
        private static string VideoOpenFileDialogFilter =>
            "動画ファイル|" + string.Join(";", VideoGlobPatterns) + "|すべてのファイル|*.*";

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        /// <summary>Windowsエクスプローラーと同じ「自然順」（数字を数値として比較する）でファイル名/
        /// フォルダ名を比較する。既定のstring.Sort/OrderByは単純な文字コード順のため、
        /// "file2"より"file10"が先に来るなど、エクスプローラー上の並びと食い違っていた。</summary>
        private sealed class NaturalNameComparer : IComparer<string>
        {
            public static readonly NaturalNameComparer Instance = new();
            public int Compare(string? x, string? y) =>
                StrCmpLogicalW(Path.GetFileName(x) ?? "", Path.GetFileName(y) ?? "");
        }

        /// <summary>指定フォルダ直下の動画ファイル＋サブフォルダを、Windowsエクスプローラーと同じ
        /// 自然順で並べた一覧を返す（次/前ファイル探索の共通ロジック）。</summary>
        private static List<string> GetNaturalSortedEntries(string dir)
        {
            var entries = new List<string>();
            foreach (var ext in VideoGlobPatterns) entries.AddRange(Directory.GetFiles(dir, ext));
            entries.AddRange(Directory.GetDirectories(dir));
            entries.Sort(NaturalNameComparer.Instance);
            return entries;
        }

        /// <summary>指定フォルダ以下（サブフォルダも自然順に再帰的に）から、最初に見つかる動画
        /// ファイルを返す。フォルダを直接開いた時、および次ファイル探索でフォルダに突き当たった
        /// 時の両方で使う共通ロジック。</summary>
        private static string? FindFirstVideoRecursive(string dir)
        {
            try
            {
                foreach (var entry in GetNaturalSortedEntries(dir))
                {
                    if (Directory.Exists(entry))
                    {
                        var found = FindFirstVideoRecursive(entry);
                        if (found != null) return found;
                    }
                    else
                    {
                        return entry;
                    }
                }
            }
            catch { /* アクセス権限等で読めないフォルダはスキップ */ }
            return null;
        }

        /// <summary>現在のファイルと同じフォルダ内で、自然順に1つ先/前のエントリへ進む。
        /// フォルダに突き当たった場合はその中を再帰的に探索し、最初に見つかる動画ファイルへ
        /// 進む（中に動画が無ければ、さらにその次のエントリへスキップを続ける）。</summary>
        private static string? FindAdjacentVideo(string currentPath, int delta)
        {
            string? dir = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var entries = GetNaturalSortedEntries(dir);
            int idx = entries.FindIndex(f => f.Equals(currentPath, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return null;

            int step = delta >= 0 ? 1 : -1;
            for (int i = idx + step; i >= 0 && i < entries.Count; i += step)
            {
                string entry = entries[i];
                if (Directory.Exists(entry))
                {
                    var found = FindFirstVideoRecursive(entry);
                    if (found != null) return found;
                    // 動画が無いフォルダはスキップして次のエントリへ
                }
                else
                {
                    return entry;
                }
            }
            return null;
        }

        // ── 状態フラグ ──
        private bool _isDragging = false;
        private bool _seekLiveBusy = false;
        private double? _seekLivePendingSeconds = null;
        private bool _wasPlayingBeforeSeekDrag = false;
        private bool _dragCompleting = false;
        private int _actualFrameCount;
        private readonly System.Diagnostics.Stopwatch _fpsStopwatch = System.Diagnostics.Stopwatch.StartNew();
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
#if DEBUG
            try
            {
                File.AppendAllText(TracePath,
                    $"{DateTime.Now:HH:mm:ss.fff} | {msg}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
#endif
        }

        // ─────────────────────────────────────────────────────────────────
        // 初期化
        // ─────────────────────────────────────────────────────────────────
        public MainWindow()
        {
#if DEBUG
            try
            {
                // アプリ起動時にログファイルを初期化（空にする）
                File.WriteAllText(TracePath, string.Empty, new UTF8Encoding(false));
            }
            catch { }
#endif

            Trace("=== MainWindow() start ===");
            InitializeComponent();
            Trace("InitializeComponent done");

            // GPU描画パス（D3DImage経由）を有効化。コントラスト/ダイナミックコントラスト/
            // 超解像/比較ビューはこれがtrueでないと一切効果が出ない（デノイズはCPU/avfilter側
            // なので無関係）。D3D9Ex/D3D11初期化に失敗した環境では自動的にWriteableBitmap側へ
            // フォールバックする（GpuFramePresenter.IsAvailable=false時）。
            Player.UseGpuPresenter = true;
            Trace($"GpuPresenter available={Player.IsGpuPresenterAvailable}（falseの場合、D3D9Ex/D3D11初期化失敗のためGPU専用機能は全て無効）");

            // DNN超解像エンジンのバックグラウンドビルド中はStatusTextで見える化する
            // （初回ビルドは数十秒かかることがあり、無表示だと固まったように見えるため）
            Player.DnnBuildStateChanged += building =>
            {
                StatusText.Text = building ? "超解像エンジンをビルド中…（初回のみ、数十秒かかることがあります）" : "";
            };

            // 実測FPS表示（1秒間隔で実際に表示されたフレーム数を集計）
            Player.FrameDisplayed += _ => OnFrameDisplayedForFps();

            // DNNモデル一覧をmodelsフォルダから自動スキャンしてコンボへ反映
            // （軽量モデルへの差し替えを見越して、モデルファイルの追加だけで選べるようにする）
            foreach (var name in VerticalPlayer.Media.FfmpegMediaElement.ListAvailableDnnModels())
                DnnModelCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            if (DnnModelCombo.Items.Count > 0 && DnnModelCombo.SelectedIndex < 0)
                DnnModelCombo.SelectedIndex = 0;

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

            // 実際のデコードモード（HW/SW）表示。設定パネル内のDecodeModeTextと            // コントロールバーのHwStatusLabelを同じイベントで同時に更新することで連動させる。
            Player.DecodeModeChanged += mode =>
            {
                DecodeModeText.Text = $"デコード: {mode}";
                bool isHw = mode.StartsWith("HW");
                var brush = isHw ? (Brush)FindResource("AccentCyan") : (Brush)FindResource("TextPrimary");
                DecodeModeText.Foreground = brush;

                HwStatusLabel.Text = isHw ? "H/W" : "S/W";
                HwStatusLabel.Foreground = brush;
                HwStatusBtn.ToolTip = isHw
                    ? "現在: ハードウェアデコード（クリックでソフトウェアへ切替）"
                    : "現在: ソフトウェアデコード（クリックでハードウェアへ切替）";
            };

            // チャプター目盛り（MPC-HC風）。ファイルを開くたびに更新、シークバーの
            // 幅が変わった時（ウィンドウリサイズ）も再配置する。
            Player.ChaptersLoaded += chapters =>
            {
                _chapterSeconds = chapters;
                RedrawChapterTicks();
            };
            ChapterTicksCanvas.SizeChanged += (s, e) => RedrawChapterTicks();
        }

        private List<double> _chapterSeconds = new();

        private void RedrawChapterTicks()
        {
            ChapterTicksCanvas.Children.Clear();
            if (_chapterSeconds.Count == 0 || !Player.NaturalDuration.HasTimeSpan) return;
            double total = Player.NaturalDuration.TimeSpan.TotalSeconds;
            double width = ChapterTicksCanvas.ActualWidth;
            if (total <= 0 || width <= 0) return;

            foreach (var t in _chapterSeconds)
            {
                double x = Math.Clamp(t / total, 0, 1) * width;
                var tick = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Height = ChapterTicksCanvas.ActualHeight > 0 ? ChapterTicksCanvas.ActualHeight : 3,
                    Fill = (Brush)FindResource("TextMuted")
                };
                Canvas.SetLeft(tick, x - 1);
                Canvas.SetTop(tick, 0);
                ChapterTicksCanvas.Children.Add(tick);
            }
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
            AutoPlayNextCheck.IsChecked = s.AutoPlayNext;
            _currentRotation = s.Rotation;
            PlayerRotation.Angle = _currentRotation;
            Player.DisplayRotation = _currentRotation;
            HwAccelCheck.IsChecked = s.HwAccel;

            // ── エフェクト ──
            ContrastSlider.Value = s.Contrast;
            SaturationSlider.Value = s.Saturation;
            GammaSlider.Value = s.Gamma;
            SharpnessSlider.Value = s.SharpAmount;
            if (SharpnessLabel != null) SharpnessLabel.Text = $"{s.SharpAmount:0.0}";
            Player.SharpAmount = (float)s.SharpAmount;
            ColorMatrixCheck.IsChecked = s.ColorMatrix601To709;
            Player.ColorMatrixMode = s.ColorMatrix601To709 ? 1 : 0;
            PlayerScale.ScaleX = s.ZoomScaleX;
            PlayerScale.ScaleY = s.ZoomScaleY;
            DenoiseCheck.IsChecked = s.Denoise;
            Player.Denoise = s.Denoise; // 再オープン方式のため、次に開くファイルから適用（起動直後は未オープンなのでこれで十分）
            DynamicContrastCheck.IsChecked = s.DynamicContrast;
            Player.DynamicContrast = s.DynamicContrast;
            CompareModeCombo.SelectedIndex = Math.Clamp(s.CompareViewMode, 0, 2);
            Player.CompareViewMode = s.CompareViewMode;
            SuperResolutionCombo.SelectedIndex = s.DnnSuperResolution
                ? 3
                : s.SuperResolutionScale switch
                {
                    >= 1.9f => 2,
                    >= 1.4f => 1,
                    _ => 0
                };
            Player.DnnSuperResolutionEnabled = s.DnnSuperResolution;
            Player.SuperResolutionScale = s.SuperResolutionScale;
            DnnWaitForBuildCheck.IsChecked = s.DnnWaitForBuild;

            // DNNモデル選択の復元（コンボは既にコンストラクタでmodelsフォルダから
            // populate済みのはず。見つからない場合は先頭のまま＝規定モデル）
            if (!string.IsNullOrEmpty(s.DnnModelFileName))
            {
                foreach (ComboBoxItem item in DnnModelCombo.Items)
                {
                    if ((string)item.Tag == s.DnnModelFileName)
                    {
                        DnnModelCombo.SelectedItem = item;
                        break;
                    }
                }
            }

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
            _mediaInfo?.Dispose();
            // RAMディスク運用のtrtcacheを、使われていれば永続バックアップへコピー
            Player.BackupDnnTrtCache();
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
                AutoPlayNext = AutoPlayNextCheck.IsChecked ?? true,

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
                SharpAmount = SharpnessSlider.Value,
                ColorMatrix601To709 = ColorMatrixCheck.IsChecked ?? false,
                ZoomScaleX = PlayerScale.ScaleX,
                ZoomScaleY = PlayerScale.ScaleY,
                Denoise = DenoiseCheck.IsChecked ?? false,
                DynamicContrast = DynamicContrastCheck.IsChecked ?? false,
                CompareViewMode = CompareModeCombo.SelectedIndex,
                SuperResolutionScale = (SuperResolutionCombo.SelectedItem is ComboBoxItem srItem &&
                    float.TryParse((string)srItem.Tag, System.Globalization.CultureInfo.InvariantCulture, out float srScale))
                    ? srScale : 1f,
                DnnSuperResolution = SuperResolutionCombo.SelectedItem is ComboBoxItem srDnnItem &&
                    (string)srDnnItem.Tag == "dnn",
                DnnWaitForBuild = DnnWaitForBuildCheck.IsChecked ?? true,
                DnnModelFileName = DnnModelCombo.SelectedItem is ComboBoxItem dnnModelItem
                    ? (string)dnnModelItem.Tag : null,

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

            // フォルダが渡された場合（ドラッグ&ドロップ／コマンドライン引数）は、
            // その中を自然順に再帰探索して最初に見つかる動画ファイルを開く。
            if (Directory.Exists(path))
            {
                string? first = FindFirstVideoRecursive(path);
                if (first == null)
                {
                    Trace($"LoadVideo: フォルダ内に動画ファイルが見つからない: {path}");
                    StatusText.Text = "フォルダ内に動画ファイルが見つかりません";
                    return;
                }
                path = first;
                Trace($"LoadVideo: フォルダを解決 -> {path}");
            }

            try
            {
                // 特殊再生（自動コマ送り/スロー）状態はファイル単位で持ち回さない。
                // 次ファイルへ切り替える際は常にノーマル速度へ戻す。
                if (_isAutoFraming) StopAutoFrame();
                Player.SpeedRatio = 1.0;
                SpeedSlider.Value = 1.0;
                VideoCodecLabel.Text = "";
                AudioCodecLabel.Text = "";
                AudioChannelLabel.Text = "";
                _chapterSeconds = new List<double>();
                ChapterTicksCanvas.Children.Clear();

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

        private async void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            Trace($"MediaOpened: {Player.Source} {Player.NaturalVideoWidth}x{Player.NaturalVideoHeight} dur={Player.NaturalDuration}");
            ApplyLayout();
            Player.SpeedRatio = SpeedSlider.Value;

            // 動画詳細情報の取得と表示
            if (Player.Source?.LocalPath != null)
                AnalyzeAndShowMediaInfo(Player.Source.LocalPath);

            // 前回位置へシーク
            if (_pendingSeek > 0 && Player.NaturalDuration.HasTimeSpan)
            {
                Player.Position = TimeSpan.FromSeconds(
                    Math.Min(_pendingSeek, Player.NaturalDuration.TimeSpan.TotalSeconds - 1));
                _pendingSeek = 0;
            }

            // 動画情報タブ更新
            UpdateVideoInfo();

            // DNNモード選択中に解像度の異なるファイルを開いた場合、「待ってから再生」設定なら
            // ライブ切替時と同じく一時停止→ビルド待ちにする（未対応だと常にバックグラウンド版
            // ＝等倍のまま再生継続になり、設定と挙動が食い違って見えていた）
            if (SuperResolutionCombo.SelectedItem is ComboBoxItem srItem && (string)srItem.Tag == "dnn" &&
                DnnWaitForBuildCheck.IsChecked == true && !Player.IsDnnReadyForCurrentResolution)
            {
                await EnableDnnSuperResolutionAsync();
            }
        }

        // MediaInfoNative で詳細解析
        private void AnalyzeAndShowMediaInfo(string path)
        {
            try
            {
                var mi = new MediaInfoNative(path);
                if (!mi.Success)
                {
                    Trace($"MediaInfo: failed for {path}");
                    _mediaInfo?.Dispose();
                    _mediaInfo = null;
                    UpdateCodecStatusBar();
                    return;
                }

                // フレームレート自動設定
                double fps = mi.VideoFrameRate;
                if (fps > 0)
                {
                    _frameIntervalMs = 1000.0 / fps;
                    Trace($"MediaInfo: fps={fps} -> frameIntervalMs={_frameIntervalMs:F2}");
                    UpdateFrameRateCombo(fps);
                }

                // 古いインスタンスを破棄して新しいものを保持
                _mediaInfo?.Dispose();
                _mediaInfo = mi;
                UpdateVideoInfo();
                UpdateCodecStatusBar();
            }
            catch (Exception ex)
            {
                Trace($"AnalyzeAndShowMediaInfo EXCEPTION: {ex.Message}");
            }
        }

        // コントロールバーのH/W表示の隣に、PotPlayerのような映像/音声コーデック略称を表示する。
        // MediaInfoNativeの解析が終わるたびに（AnalyzeAndShowMediaInfoから）呼ばれる。
        private void UpdateCodecStatusBar()
        {
            if (_mediaInfo == null || !_mediaInfo.Success)
            {
                VideoCodecLabel.Text = "";
                AudioCodecLabel.Text = "";
                AudioChannelLabel.Text = "";
                return;
            }

            VideoCodecLabel.Text = _mediaInfo.VideoCodec ?? "";
            AudioCodecLabel.Text = _mediaInfo.AudioCodec ?? "";
            int ch = _mediaInfo.AudioChannelCount;
            AudioChannelLabel.Text = ch switch
            {
                1 => "1.0",
                2 => "2.0",
                6 => "5.1",
                8 => "7.1",
                _ => ch > 0 ? $"{ch}ch" : ""
            };
        }

        private MediaInfoNative? _mediaInfo = null;

        private void UpdateFrameRateCombo(double fps)
        {
            // ComboBoxのfps選択を実際のfpsに近いものに更新
            if (FrameRateCombo == null) return;
            double[] candidates = { 1, 2, 5, 10, 15, 24, 30 };
            double best = candidates.OrderBy(c => Math.Abs(c - fps)).First();
            foreach (ComboBoxItem item in FrameRateCombo.Items)
            {
                if (double.TryParse(item.Tag?.ToString(), out double v) && v == best)
                {
                    FrameRateCombo.SelectedItem = item;
                    break;
                }
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
                // フォルダ内の次のファイルを自動再生するか（設定でOFFにできる）
                if (!(AutoPlayNextCheck.IsChecked ?? true) || !PlayNextVideoInFolder())
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
            string? nextPath = FindAdjacentVideo(Player.Source.LocalPath, +1);
            if (nextPath == null) return false;
            LoadVideo(nextPath);
            return true;
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
        // 実測FPS: Player.FrameDisplayed（実際に画面へ表示されたフレーム）を1秒集計して表示。
        // DNN超解像の性能改善効果を数値で確認できるようにするための計測用。
        private void OnFrameDisplayedForFps()
        {
            _actualFrameCount++;
            if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
            {
                double fps = _actualFrameCount * 1000.0 / _fpsStopwatch.ElapsedMilliseconds;
                ActualFpsLabel.Text = $"{fps:F1}fps";
                _actualFrameCount = 0;
                _fpsStopwatch.Restart();
            }
        }

        private void DnnModel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (DnnModelCombo.SelectedItem is ComboBoxItem item)
            {
                Player.DnnModelFileName = (string)item.Tag;
                UpdateDnnComboLabel();
            }
        }

        // SuperResolutionCombo側の「DNN/TensorRT」項目に、実際に選択中のモデルの倍率
        // （ファイル名から自動解析されたもの）を反映する。DnnModelComboの選択が変わる
        // たびに呼ぶこと（モデルによって倍率が異なる＝2x/4x等が混在するため固定表示にできない）。
        private void UpdateDnnComboLabel()
        {
            DnnComboItem.Content = $"DNN/TensorRT（{Player.DnnScale}倍）";
        }

        private void SeekBar_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            // StepToVideoOnlyAsyncは「音声は一時停止済み」前提のため、ドラッグ中に
            // 再生中のままだと音声だけ実時間で進み続け、位置の連打書き換えで
            // 早送りパラパラ再生のようになってしまう。ここで確実に一時停止する。
            _wasPlayingBeforeSeekDrag = _isPlaying;
            if (_isPlaying)
            {
                Player.Pause();
                _isPlaying = false;
                UpdatePlayIcon();
                _timer.Stop();
            }
        }

        private async void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragCompleting = true;
            // 直前のライブシーク(StepToVideoOnlyAsync)がまだ処理中の場合、その後始末の
            // _engine.Pause()がこの後のPlay()を追い越して実行され、映像だけ再度一時停止
            // してしまうことがあるため、完全に終わるのを待ってから本シークへ移行する。
            while (_seekLiveBusy)
                await Task.Delay(15);

            // ここは「投げっぱなしSeek+即Play」ではなく、ドラッグ中のライブプレビューと同じ
            // StepToVideoOnlyAsyncで実際に目標フレームへ追いついたことを確認してからPlay()する。
            // 投げっぱなしだと音声だけ即座に新位置から再生が始まり、映像はキーフレームからの
            // 追いつきデコード中（追いつくまで無表示）のため、GOPが大きい素材で追いつきが遅れると
            // 音声だけ先に進んで「キーフレーム寄りに引っ張られたような着地ズレ」になっていた。
            // ライブプレビュー用の500msでは大きいGOPで追いつき切らないことがあるため、
            // 最終着地は精度優先でタイムアウトを長めに取る。
            var target = TimeSpan.FromSeconds(SeekBar.Value);
            await Player.StepToVideoOnlyAsync(target, timeoutMs: 2000);

            _isDragging = false;
            _dragCompleting = false;
            if (_wasPlayingBeforeSeekDrag)
            {
                Player.Play();
                _isPlaying = true;
                UpdatePlayIcon();
                _timer.Start();
            }
        }

        // ドラッグ中はThumb移動のたびに映像だけ即シーク（音声には触れない）。
        // 前回のシークが終わっていない間に来た移動要求は最新値だけ残して間引く。
        private async void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDragging || _dragCompleting || !Player.NaturalDuration.HasTimeSpan) return;
            await RequestLiveSeekAsync(SeekBar.Value);
        }

        private async Task RequestLiveSeekAsync(double seconds)
        {
            _seekLivePendingSeconds = seconds;
            if (_seekLiveBusy) return;
            _seekLiveBusy = true;
            try
            {
                while (_seekLivePendingSeconds is double target)
                {
                    _seekLivePendingSeconds = null;
                    // ドラッグ中は精度より速さ優先の軽量プレビュー（直近キーフレームを即表示）。
                    // 正確な1枚への合わせ込みはDragCompleted側のStepToVideoOnlyAsyncで行う。
                    await Player.FastSeekPreviewAsync(TimeSpan.FromSeconds(target));
                }
            }
            finally
            {
                _seekLiveBusy = false;
            }
        }

        private void SeekBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !Player.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            Player.Position = TimeSpan.FromSeconds(t);
        }
        private void SeekBar_MouseMove(object sender, MouseEventArgs e)
        {
            double width = SeekBar.ActualWidth;
            if (!Player.NaturalDuration.HasTimeSpan || width <= 0) { SeekPreviewPopup.IsOpen = false; return; }
            double ratio = Math.Clamp(e.GetPosition(SeekBar).X / width, 0, 1);
            double total = Player.NaturalDuration.TimeSpan.TotalSeconds;
            SeekPreviewText.Text = Fmt(TimeSpan.FromSeconds(ratio * total));

            SeekPreviewPopup.IsOpen = true;
            var popupChild = (FrameworkElement)SeekPreviewPopup.Child;
            popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double halfW = popupChild.DesiredSize.Width / 2;
            double cursorX = e.GetPosition(SeekBar).X;
            SeekPreviewPopup.HorizontalOffset = Math.Clamp(cursorX - halfW, 0, Math.Max(0, width - halfW * 2));
        }

        private void SeekBar_MouseLeave(object sender, MouseEventArgs e)
        {
            SeekPreviewPopup.IsOpen = false;
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
                Filter = VideoOpenFileDialogFilter,
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
            double before = _currentRotation;
            _currentRotation = (_currentRotation + 90) % 360;
            PlayerRotation.Angle = _currentRotation;
            Player.DisplayRotation = _currentRotation;
            Trace($"Rotate: {before}° -> {_currentRotation}° (PlayerRotation.Angle actual={PlayerRotation.Angle}°)");
            if (Player.NaturalVideoWidth > 0)
            {
                ResizeToVideo();
                Trace($"Rotate: ResizeToVideo applied. WindowSize={this.Width:F0}x{this.Height:F0}");
            }
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
                Player.DisplayRotation = 0;
            }
            ResizeToVideo();
        }

        private bool _resizingProgrammatically = false;

        // 手動でウィンドウをドラッグリサイズした際、片辺だけを動かした場合は
        // もう片方をアスペクト比維持で自動追従させる（横方向の黒帯を自動解消）
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_resizingProgrammatically) return;
            if (Player == null || Player.NaturalVideoWidth <= 0) return;
            if (this.WindowState != WindowState.Normal) return;

            double dispRatio = (_currentRotation == 90 || _currentRotation == 270)
                ? (double)Player.NaturalVideoHeight / Player.NaturalVideoWidth
                : (double)Player.NaturalVideoWidth / Player.NaturalVideoHeight;

            _resizingProgrammatically = true;
            try
            {
                if (e.WidthChanged && !e.HeightChanged)
                {
                    this.Height = Math.Max(this.MinHeight, e.NewSize.Width / dispRatio);
                }
                else if (e.HeightChanged && !e.WidthChanged)
                {
                    this.Width = Math.Max(this.MinWidth, e.NewSize.Height * dispRatio);
                }
                // 角ドラッグ等で両辺同時に変わった場合は、ユーザー操作をそのまま尊重する
            }
            finally
            {
                _resizingProgrammatically = false;
            }
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

            // 利用可能な高さを基準に幅を算出し、画面幅をはみ出す場合のみ幅基準に切り替える。
            // （旧ロジックは「現在の幅」を基準にしていたため、回転前の狭い幅がそのまま残り、
            //   回転後に物理的な縦サイズを最大限使い切れなかった）
            double newH = waHeight;
            double newW = newH * dispRatio;
            if (newW > waWidth)
            {
                newW = waWidth;
                newH = newW / dispRatio;
            }

            if (newH < 300) { newH = 300; newW = newH * dispRatio; }

            this.Width = newW;
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
        // エフェクト（AVEngine内でBGRAバッファへ直接適用。次フレームから即反映）
        // ─────────────────────────────────────────────────────────────────
        private void Effect_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateEffectLabels();
            Player.Contrast = ContrastSlider.Value;
            Player.Saturation = SaturationSlider.Value;
            Player.Gamma = GammaSlider.Value;
        }

        private void Sharpness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SharpnessLabel != null) SharpnessLabel.Text = $"{SharpnessSlider.Value:0.0}";
            Player.SharpAmount = (float)SharpnessSlider.Value;
        }

        private void ColorMatrix_Changed(object sender, RoutedEventArgs e)
        {
            Player.ColorMatrixMode = (ColorMatrixCheck.IsChecked ?? false) ? 1 : 0;
        }

        // ─────────────────────────────────────────────────────────────────
        // 画面フィット（黒帯なし）・アスペクト比・デインターレース
        // ─────────────────────────────────────────────────────────────────
        private void ScaleMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ScaleModeCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<VerticalPlayer.Media.VideoScaleMode>((string)item.Tag, out var mode))
            {
                Player.ScaleMode = mode;
            }
        }

        private void AspectMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (AspectModeCombo.SelectedItem is ComboBoxItem item
                && Enum.TryParse<VerticalPlayer.Media.VideoAspectMode>((string)item.Tag, out var mode))
            {
                Player.AspectMode = mode;
            }
        }

        private void Deinterlace_Changed(object sender, RoutedEventArgs e)
        {
            Player.Deinterlace = DeinterlaceCheck.IsChecked ?? false;
        }

        private void Denoise_Changed(object sender, RoutedEventArgs e)
        {
            Player.Denoise = DenoiseCheck.IsChecked ?? false;

            if (Player.Source != null)
            {
                // HW/SW切替と同じ「現在位置・再生状態を保持したまま再オープン」方式。
                // デノイズは再生中スレッドの途中で動的に切り替えず、常に再オープンで反映する。
                var pos = Player.Position;
                bool wasPlaying = _isPlaying;
                var src = Player.Source;
                Trace($"Denoise_Changed: requested={Player.Denoise} - 現在のファイルを再オープンして即時反映 pos={pos}");
                Player.Source = src;
                Player.Position = pos;
                if (wasPlaying) { Player.Play(); _isPlaying = true; } else { _isPlaying = false; }
            }
            else
            {
                Trace($"Denoise_Changed: requested={Player.Denoise}（次に開くファイルから適用）");
            }
        }

        private void DynamicContrast_Changed(object sender, RoutedEventArgs e)
        {
            // GPU Compute Shaderのみで完結する後段処理のため、デコードスレッドには一切触れない。
            // H/W・デノイズと違い再オープンは不要で、ライブに即時反映される。
            Player.DynamicContrast = DynamicContrastCheck.IsChecked ?? false;
        }

        private async void SuperResolution_Changed(object sender, SelectionChangedEventArgs e)
        {
            // こちらもGPU完結の後段処理のため再オープン不要（ライブ切替）。
            if (SuperResolutionCombo.SelectedItem is not ComboBoxItem item) return;
            string tag = (string)item.Tag;
            if (tag == "dnn")
            {
                if (DnnWaitForBuildCheck.IsChecked == true)
                    await EnableDnnSuperResolutionAsync();
                else
                    Player.DnnSuperResolutionEnabled = true; // バックグラウンドビルド（等倍表示のまま継続）
            }
            else if (float.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out float scale))
            {
                Player.DnnSuperResolutionEnabled = false;
                Player.SuperResolutionScale = scale;
            }
        }

        // DNNへのライブ切替: 既にビルド済みの解像度なら待たずに即切替。未ビルドの場合は
        // 一時停止してオーバーレイ表示（StatusText）を出し、ビルド完了を待ってから再生再開する
        // （ビルド中に低解像度のまま再生し続けると「今どちらの画質か分かりにくい」ため）。
        private async Task EnableDnnSuperResolutionAsync()
        {
            // 動画が未オープン（解像度0x0）の場合はビルドしようがないため、
            // フラグだけ立てて実際のビルドはMediaOpened後に回す
            // （起動時のRestoreSettingsによるコンボ初期化がSelectionChangedを誤発火させ、
            // 0x0でビルドを試みて失敗し、その後始末が別解像度の正常なビルドを巻き添えで
            // 破棄してしまう不具合があったため、ここで確実にガードする）。
            if (Player.NaturalVideoWidth <= 0 || Player.NaturalVideoHeight <= 0)
            {
                Player.DnnSuperResolutionEnabled = true;
                return;
            }

            if (Player.IsDnnReadyForCurrentResolution)
            {
                Player.DnnSuperResolutionEnabled = true;
                return;
            }

            bool wasPlaying = _isPlaying;
            if (_isPlaying) { Player.Pause(); _isPlaying = false; UpdatePlayIcon(); _timer.Stop(); }

            StatusText.Text = "超解像エンジンをビルド中…（初回のみ、数十秒かかることがあります）";
            bool ok = await Player.PrebuildDnnSuperResolutionAsync();
            StatusText.Text = ok ? "" : "DNN超解像エンジンの初期化に失敗しました（Lanczos版のままです）";

            Player.DnnSuperResolutionEnabled = ok;
            if (!ok)
            {
                // 失敗時は無限に黒画面/低解像度のままにしないよう「なし」へ戻す
                SuperResolutionCombo.SelectedIndex = 0;
            }

            if (wasPlaying) { Player.Play(); _isPlaying = true; UpdatePlayIcon(); _timer.Start(); }
        }

        private void CompareMode_Changed(object sender, SelectionChangedEventArgs e)
        {
            // GPU完結の後段処理のため再オープン不要（ライブ切替）。
            if (CompareModeCombo.SelectedItem is ComboBoxItem item &&
                int.TryParse((string)item.Tag, out int mode))
            {
                Player.CompareViewMode = mode;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // ハードウェアアクセラレーション設定（次に開くファイルから適用）
        // ─────────────────────────────────────────────────────────────────
        private void HwAccel_Changed(object sender, RoutedEventArgs e)
        {
            Player.HardwareAcceleration = HwAccelCheck.IsChecked ?? false;

            if (Player.Source != null)
            {
                // 再生中のファイルにも即時反映するため、現在位置・再生状態を保持したまま開き直す
                var pos = Player.Position;
                bool wasPlaying = _isPlaying;
                var src = Player.Source;
                Trace($"HwAccel_Changed: requested={Player.HardwareAcceleration} - 現在のファイルを再オープンして即時反映 pos={pos}");
                Player.Source = src;
                Player.Position = pos;
                if (wasPlaying) { Player.Play(); _isPlaying = true; } else { _isPlaying = false; }
            }
            else
            {
                Trace($"HwAccel_Changed: requested={Player.HardwareAcceleration}（次に開くファイルから適用）");
            }
        }

        // コントロールバーの[H/W]/[S/W]ボタン：設定パネルのチェックボックスをトグルするだけで、
        // 実際の再オープン処理はHwAccel_Changedに一本化する（表示はDecodeModeChangedで連動）。
        private void HwStatus_Click(object sender, RoutedEventArgs e)
        {
            HwAccelCheck.IsChecked = !(HwAccelCheck.IsChecked ?? false);
        }

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

        private void AutoPlayNext_Changed(object sender, RoutedEventArgs e) { /* Player_MediaEndedで都度参照するのみ */ }

        // ─────────────────────────────────────────────────────────────────
        // 動画情報タブ更新
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
            {
                Height = 1,
                Background = (Brush)FindResource("TextFaint"),
                Margin = new Thickness(0, 5, 0, 5),
                Opacity = 0.3
            });

            if (Player.Source == null) { Row("状態", "未読み込み"); return; }

            // ── 基本情報 ──
            Row("ファイル名", Path.GetFileName(Player.Source.LocalPath));
            Row("解像度",
                $"{Player.NaturalVideoWidth} × {Player.NaturalVideoHeight}" +
                (Player.NaturalVideoWidth > 0 && Player.NaturalVideoHeight > 0
                    ? $"  ({(Player.NaturalVideoWidth > Player.NaturalVideoHeight ? "横型" : "縦型")})" : ""));
            if (Player.NaturalDuration.HasTimeSpan)
                Row("長さ", Fmt(Player.NaturalDuration.TimeSpan));
            var fi = new FileInfo(Player.Source.LocalPath);
            if (fi.Exists) Row("ファイルサイズ", FormatBytes(fi.Length));

            // ── MediaInfo詳細 ──
            if (_mediaInfo != null && _mediaInfo.Success)
            {
                Sep();
                double fps = _mediaInfo.VideoFrameRate;
                Row("フレームレート", fps > 0 ? $"{fps:F3} fps" : "不明", accent: true);
                Row("映像コーデック", _mediaInfo.VideoCodec ?? "不明", accent: true);
                long vBr = _mediaInfo.VideoBitRate;
                Row("映像ビットレート", vBr > 0 ? $"{vBr / 1000:N0} kbps" : "不明");
                string colorInfo = string.Join(" / ",
                    new[] { _mediaInfo.VideoColorSpace ?? "", _mediaInfo.VideoChromaSubsampling ?? "",
                            _mediaInfo.VideoBitDepth > 0 ? $"{_mediaInfo.VideoBitDepth}bit" : "" }
                    .Where(s => !string.IsNullOrEmpty(s)));
                Row("カラー情報", string.IsNullOrEmpty(colorInfo) ? "不明" : colorInfo);
                Sep();
                Row("音声コーデック", _mediaInfo.AudioCodec ?? "不明");
                int sr = _mediaInfo.AudioSampleRate;
                Row("サンプルレート", sr > 0 ? $"{sr:N0} Hz" : "不明");
                int ch = _mediaInfo.AudioChannelCount;
                Row("音声チャンネル", ch switch
                {
                    1 => "1ch (Mono)",
                    2 => "2ch (Stereo)",
                    6 => "5.1ch",
                    8 => "7.1ch",
                    _ => ch > 0 ? $"{ch}ch" : "不明"
                });
                long aBr = _mediaInfo.AudioBitRate;
                Row("音声ビットレート", aBr > 0 ? $"{aBr / 1000:N0} kbps" : "不明");
                Sep();
                long totalBr = (fi.Exists && Player.NaturalDuration.HasTimeSpan && Player.NaturalDuration.TimeSpan.TotalSeconds > 0)
                    ? (long)(fi.Length * 8 / Player.NaturalDuration.TimeSpan.TotalSeconds) : 0;
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
                case Key.R:
                    Rotate_Click(sender, e);
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
            return FindAdjacentVideo(_lastFilePath, delta);
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
            Player.SpeedRatio = 1.0; // 特殊再生（スロー等）状態は持ち越さず、resumeは常にノーマル速度
            Player.Volume = volume;
            VolumeSlider.Value = volume;
            SpeedSlider.Value = 1.0;

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
            Trace($"StepFrame target={target.TotalSeconds:F3}s");

            // コマ送りは音声再生を伴う必要がないため、音声(Play/Pause)には一切触れず
            // 映像デコードだけをシークして1フレーム表示する専用APIを使用する。
            bool ok = await Player.StepToVideoOnlyAsync(target);
            Trace(ok ? "StepFrame: FrameDisplayed待ち成功" : "StepFrame: 500msタイムアウトで打ち切り");
            Trace($"StepFrame done: target={target} actualPos={Player.Position}");
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
            string? next = FindAdjacentVideo(_lastFilePath, delta);
            Trace($"NavigateFile delta={delta} next={next}");
            if (next == null) return false;
            LoadVideo(next);
            StatusText.Text = Path.GetFileName(next);
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        // エラーログ出力機能
        // ─────────────────────────────────────────────────────────────────
        private void Player_MediaFailed(object sender, VerticalPlayer.Media.FfmpegMediaFailedEventArgs e)
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
