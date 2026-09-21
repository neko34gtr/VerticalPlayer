using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VerticalPlayer.Media;
// AppMessageBox は親名前空間 VerticalPlayer 側にあるため using が必要
using VerticalPlayer;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコ再生モードのメインビュー。
    /// 録画フォルダ種別(NORMAL/MANUAL/EVENT/PARKING)＋ドライブ(または任意フォルダ)選択→
    /// 自動読み込み→Front/Rear動画リスト化→リストから選択再生（Frontは連続再生、Rearは
    /// リア追従設定でFrontに連動 or 独立動作）・シーク追従・センサーHUD表示・走行軌跡マップ・
    /// レジューム再生・次ファイル先読み（OSファイルキャッシュ温め）を担当する。
    /// 動画再生・DNN超解像は既存FfmpegMediaElement/MainWindowの実装に合わせてある。
    /// AI推論(DNN超解像)はこの画面でも有効化可能だが、既定はOFF。
    ///
    /// シークバーはAccelChart（加速度/速度チャート）最上段の独立した帯に統合済み
    /// （旧SeekSliderは廃止）。センサー情報(Hud)は縦/横/自動どの地図モードでも常に
    /// AccelChartの右隣に固定表示する。地図の縦長/横長は右サイドバーの列幅だけを
    /// MapView.OrientationMode（自動/縦/横）とルート方位の自動判定結果から調整する。
    /// </summary>
    public partial class DashcamPlayerView : UserControl
    {
        private List<DashcamMediaGroup> _groups = new();
        private List<DashcamMediaGroup> _frontGroups = new();
        private List<DashcamMediaGroup> _rearGroups = new();

        private DashcamMediaGroup? _currentFrontGroup;
        private DashcamMediaGroup? _currentRearGroup;
        private List<DashcamSensorFrame> _sensorFrames = new();

        private readonly DispatcherTimer _syncTimer;
        // リア再同期: 閾値150ms・クールダウン無しだと、シーク所要時間(約120〜360ms)ぶん必ずリアが
        // 遅れて着地するため毎回(500ms周期)再シークするループに陥り、リアが約10fpsに落ちていた。
        // 閾値を広げ、再同期後はクールダウンを置き、シーク所要時間ぶんを先乗せして着地させる。
        private static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan ResyncCooldown = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxRearLead = TimeSpan.FromMilliseconds(800);
        private readonly System.Diagnostics.Stopwatch _rearResyncWatch = new();
        private bool _rearSettleSamplePending;
        private TimeSpan _rearSeekLead = TimeSpan.FromMilliseconds(250); // 再同期時のシーク先に上乗せする量（実測で自動学習）

        private bool _suppressSelectionEvent;
        private bool _isPlaying;

        // Front/RearのMediaOpenedは非同期でタイミングがずれるため、「再生する意図」を
        // 独立フラグとして保持し、どちらのMediaOpenedが先に来ても正しく再生開始できるようにする。
        private bool _wantsPlaying;

        // 壊れた/読めないファイルが連続した場合に無限ループでスキップし続けないための保険
        private int _consecutiveFrontFailures;

        // リア表示スイッチ(RearVisibleCheck)とは独立して管理する「今リアが実際に再生可能か」の状態。
        // スイッチ自体はユーザー操作でのみ変化させ、このフラグとの組み合わせで実際の表示可否を決める。
        private bool _rearAvailable;

        // 起動時レジューム用: MediaOpened後にシークすべき秒数（該当なければnull）
        private double? _pendingResumeSeconds;

        // MediaInfoNativeでの詳細解析結果。MainWindow本体の_mediaInfoと同じ役割だが、
        // ドラレコ側は音声がFrontのみのためFront基準でのみ解析する。
        private MediaInfoNative? _mediaInfo;

        // レジューム処理中フラグ: 目的のドライブへ切り替わる前の一瞬だけ発生する空振りスキャンで
        // 「見つかりませんでした」警告を出さないようにするためのガード
        private bool _isResuming;

        // 次ファイルの先読み（OSファイルキャッシュ温め）: 同じパスを何度も読み直さないための記録
        private readonly HashSet<string> _prefetchedPaths = new();

        // サムネイル生成キュー（1件ずつ順番に処理。ThumbCapturePlayerという専用の非表示
        // プレイヤーを使い回すため並列実行はしない）
        //private readonly Queue<(DashcamMediaGroup group, string path)> _thumbnailQueue = new();
        private readonly Queue<(DashcamMediaGroup group, string path, bool fast)> _thumbnailQueue = new();
        private readonly HashSet<string> _finalizedThumbnailPaths = new(); // 正規版(DB保存対象)まで到達済みのパス
        private const int FastThumbnailCount = 8; // 初期表示ですぐ目に入る分だけ先に簡易生成する件数
        private bool _thumbnailWorkerRunning;
        private System.Threading.CancellationTokenSource _thumbCts = new(); // 再スキャン時に生成中のバッチを中断する

        // リア(PiP)ドラッグ移動・リサイズ用状態
        // ---- リア時刻ベース同期用 ----
        // Front/Rearのファイル名タイムスタンプは実際の録画開始時刻（実測: 名前+120秒≒最終書込時刻）だが、
        // リアはFrontと位相が異なる（リアの再起動等で0〜100秒超ずれる）。「同名ペア＝同時開始」と
        // みなす従来方式ではずれるため、絶対時刻で対応付ける:
        //   T = Front開始時刻 + Front再生位置、 リア再生位置 = T − リアclip開始時刻
        private sealed record RearClip(DateTime Start, string FilePath);
        private List<RearClip> _rearTimeline = new();
        private string? _currentRearClipPath;        // 時刻ベースで読み込み中のリアclip（独立選択中はnull）
        private DateTime _currentRearClipStart;
        private string? _rearExhaustedPath;          // 想定より早く終了したclip（同じclipを再読込しない）
        private string? _rearFailedPath;             // 開けなかったclip（同上）
        private DateTime? _frontStart;               // 現在のFrontファイルの開始時刻
        private const double RearClipMaxSeconds = 121; // 1clipの最大長(2分)＋余裕

        private bool _pipUserPositioned;
        // リアPiPの位置（映像エリアの空き領域に対する比率0..1、-1=未設定＝既定の左下配置）。
        // 比率で持つのはウィンドウ/フロント倍率が変わっても相対位置を保つため（永続化対象）。
        private double _pipRatioX = -1;
        private double _pipRatioY = -1;
        /// <summary>リア倍率の最小値（＝従来のPiP既定サイズ220×124 ÷ リア映像1920×1080 ≒ 0.115）。
        /// AppSettings.DashcamRearZoomScaleの既定値と揃えること。</summary>
        public const double DefaultRearZoomScale = 0.115;
        private Point? _pipDragStart;
        private Point? _pipDragStartPos;
        private Point? _pipResizeStart;
        private Size? _pipResizeStartSize;

        // ---- シーク（AccelChart最上段の独立した帯から通知される。旧SeekSliderと同等の
        //      ドラッグ間引きプレビュー・クリック即シーク・ホバー時刻プレビューを維持） ----
        private bool _isDragging;
        private bool _dragCompleting;
        private bool _wasPlayingBeforeSeekDrag;
        private bool _seekLiveBusy;
        private double? _seekLivePendingSeconds;

        // 実測fps表示（MainWindowのActualFpsLabelと同じ考え方で1秒集計）
        private int _fpsFrameCount;
        private DateTime _fpsWindowStart = DateTime.UtcNow;

        // 地図の縦長/横長: ルート方位からの自動判定結果（Autoモード用）と、実際に現在適用中の状態。
        // Hudの位置には一切影響しない（Hudは常にAccelChartの右隣に固定）。列幅だけを調整する。
        private bool _lastAutoHorizontal;
        private bool _mapHorizontal;

        /// <summary>ズーム変更・動画オープン時に、映像本来のサイズ×倍率での
        /// ウィンドウフィットをホスト(MainWindow)へ依頼する。引数は動画のネイティブ幅・高さ×倍率(px)。</summary>
        public event Action<double, double>? RequestWindowFit;

        /// <summary>再生中のFrontファイル名が変わるたびに通知する（ホスト側でウィンドウタイトル表示用）。停止時はnull。</summary>
        public event Action<string?>? CurrentFileChanged;

        /// <summary>再生中のFront/Rearファイル名が変わったときに通知する（タイトルバー中央の表示用）。
        /// 引数: Frontファイル名(無ければnull)、Rearファイル名(リアが無い区間・未検出・開けなかった場合はnull)、
        /// リア表示スイッチの状態。</summary>
        public event Action<string?, string?, bool>? PlayingFilesChanged;

        private void DashcamPlayerView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // ドラレコモードを抜けてこの画面が非表示になったら、開いているペア一覧も閉じる
            if (e.NewValue is bool visible && !visible)
                ClosePairListWindow();
        }

        private void NotifyPlayingFiles()
        {
            string? frontPath = _isStopped ? null : _currentFrontGroup?.FrontVideoPath;
            string? rearPath = _isStopped ? null : (_currentRearClipPath ?? _currentRearGroup?.RearVideoPath);
            PlayingFilesChanged?.Invoke(
                frontPath != null ? Path.GetFileName(frontPath) : null,
                rearPath != null ? Path.GetFileName(rearPath) : null,
                RearVisibleCheck?.IsChecked == true);
        }

        public bool RearLinked
        {
            get => RearLinkedCheck.IsChecked == true;
            set => RearLinkedCheck.IsChecked = value;
        }
        /// <summary>
        /// ハードウェアデコード(HW/SW)の切り替え。MainWindow本体のHwAccelCheckと共有する設定で、
        /// トグル時はメイン画面のHwAccel_Changedと同様に、現在開いているFront/Rearを
        /// 位置・再生状態を保ったまま開き直して即時反映する。
        /// </summary>
        public bool HardwareAcceleration
        {
            get => PlayerFront.HardwareAcceleration;
            set
            {
                PlayerFront.HardwareAcceleration = value;
                PlayerRear.HardwareAcceleration = value;
                ReapplyDecodeModeToOpenFiles();
            }
        }

        /// <summary>ノイズリダクション。MainWindow本体のDenoiseCheckと共有する設定（AppSettings.Denoise）。
        /// HW/SW切替と同じく再オープンでないと反映されないため、Reopenする。</summary>
        public bool Denoise
        {
            get => PlayerFront.Denoise;
            set
            {
                PlayerFront.Denoise = value;
                PlayerRear.Denoise = value;
                NdrStatusText.Text = value ? "NDR: ON" : "NDR: OFF";
                ReapplyDecodeModeToOpenFiles();
            }
        }

        /// <summary>ダイナミックコントラスト。MainWindow本体のDynamicContrastCheckと共有
        /// する設定（AppSettings.DynamicContrast）。GPU後段処理のみのためライブ反映で再オープン不要。</summary>
        public bool DynamicContrast
        {
            get => PlayerFront.DynamicContrast;
            set
            {
                PlayerFront.DynamicContrast = value;
                PlayerRear.DynamicContrast = value;
                DcrStatusText.Text = value ? "DCR: ON" : "DCR: OFF";
            }
        }

        /// <summary>デインターレース。MainWindow本体のDeinterlaceCheckと共有する設定。
        /// MainWindow側もライブ反映（再オープンなし）のためこちらも合わせる。</summary>
        public bool Deinterlace
        {
            get => PlayerFront.Deinterlace;
            set
            {
                PlayerFront.Deinterlace = value;
                PlayerRear.Deinterlace = value;
                DeintStatusText.Text = value ? "De-int: ON" : "De-int: OFF";
            }
        }

        /// <summary>fpsカウンタ表示。MainWindow本体のFpsCounterCheck(AppSettings.ShowFpsCounter)と共有。</summary>
        public bool ShowFpsCounter
        {
            get => FpsText.Visibility == Visibility.Visible;
            set => FpsText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ReapplyDecodeModeToOpenFiles()
        {
            if (PlayerFront.Source != null)
            {
                var pos = PlayerFront.Position;
                bool wasPlaying = _isPlaying;
                var src = PlayerFront.Source;
                PlayerFront.Source = src;
                PlayerFront.Position = pos;
                if (wasPlaying) PlayerFront.Play();
            }

            bool rearActive = HasActiveRear();
            if (rearActive && PlayerRear.Source != null)
            {
                var pos = PlayerRear.Position;
                var src = PlayerRear.Source;
                PlayerRear.Source = src;
                PlayerRear.Position = pos;
                if (_isPlaying) PlayerRear.Play();
            }
        }

        /// <summary>リア(PiP)を表示するかどうかのユーザー設定（AppSettings.DashcamRearVisibleと連動、ホスト側で永続化）。</summary>
        public bool RearVisible
        {
            get => RearVisibleCheck.IsChecked == true;
            set => RearVisibleCheck.IsChecked = value;
        }

        /// <summary>リア(PiP)の表示倍率（永続化対象）。リア映像の等倍(オリジナル)を1.0とした縮小倍率で、
        /// 1.0=オリジナル(最大)。最小は従来のPiP既定サイズ相当(DefaultRearZoomScale)。</summary>
        public double RearZoomScale
        {
            get => RearZoomCombo.SelectedItem is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : DefaultRearZoomScale;
            set
            {
                // 保存値が現在の選択肢と完全一致しない場合（選択肢を変更した後など）は最寄りの項目を選ぶ
                ComboBoxItem? best = null;
                double bestDiff = double.MaxValue;
                foreach (var obj in RearZoomCombo.Items)
                {
                    if (obj is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        double diff = Math.Abs(v - value);
                        if (diff < bestDiff) { bestDiff = diff; best = ci; }
                    }
                }
                if (best != null) RearZoomCombo.SelectedItem = best;
            }
        }

        /// <summary>車速OSDの表示ON/OFF（永続化対象。AppSettings.EnableOSDと対応）。</summary>
        public bool SpeedOsdEnabled
        {
            get => SpeedOsdCheck.IsChecked == true;
            set => SpeedOsdCheck.IsChecked = value;
        }

        /// <summary>リア(PiP)の水平位置（永続化対象）。映像エリアの空き幅に対する比率0..1、-1=未設定(既定の左下)。</summary>
        public double RearPipPosX
        {
            get => _pipRatioX;
            set
            {
                _pipRatioX = double.IsNaN(value) || value < 0 ? -1 : Math.Min(1, value);
                _pipUserPositioned = _pipRatioX >= 0 && _pipRatioY >= 0;
                ApplyRearPipLayout();
            }
        }

        /// <summary>リア(PiP)の垂直位置（永続化対象）。映像エリアの空き高さに対する比率0..1、-1=未設定(既定の左下)。</summary>
        public double RearPipPosY
        {
            get => _pipRatioY;
            set
            {
                _pipRatioY = double.IsNaN(value) || value < 0 ? -1 : Math.Min(1, value);
                _pipUserPositioned = _pipRatioX >= 0 && _pipRatioY >= 0;
                ApplyRearPipLayout();
            }
        }

        public double ZoomScale
        {
            get => ZoomCombo.SelectedItem is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 2.0;
            set
            {
                foreach (var obj in ZoomCombo.Items)
                {
                    if (obj is ComboBoxItem ci && double.TryParse((string)ci.Tag, System.Globalization.CultureInfo.InvariantCulture, out var v) && Math.Abs(v - value) < 0.001)
                    {
                        ZoomCombo.SelectedItem = ci;
                        return;
                    }
                }
            }
        }

        private DashcamEventFolder CurrentEventFolder =>
            EventFolderCombo.SelectedItem is ComboBoxItem ci && Enum.TryParse<DashcamEventFolder>((string)ci.Tag, out var v)
                ? v : DashcamEventFolder.Normal;

        // ---- レジューム再生用の現在状態（MainWindow.SaveSettingsから読み取られる） ----
        public string? CurrentDrivePath => (DriveCombo.SelectedItem as DriveOrFolderOption)?.RootPath;
        public string? CurrentGroupKey => _currentFrontGroup?.TimestampKey;
        public double CurrentPositionSeconds => PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.Position.TotalSeconds : 0;
        public string CurrentEventFolderName => CurrentEventFolder.ToString();

        public DashcamPlayerView()
        {
            InitializeComponent();
            IsVisibleChanged += DashcamPlayerView_IsVisibleChanged;

            // 折りたたみ中はサイドバー内容(ScrollBar/コーナー等)を不可視にする（XAML側の指定に依存しない）
            SidebarContent.Opacity = 0;

            // ドラレコは1ファイルあたり約2分間隔で次々切り替わり、かつSDカード等の低速
            // ストレージ運用が前提のため、既存の「パケット先読み（低速ストレージ対策）」
            // パイプラインをこの画面のFront/Rear両方で既定ONにする。
            PlayerFront.PacketPrefetch = true;
            PlayerRear.PacketPrefetch = true;

            // サムネイル生成専用（音は絶対に鳴らさない）。Source切り替えのたびにフルの
            // XAudio2エンジンを作って捨てるのは無駄が大きく、ファイル一覧を高速に舐める
            // サムネイル生成では短時間に何十回も発生するため、音声エンジン自体を作らせない。
            ThumbCapturePlayer.AudioEnabled = false;

            // ハードウェアデコード(D3D11VA、非対応/失敗時は自動でSWへフォールバック)・
            // ノイズリダクション・ダイナミックコントラストも、MainWindow本体の既定(true)に
            // 合わせてドラレコ側でも既定ONにする。デインターレースはMainWindow本体と同じく
            // 既定OFF。
            // ※Deinterlaceプロパティ名はMainWindow側の命名規則(HardwareAcceleration/Denoise/
            //   DynamicContrastと同型のbool)から類推したもの。実際のFfmpegMediaElementの
            //   プロパティ名が異なる場合はここと下のDeintStatusText設定を要調整。
            PlayerFront.HardwareAcceleration = true;
            PlayerRear.HardwareAcceleration = true;
            PlayerFront.Denoise = true;
            PlayerRear.Denoise = true;
            PlayerFront.DynamicContrast = true;
            PlayerRear.DynamicContrast = true;
            PlayerFront.Deinterlace = false;
            PlayerRear.Deinterlace = false;

            // 実際に使われたデコードモード（"HW (D3D11VA)" / "SW"）をコントロールバーに表示して確認できるようにする
            PlayerFront.DecodeModeChanged += mode => Dispatcher.Invoke(() => DecodeModeText.Text = $"デコード: {mode}");
            DeintStatusText.Text = "De-int: OFF";
            NdrStatusText.Text = "NDR: ON";
            DcrStatusText.Text = "DCR: ON";

            PlayerFront.FrameDisplayed += OnFrontFrameDisplayed;

            // 旧SeekSliderの代わりにAccelChart自体がシーク操作を通知してくる
            AccelChart.SeekDragStarted += AccelChart_SeekDragStarted;
            AccelChart.SeekPreview += AccelChart_SeekPreview;
            AccelChart.SeekDragCompleted += AccelChart_SeekDragCompleted;
            AccelChart.HoverTimeChanged += AccelChart_HoverTimeChanged;

            // 地図の表示方向をユーザーが手動変更したら、ルート方位の自動判定結果と合わせて再判定する
            MapView.OrientationModeChanged += () => UpdateEffectiveMapOrientation();

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();
        }

        private void DashcamPlayerView_Loaded(object sender, RoutedEventArgs e)
        {
            // 初回Loaded時点ではRestoreSettings側のレジューム(TryResumeAsync)/起動引数の
            // フォルダ直接読み込みがまだ完了していない場合がある。この状態でRefreshDriveList()が
            // 先頭ドライブ（C:等、通常は録画データが無い）を自動選択してスキャンしてしまうと、
            // 後から正しいドライブへ選び直されても「対応する動画が見つかりませんでした」警告が
            // 誤って一瞬表示される（通常の動画ファイル引数起動でこのダイアログが出る不具合の原因）。
            // レジューム処理中と同じ扱いにして、この自動選択によるスキャンでは警告を出さない。
            _isResuming = true;
            try
            {
                RefreshDriveList();
            }
            finally
            {
                _isResuming = false;
            }
        }

        // ---- 左サイドバー: マウスオーバーで展開 ----

        private void LeftSidebarHost_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 列幅(LeftSidebarColumn)は常に16pxで固定のまま変更しない。展開はLeftSidebarHost自身の
            // Widthだけを広げ、Grid.ColumnSpan+Panel.ZIndexで映像の上にオーバーレイ表示する方式に
            // している（列幅そのものを変更する旧方式だと動画エリアが毎回リサイズされてしまい、
            // D3DImage経由の映像とZ順が競合して一覧が表に出てこない不具合もあったため）。
            LeftSidebarHost.Width = 220;
            CollapsedHint.Visibility = Visibility.Collapsed;
            // SidebarContent自体のVisibilityは常にVisibleのまま変更しない（下記コメント参照）。
            // 折りたたみ中はOpacity=0で不可視にしているため、展開時に表示へ戻す。
            SidebarContent.Opacity = 1;
        }

        private void LeftSidebarHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            LeftSidebarHost.Width = 16;
            CollapsedHint.Visibility = Visibility.Visible;
            // 幅16pxの間は各ListBoxのScrollBar(上下ボタン)がはみ出して見えてしまうため、
            // Visibilityではなく（レイアウトを維持したまま）Opacity=0で完全に不可視にする。
            SidebarContent.Opacity = 0;
        }

        // ---- 録画フォルダ種別・ドライブ選択（選択が変わったら自動で読み込み直す） ----

        private void EventFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveCombo == null) return; // InitializeComponent中の初期選択イベントは無視
            RescanCurrentSelection();
        }

        private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => RefreshDriveList();

        private void PairListButton_Click(object sender, RoutedEventArgs e)
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option || option.IsBrowseOption || option.RootPath == null)
            {
                AppMessageBox.Show(Window.GetWindow(this), "先にドライブ/フォルダを選択してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            var rows = BuildPairOverlapRows();
            if (rows.Count == 0)
            {
                AppMessageBox.Show(Window.GetWindow(this), "Front/Rearのファイルが見つかりません。",
                    "ペア一覧", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            // すでに開いていれば、内容を最新にして前面へ出すだけ（複数開かない）
            if (_pairListWindow != null)
            {
                _pairListWindow.UpdateRows(rows, PairListNote);
                if (_pairListWindow.WindowState == WindowState.Minimized)
                    _pairListWindow.WindowState = WindowState.Normal;
                _pairListWindow.Activate();
                return;
            }

            // モーダル(ShowDialog)ではなく別ウィンドウとして開く。開いている間もメイン画面は操作・再生でき、
            // メイン画面全体が無効化されてサムネイル等が暗く見えることもない。Ownerを指定しているため
            // 常にメイン画面の前面に出て、メイン画面を閉じれば一緒に閉じる。
            var win = new DashcamPairListWindow(rows, PairListNote)
            {
                Owner = Window.GetWindow(this)
            };
            win.Closed += (s, args) =>
            {
                _pairListWindow = null;
                DashcamPlayErrorLogger.Log("[PairList] 閉じた");
            };
            _pairListWindow = win;

            DashcamPlayErrorLogger.Log($"[PairList] 開く(別ウィンドウ) 再生中={_isPlaying} 描画Tier={System.Windows.Media.RenderCapability.Tier >> 16} " +
                $"Front(GPU)={PlayerFront.IsGpuPresenterAvailable} Rear(GPU)={PlayerRear.IsGpuPresenterAvailable}");
            win.Show();
        }

        private DashcamPairListWindow? _pairListWindow;

        private const string PairListNote = "ファイル名の時刻から算出したFront/Rearの対応です（1本=最大2分と仮定した推定）。" +
            "FrontとRearは録画開始の位相がずれているため1対1にはならず、Frontに対して重なるRear、" +
            "Rearに対して重なるFrontを、重なる区間ごとに1行で表示します。「ずれ」はRear開始−Front開始(秒)、" +
            "「区間」はその行が占める各ファイル内の位置です。表示専用で、再生は同じ時刻情報で自動的に同期します。";

        /// <summary>ペア一覧を開いている場合、最新のFront/Rear情報で内容を更新する（ドライブ/フォルダの再スキャン後）。</summary>
        private void RefreshPairListWindow()
        {
            if (_pairListWindow == null) return;
            _pairListWindow.UpdateRows(BuildPairOverlapRows(), PairListNote);
        }

        /// <summary>ペア一覧を閉じる（ドラレコモードを抜けたときなど）。</summary>
        private void ClosePairListWindow()
        {
            _pairListWindow?.Close();
            _pairListWindow = null;
        }

        private const double PairListClipSeconds = 120; // 1ファイルの公称長(2分)

        /// <summary>FrontとRearのファイル名時刻から、重なる区間ごとの対応行（n対n）を算出する。
        /// 1ファイルの終端は「次のファイルの開始」と「開始+2分」のうち早い方とみなす。</summary>
        private List<DashcamPairListWindow.PairOverlapRow> BuildPairOverlapRows()
        {
            var fronts = _frontGroups
                .Select(g => (name: g.FrontVideoPath != null ? Path.GetFileName(g.FrontVideoPath) : null,
                              start: ParseFileStamp(g.FrontVideoPath) ?? g.Timestamp))
                .Where(x => x.name != null && x.start != null)
                .Select(x => (name: x.name!, start: x.start!.Value))
                .OrderBy(x => x.start)
                .ToList();
            var rears = _rearTimeline
                .Select(c => (name: Path.GetFileName(c.FilePath), start: c.Start))
                .ToList();

            DateTime EndOf(List<(string name, DateTime start)> list, int i)
            {
                DateTime end = list[i].start.AddSeconds(PairListClipSeconds);
                if (i + 1 < list.Count && list[i + 1].start < end) end = list[i + 1].start;
                return end;
            }

            // 重なり(front idx, rear idx, 開始, 終了)を全列挙
            var overlaps = new List<(int f, int r, DateTime s, DateTime e)>();
            for (int i = 0; i < fronts.Count; i++)
            {
                DateTime fs = fronts[i].start, fe = EndOf(fronts, i);
                for (int j = 0; j < rears.Count; j++)
                {
                    if (rears[j].start >= fe) break;
                    DateTime re = EndOf(rears, j);
                    DateTime s = fs > rears[j].start ? fs : rears[j].start;
                    DateTime e = fe < re ? fe : re;
                    if ((e - s).TotalSeconds > 0.5)
                        overlaps.Add((i, j, s, e));
                }
            }

            static string Pos(TimeSpan ts) => $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
            static string RangeText(DateTime origin, DateTime s, DateTime e) => $"{Pos(s - origin)} – {Pos(e - origin)}";
            static string Clock(DateTime dt) => dt.ToString("HH:mm:ss");

            var rows = new List<DashcamPairListWindow.PairOverlapRow>();

            foreach (var (f, r, s, e) in overlaps)
            {
                double offset = (rears[r].start - fronts[f].start).TotalSeconds;
                rows.Add(new DashcamPairListWindow.PairOverlapRow
                {
                    Kind = DashcamPairListWindow.RowKind.Overlap,
                    FrontFileName = fronts[f].name,
                    FrontStartText = Clock(fronts[f].start),
                    RearFileName = rears[r].name,
                    RearStartText = Clock(rears[r].start),
                    OffsetText = ((int)Math.Round(offset)).ToString("+0;-0;0"),
                    FrontRangeText = RangeText(fronts[f].start, s, e),
                    RearRangeText = RangeText(rears[r].start, s, e),
                    FrontStart = fronts[f].start,
                    RearStart = rears[r].start,
                    SpanStart = s,
                });
            }

            // Frontのうち、Rearが無い区間
            for (int i = 0; i < fronts.Count; i++)
            {
                DateTime fs = fronts[i].start, fe = EndOf(fronts, i);
                DateTime cursor = fs;
                foreach (var o in overlaps.Where(o => o.f == i).OrderBy(o => o.s))
                {
                    if ((o.s - cursor).TotalSeconds > 0.5)
                        rows.Add(new DashcamPairListWindow.PairOverlapRow
                        {
                            Kind = DashcamPairListWindow.RowKind.FrontOnly,
                            FrontFileName = fronts[i].name,
                            FrontStartText = Clock(fs),
                            FrontRangeText = RangeText(fs, cursor, o.s),
                            FrontStart = fs,
                            SpanStart = cursor,
                        });
                    if (o.e > cursor) cursor = o.e;
                }
                if ((fe - cursor).TotalSeconds > 0.5)
                    rows.Add(new DashcamPairListWindow.PairOverlapRow
                    {
                        Kind = DashcamPairListWindow.RowKind.FrontOnly,
                        FrontFileName = fronts[i].name,
                        FrontStartText = Clock(fs),
                        FrontRangeText = RangeText(fs, cursor, fe),
                        FrontStart = fs,
                        SpanStart = cursor,
                    });
            }

            // Rearのうち、Frontが無い区間
            for (int j = 0; j < rears.Count; j++)
            {
                DateTime rs = rears[j].start, re = EndOf(rears, j);
                DateTime cursor = rs;
                foreach (var o in overlaps.Where(o => o.r == j).OrderBy(o => o.s))
                {
                    if ((o.s - cursor).TotalSeconds > 0.5)
                        rows.Add(new DashcamPairListWindow.PairOverlapRow
                        {
                            Kind = DashcamPairListWindow.RowKind.RearOnly,
                            RearFileName = rears[j].name,
                            RearStartText = Clock(rs),
                            RearRangeText = RangeText(rs, cursor, o.s),
                            RearStart = rs,
                            SpanStart = cursor,
                        });
                    if (o.e > cursor) cursor = o.e;
                }
                if ((re - cursor).TotalSeconds > 0.5)
                    rows.Add(new DashcamPairListWindow.PairOverlapRow
                    {
                        Kind = DashcamPairListWindow.RowKind.RearOnly,
                        RearFileName = rears[j].name,
                        RearStartText = Clock(rs),
                        RearRangeText = RangeText(rs, cursor, re),
                        RearStart = rs,
                        SpanStart = cursor,
                    });
            }

            return rows;
        }

        private void RefreshDriveList()
        {
            string? selectedPath = (DriveCombo.SelectedItem as DriveOrFolderOption)?.RootPath;

            var options = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DriveOrFolderOption
                {
                    DisplayName = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.Name} ({d.VolumeLabel})",
                    RootPath = d.RootDirectory.FullName
                })
                .ToList();

            // 過去にブラウズで選んだ任意フォルダが現在選択中なら、一覧から消えないよう残しておく
            if (DriveCombo.ItemsSource is IEnumerable<DriveOrFolderOption> current)
            {
                foreach (var custom in current.Where(o => !o.IsBrowseOption && o.RootPath != null &&
                                                           !options.Any(x => string.Equals(x.RootPath, o.RootPath, StringComparison.OrdinalIgnoreCase))))
                {
                    options.Add(custom);
                }
            }

            options.Add(new DriveOrFolderOption { DisplayName = "フォルダを選択...", IsBrowseOption = true });

            DriveCombo.ItemsSource = options;

            var restore = options.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, selectedPath, StringComparison.OrdinalIgnoreCase));
            DriveCombo.SelectedItem = restore ?? options.FirstOrDefault(o => !o.IsBrowseOption);
        }

        private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option) return;

            if (option.IsBrowseOption)
            {
                var dlg = new OpenFolderDialog { Title = "ドラレコ映像フォルダ(ROOT)を選択（SSD/HDD等にコピーした場合はそのフォルダ）" };
                if (dlg.ShowDialog() != true)
                {
                    // キャンセル時は選択肢がブラウズ項目のままにならないよう、直前の実ドライブへ戻す
                    var fallback = (DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>)?.FirstOrDefault(o => !o.IsBrowseOption);
                    if (fallback != null) DriveCombo.SelectedItem = fallback;
                    return;
                }

                var custom = new DriveOrFolderOption { DisplayName = dlg.FolderName, RootPath = dlg.FolderName };
                var list = new List<DriveOrFolderOption>((DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>)!);
                list.Insert(list.Count - 1, custom); // 末尾の「フォルダを選択...」の手前に挿入
                DriveCombo.ItemsSource = list;
                DriveCombo.SelectedItem = custom; // ここで再度SelectionChangedが呼ばれ、下のRescanへ進む
                return;
            }

            RescanCurrentSelection();
        }

        // 直近にスキャンしたドライブ/フォルダ（別のドライブ/フォルダへの切替を検出するため）
        private string? _scannedRoot;
        private DashcamEventFolder? _scannedFolder;

        private void RescanCurrentSelection()
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option || option.IsBrowseOption || option.RootPath == null)
                return;

            // 物理ドライブ(または対象フォルダ)が別のものへ変わった場合は、再生を止めてからファイルリストの
            // 処理へ進む。再生中のまま切り替えると、旧ドライブの再生と新ドライブのサムネイル生成が並行して
            // 走り、サムネイルが黒いまま残る等の不具合になっていた。同じドライブの再スキャン（更新ボタン等）では止めない。
            bool targetChanged = _scannedRoot != null
                && (!string.Equals(_scannedRoot, option.RootPath, StringComparison.OrdinalIgnoreCase)
                    || _scannedFolder != CurrentEventFolder);
            if (targetChanged && !_isResuming)
                StopPlayback();

            _scannedRoot = option.RootPath;
            _scannedFolder = CurrentEventFolder;
            ScanDrive(option.RootPath, CurrentEventFolder);

            // レジューム再生の内部処理中は、目的のドライブへ切り替わる前の一瞬だけ発生する
            // 「（まだ違う）先頭ドライブが自動選択された状態」での空振りスキャンでも
            // このメッセージが出てしまっていた（実際には直後に正しいドライブへ切り替わり成功する）。
            // レジューム処理中はこの警告を出さないようにする。
            if (!_isResuming && _frontGroups.Count == 0 && _rearGroups.Count == 0)
            {
                AppMessageBox.Show(Window.GetWindow(this),
                    $"対応する動画が見つかりませんでした（{CurrentEventFolder}フォルダ等を確認してください）。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
            }
        }

        private void ScanDrive(string rootPath, DashcamEventFolder folder)
        {
            _groups = DashcamFileScanner.Scan(rootPath, folder);
            _frontGroups = _groups.Where(g => g.HasFront).ToList();
            _rearGroups = _groups.Where(g => g.HasRear).ToList();
            BuildRearTimeline();
            RefreshPairListWindow();

            FrontList.ItemsSource = _frontGroups;
            RearList.ItemsSource = _rearGroups;

            // 再スキャンでグループ実体が作り直されるため、再生中のグループを新しいリストの
            // 同一キーの実体へ付け替え、選択状態も復元する（レジューム直後にLoadedの
            // RefreshDriveList等が再スキャンすると、選択が消え_currentFrontGroupがリストに
            // 見つからなくなり「次のシーン」が進まなくなっていた）。
            RebindCurrentGroupsAfterScan();

            _thumbnailQueue.Clear(); // 別ドライブ/別フォルダへ切り替えたら古いキューは破棄する
            _thumbCts.Cancel();      // 生成中のバッチも中断する
            _thumbCts = new System.Threading.CancellationTokenSource();
            _finalizedThumbnailPaths.Clear();
            EnqueueThumbnails(_groups);
        }

        // ---- サムネイル生成（中サイズ・SQLiteキャッシュ） ----
        // ファイルパス＋更新日時をキーにSQLiteへキャッシュし、次回以降は再生成せず即表示する。
        // 未キャッシュの場合のみ、非表示のThumbCapturePlayerで対象動画を開き2秒付近の1フレームを
        // RenderTargetBitmapで撮影する（既存のスクリーンショット機能と同じ描画経路を流用）。
        // 1件ずつ順番に処理するため、大量のファイルがあってもUIスレッドや他のデコーダを
        // 圧迫しない（ただし全件そろうまでは後ろの方の項目ほど時間がかかる）。

        private void EnqueueThumbnails(IEnumerable<DashcamMediaGroup> groups)
        {
            var withPath = groups
                .Where(g => g.Thumbnail == null)
                .Select(g => (group: g, path: g.FrontVideoPath ?? g.RearVideoPath))
                .Where(x => x.path != null)
                .Select(x => (x.group, path: x.path!))
                .ToList();

            // リスト順（上から）に積む。ワーカーはFFmpegで直接・並列に生成するため、
            // 従来のfast/finalの二段階は不要（先頭から順に着手され、キャッシュ命中分は一瞬で出る）。
            foreach (var (group, path) in withPath)
                _thumbnailQueue.Enqueue((group, path, fast: false));

            if (!_thumbnailWorkerRunning)
                _ = RunThumbnailWorkerAsync();
        }

        private const int ThumbWidth = 160;
        private const int ThumbHeight = 90;

        /// <summary>
        /// サムネイル生成ワーカー。キューを丸ごと取り出し、複数ファイルを並列に処理する:
        ///   1) DBキャッシュ(パス+更新日時キー)を全件まとめて引き、命中分を並列デコードして一斉に表示（一瞬）
        ///   2) 無ければFFmpegで先頭フレームを直接デコード・縮小（FastThumbnailExtractor。UIスレッド不要）
        ///   3) 抽出に失敗したファイルだけ、従来のプレイヤー経由の生成へフォールバック（逐次）
        /// 生成したサムネイルはDBへ保存する（保存は1本のバックグラウンドタスクで逐次。SQLiteの書き込み競合回避）。
        /// </summary>
        private async Task RunThumbnailWorkerAsync()
        {
            _thumbnailWorkerRunning = true;
            try
            {
                while (_thumbnailQueue.Count > 0)
                {
                    var batch = new List<(DashcamMediaGroup group, string path)>();
                    while (_thumbnailQueue.Count > 0)
                    {
                        var (g, p, _) = _thumbnailQueue.Dequeue();
                        if (_finalizedThumbnailPaths.Add(p)) // 二重生成防止（同じパスは1回だけ）
                            batch.Add((g, p));
                    }
                    if (batch.Count == 0) continue;

                    var ct = _thumbCts.Token;

                    // 1) DBキャッシュを全件まとめて引き、命中分は並列にデコードして一斉に表示する（一瞬で出る）
                    var cachedMap = await Task.Run(() => DashcamThumbnailCache.TryGetMany(batch.Select(b => b.path).ToList()));
                    if (ct.IsCancellationRequested) continue;

                    var hits = new List<(DashcamMediaGroup group, byte[] data)>();
                    var missing = new List<(DashcamMediaGroup group, string path)>();
                    foreach (var item in batch)
                    {
                        if (cachedMap.TryGetValue(item.path, out var data)) hits.Add((item.group, data));
                        else missing.Add(item);
                    }

                    if (hits.Count > 0)
                    {
                        await Task.Run(() => Parallel.ForEach(hits,
                            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                            h =>
                            {
                                var src = BytesToImageSource(h.data);
                                var grp = h.group;
                                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                    new Action(() => grp.Thumbnail = src));
                            }));
                    }

                    // 2) 無かった分だけFFmpegで直接生成する
                    var fallback = new System.Collections.Concurrent.ConcurrentQueue<(DashcamMediaGroup group, string path)>();
                    var toSave = new System.Collections.Concurrent.ConcurrentQueue<(string path, byte[] jpeg)>();
                    int degree = Math.Clamp(Environment.ProcessorCount / 2, 2, 4); // ディスクI/Oが主体のため控えめ

                    try
                    {
                        await Parallel.ForEachAsync(missing,
                            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                            (item, token) =>
                            {
                                var (group, path) = item;
                                ImageSource? source = null;

                                byte[]? bgra = FastThumbnailExtractor.ExtractBgra(path, ThumbWidth, ThumbHeight);
                                if (bgra != null && bgra.Length >= ThumbWidth * ThumbHeight * 4)
                                {
                                    var bmp = BitmapSource.Create(ThumbWidth, ThumbHeight, 96, 96,
                                        PixelFormats.Bgr32, null, bgra, ThumbWidth * 4);
                                    bmp.Freeze();

                                    var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                                    using var ms = new MemoryStream();
                                    encoder.Save(ms);
                                    toSave.Enqueue((path, ms.ToArray()));
                                    source = bmp;
                                }

                                if (source != null)
                                {
                                    var s = source;
                                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                                        new Action(() => group.Thumbnail = s));
                                }
                                else
                                {
                                    fallback.Enqueue(item);
                                }
                                return ValueTask.CompletedTask;
                            });
                    }
                    catch (OperationCanceledException)
                    {
                        continue; // 再スキャンで中断された。次のループで新しいキューを処理する
                    }

                    // 新規生成分のDB保存（1本のバックグラウンドタスクで逐次）
                    if (!toSave.IsEmpty)
                    {
                        var saves = toSave.ToArray();
                        _ = Task.Run(() => DashcamThumbnailCache.SaveBatch(saves.Select(s => (s.path, s.jpeg))));
                    }

                    // FFmpeg直接抽出に失敗したファイルだけ、従来のプレイヤー経由(UIスレッド・逐次)で生成する
                    foreach (var (group, path) in fallback)
                    {
                        if (ct.IsCancellationRequested) break;
                        byte[]? png = await GenerateThumbnailAsync(path, fast: false);
                        if (png == null) continue; // 壊れたファイル等はスキップ
                        await Task.Run(() => DashcamThumbnailCache.Save(path, png));
                        var imageSource = BytesToImageSource(png);
                        Dispatcher.Invoke(() => group.Thumbnail = imageSource);
                    }
                }
            }
            finally
            {
                _thumbnailWorkerRunning = false;
            }
        }

        private static ImageSource BytesToImageSource(byte[] pngBytes)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(pngBytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze(); // UIスレッド以外からでも安全に参照できるようにする
            return bmp;
        }

        private async Task<byte[]?> GenerateThumbnailAsync(string videoPath, bool fast = false)
        {
            // UI要素の操作およびキャプチャは確実にUIスレッドで行う（DispatcherOperationが
            // async delegateを返すため、結果取得には2段階のawaitが必要）。
            var op = Dispatcher.InvokeAsync(async () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler openedHandler = (s, e) => tcs.TrySetResult(true);
                EventHandler<FfmpegMediaFailedEventArgs> failedHandler = (s, e) => tcs.TrySetResult(false);

                ThumbCapturePlayer.MediaOpened += openedHandler;
                ThumbCapturePlayer.MediaFailed += failedHandler;
                try
                {
                    ThumbCapturePlayer.Stop();
                    ThumbCapturePlayer.Source = new Uri(videoPath);

                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    if (completed != tcs.Task || !await tcs.Task)
                        return null; // オープン失敗 or タイムアウト

                    var duration = ThumbCapturePlayer.NaturalDuration.HasTimeSpan
                        ? ThumbCapturePlayer.NaturalDuration.TimeSpan
                        : TimeSpan.FromSeconds(4);

                    if (!fast)
                    {
                        // 簡易生成(fast)はシーク待ちの数百ms〜秒単位を丸ごと省略し、MediaOpened直後の
                        // 先頭フレームをそのまま使う（体感速度優先。正式なDB保存版は後で上書きされる）。
                        var target = TimeSpan.FromSeconds(Math.Min(2, duration.TotalSeconds * 0.3));
                        await ThumbCapturePlayer.StepToVideoOnlyAsync(target, timeoutMs: 3000);
                    }
                    // 画面外(Canvas.Left/Top=-2000)のままだとMeasure/Arrangeが確定しておらず、
                    // RenderTargetBitmap.Render(ThumbCapturePlayer)を直接呼んでも空(透明)の
                    // ビットマップになる不具合が実機で確認された。160x90でレイアウトを明示的に
                    // 確定させ、さらにVisualBrush経由でDrawingVisualへ転写してからキャプチャする
                    // ことで解消している。
                    ThumbCapturePlayer.Measure(new Size(160, 90));
                    ThumbCapturePlayer.Arrange(new Rect(-2000, -2000, 160, 90));
                    ThumbCapturePlayer.UpdateLayout();
                    await Task.Delay(fast ? 30 : 100);

                    var visualBrush = new VisualBrush(ThumbCapturePlayer) { Stretch = Stretch.Fill };
                    var drawingVisual = new DrawingVisual();
                    using (var dc = drawingVisual.RenderOpen())
                    {
                        dc.DrawRectangle(visualBrush, null, new Rect(0, 0, 160, 90));
                    }

                    var rtb = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(drawingVisual);

                    // JPEG(品質80%)でバイト数を大幅カット（SQLiteへ大量保存するため）
                    var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return ms.ToArray();
                }
                catch
                {
                    return null;
                }
                finally
                {
                    ThumbCapturePlayer.MediaOpened -= openedHandler;
                    ThumbCapturePlayer.MediaFailed -= failedHandler;
                    ThumbCapturePlayer.Stop();
                }
            });

            var innerTask = await op;
            return await innerTask;
        }

        // ---- リア時刻ベース同期 ----

        private static DateTime? ParseFileStamp(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string name = Path.GetFileName(path);
            if (name.Length >= 14 && DateTime.TryParseExact(name.AsSpan(0, 14), "yyyyMMddHHmmss",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return null;
        }

        /// <summary>録画フォルダ内の全リアファイルを開始時刻順の連続タイムラインとして構築する。
        /// ペアリング(近傍マッチ/DB)の結果には依存しない。</summary>
        private void BuildRearTimeline()
        {
            var clips = new Dictionary<string, RearClip>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in _groups)
            {
                var st = ParseFileStamp(g.RearVideoPath);
                if (st != null && g.RearVideoPath != null) clips[g.RearVideoPath] = new RearClip(st.Value, g.RearVideoPath);
            }

            var dirs = _groups
                .SelectMany(g => new[] { g.FrontVideoPath, g.RearVideoPath })
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetDirectoryName(p!))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir!, "*_Rear.MP4"))
                    {
                        var st = ParseFileStamp(f);
                        if (st != null) clips[f] = new RearClip(st.Value, f);
                    }
                }
                catch (Exception ex)
                {
                    DashcamPlayErrorLogger.Log($"[RearTimeline] {dir} の列挙に失敗: {ex.Message}");
                }
            }

            _rearTimeline = clips.Values.OrderBy(c => c.Start).ThenBy(c => c.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>時刻tを含むリアclipを返す（無ければnull＝リアの録画が無い区間）。
        /// clipの終端は「次のclipの開始」または開始+2分のうち早い方とみなす。</summary>
        private RearClip? FindRearClipAt(DateTime t)
        {
            var tl = _rearTimeline;
            int lo = 0, hi = tl.Count - 1, idx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (tl[mid].Start <= t) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (idx < 0) return null;

            var c = tl[idx];
            DateTime endEst = c.Start.AddSeconds(RearClipMaxSeconds);
            if (idx + 1 < tl.Count && tl[idx + 1].Start < endEst) endEst = tl[idx + 1].Start;
            if (t >= endEst) return null;
            if (string.Equals(c.FilePath, _rearExhaustedPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(c.FilePath, _rearFailedPath, StringComparison.OrdinalIgnoreCase)) return null;
            return c;
        }

        private bool UseTimeAlignedRear => RearLinked && _frontStart != null && _rearTimeline.Count > 0;

        /// <summary>いまリア側を操作すべき状態か（Play/Pause/シーク/DNN等の対象にするか）。</summary>
        private bool HasActiveRear()
        {
            if (!RearLinked) return _currentRearGroup != null;
            if (UseTimeAlignedRear) return _currentRearClipPath != null;
            return _currentFrontGroup?.HasRear == true;
        }

        /// <summary>現在のFront時刻から読み込むべきリアclipを決め、違っていれば読み込み直す。
        /// Front切替後もリアの現clipが引き続きTを含むなら再読込せず、そのまま再生し続ける。</summary>
        private void ReconcileRear()
        {
            if (!UseTimeAlignedRear) return;

            var t = _frontStart!.Value + PlayerFront.Position;
            var clip = FindRearClipAt(t);
            if (clip == null)
            {
                if (_currentRearClipPath != null) StopRearForGap();
                return;
            }

            if (string.Equals(clip.FilePath, _currentRearClipPath, StringComparison.OrdinalIgnoreCase)) return;
            LoadRearClip(clip, t);
        }

        private void LoadRearClip(RearClip clip, DateTime t)
        {
            DashcamPlayErrorLogger.Log($"[RearClip] T={t:HH:mm:ss.f} → {Path.GetFileName(clip.FilePath)} 位置={(t - clip.Start).TotalSeconds:F1}s " +
                $"(直前={Path.GetFileName(_currentRearClipPath) ?? "(なし)"})");

            _currentRearClipPath = clip.FilePath;
            _currentRearClipStart = clip.Start;
            _currentRearGroup = _rearGroups.FirstOrDefault(g => string.Equals(g.RearVideoPath, clip.FilePath, StringComparison.OrdinalIgnoreCase));
            _rearResyncWatch.Reset();
            _rearSettleSamplePending = false;

            _rearAvailable = false; // 新しいSourceが実際に開き終わるまでは「表示可能」とみなさない
            UpdateRearPipVisibility();
            PlayerRear.Stop();
            PlayerRear.Source = new Uri(clip.FilePath);

            SetListSelection(RearList, _currentRearGroup, scrollToTop: false);
            if (_currentRearGroup != null)
                ScrollSelectedToTop(RearList, _rearGroups.IndexOf(_currentRearGroup));
        }

        /// <summary>リアの録画が無い区間: リアを止めてPiPを黙って隠す（スイッチ自体は触らない）。</summary>
        private void StopRearForGap()
        {
            PlayerRear.Stop();
            _currentRearClipPath = null;
            _currentRearGroup = null;
            _rearAvailable = false;
            UpdateRearPipVisibility();
            SetListSelection(RearList, null, scrollToTop: false);
        }

        /// <summary>Front位置frontPosに対応するリア位置へ合わせる（シーク確定時）。</summary>
        private async Task SeekRearToFrontPositionAsync(TimeSpan frontPos)
        {
            _rearExhaustedPath = null; // シークで時刻が飛ぶため、終了扱い/失敗扱いは解除する
            _rearFailedPath = null;
            _rearResyncWatch.Reset();
            _rearSettleSamplePending = false;

            if (UseTimeAlignedRear)
            {
                var t = _frontStart!.Value + frontPos;
                var clip = FindRearClipAt(t);
                if (clip == null)
                {
                    if (_currentRearClipPath != null) StopRearForGap();
                    return;
                }

                if (!string.Equals(clip.FilePath, _currentRearClipPath, StringComparison.OrdinalIgnoreCase))
                {
                    LoadRearClip(clip, t); // 開き終わり(MediaOpened)で位置を合わせる
                    return;
                }
                await PlayerRear.StepToVideoOnlyAsync(t - clip.Start, timeoutMs: 2000);
                return;
            }

            if (HasActiveRear())
                await PlayerRear.StepToVideoOnlyAsync(frontPos, timeoutMs: 2000);
        }

        // ---- リスト選択の同期補助 ----

        /// <summary>選択イベント(再生開始)を発火させずにリストの選択項目だけを設定する。</summary>
        private void SetListSelection(ListBox list, DashcamMediaGroup? group, bool scrollToTop)
        {
            bool prev = _suppressSelectionEvent;
            _suppressSelectionEvent = true;
            try { list.SelectedItem = group; }
            finally { _suppressSelectionEvent = prev; }

            if (group == null || !scrollToTop) return;

            var source = ReferenceEquals(list, FrontList) ? _frontGroups : _rearGroups;
            int idx = source.IndexOf(group);
            // ItemsSource差し替え直後はレイアウト前でオフセットが効かないため、Loaded優先度で遅延実行する
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ScrollSelectedToTop(list, idx)));
        }

        private void RebindCurrentGroupsAfterScan()
        {
            if (_currentFrontGroup != null)
            {
                string key = _currentFrontGroup.TimestampKey;
                var nf = _frontGroups.FirstOrDefault(g => g.TimestampKey == key);
                if (nf != null)
                {
                    _currentFrontGroup = nf;
                    SetListSelection(FrontList, nf, scrollToTop: true);
                }
            }

            if (_currentRearGroup != null)
            {
                string key = _currentRearGroup.TimestampKey;
                var nr = _rearGroups.FirstOrDefault(g => g.TimestampKey == key);
                if (nr != null)
                {
                    _currentRearGroup = nr;
                    SetListSelection(RearList, nr, scrollToTop: true);
                }
            }
        }

        // ---- リスト選択 → 再生 ----

        private void FrontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FrontList.SelectedItem != null)
                FrontList.ScrollIntoView(FrontList.SelectedItem);

            if (_suppressSelectionEvent) return;
            if (FrontList.SelectedItem is not DashcamMediaGroup group) return;
            PlayFrontGroup(group);
        }

        private void RearList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RearList.SelectedItem != null)
                RearList.ScrollIntoView(RearList.SelectedItem);

            if (_suppressSelectionEvent) return;
            if (RearList.SelectedItem is not DashcamMediaGroup group) return;

            // リアリストは常に選択可能（従来はリア追従中は無効化していたため「手動でここから見たい」
            // ケースに一切対応できなかった）。手動で選ぶ＝追従を自動解除する意図とみなす。
            if (RearLinked)
                RearLinkedCheck.IsChecked = false;

            PlayRearGroup(group);
        }

        private void PlayFrontGroup(DashcamMediaGroup group)
        {
            _currentFrontGroup = group;
            _isStopped = false;
            // 直前のFrontの次(約2分後)に続くファイルへの切替（連続再生・次のシーン）では、リアの
            // 「終了済み/開けなかった」記録を維持する。リアはFrontと位相が違い、まだ終わったはずの
            // 直前のリアclipが新しいFrontの開始時刻にも含まれて見えることがあり、リセットすると
            // 同じclipを読み直してPiPが1〜2秒途切れていた。リストの選択などで別の場所へ飛ぶ場合だけリセットする。
            var newFrontStart = ParseFileStamp(group.FrontVideoPath) ?? group.Timestamp;
            bool sequentialSwitch = _frontStart != null && newFrontStart != null
                && Math.Abs((newFrontStart.Value - _frontStart.Value).TotalSeconds - NominalClipSeconds) <= 15;
            _frontStart = newFrontStart;
            if (!sequentialSwitch)
            {
                _rearExhaustedPath = null;
                _rearFailedPath = null;
            }
            _rearResyncWatch.Reset(); // 新しいファイルでは再同期の状態を初期化する
            _rearSettleSamplePending = false;
            CurrentFileChanged?.Invoke(Path.GetFileName(group.FrontVideoPath) ?? group.TimestampKey);
            NotifyPlayingFiles();
            _sensorFrames = new List<DashcamSensorFrame>(); // Frontの動画長が判明してからParseし直す（MediaOpened側）
            _wantsPlaying = true; // Front/RearどちらのMediaOpenedが先に来ても再生開始させる意図フラグ

            if (group.FrontVideoPath != null)
            {
                PlayerFront.Stop(); // 前回のOpen失敗等で内部状態が残っていても確実にリセットしてから開く
                PlayerFront.Source = new Uri(group.FrontVideoPath);
            }

            // リア連動の診断ログ（「次のシーンでフロントだけ切り替わりリアが付いて来ない」調査用）
            DashcamPlayErrorLogger.Log($"[Scene] Front={group.TimestampKey} 開始={_frontStart:HH:mm:ss} RearLinked={RearLinked} 時刻同期={UseTimeAlignedRear} HasRear={group.HasRear} " +
                $"RearPath={group.RearVideoPath ?? "(なし)"} 直前のリア={_currentRearGroup?.TimestampKey ?? "(null)"}");

            if (UseTimeAlignedRear)
            {
                ReconcileRear(); // 絶対時刻でリアclipを選ぶ（現clipが引き続きTを含むなら再読込しない）
            }
            else if (RearLinked)
            {
                // 時刻が取れない場合のフォールバック（従来のグループ単位の追従）
                if (group.HasRear)
                {
                    PlayRearGroup(group, keepListSelectionOnly: true); // 内部でRearListの選択色も更新される
                    int rearIdx = _rearGroups.IndexOf(group);
                    ScrollSelectedToTop(RearList, rearIdx);
                }
                else
                {
                    SetListSelection(RearList, null, scrollToTop: false);
                    // フロントと同名のリアファイルが存在しないケース。スイッチ自体はユーザーの
                    // 操作結果のまま変更せず（勝手にOFFにしない）、表示できないので黙って隠すだけにする。
                    PlayerRear.Stop();
                    _currentRearGroup = null;
                    _rearAvailable = false;
                    UpdateRearPipVisibility();
                }
            }
        }

        private void PlayRearGroup(DashcamMediaGroup group, bool keepListSelectionOnly = false)
        {
            _currentRearGroup = group;
            _currentRearClipPath = null; // 独立選択/フォールバックでは時刻ベースの管理から外す
            _wantsPlaying = true; // 独立選択(リア追従OFF時)から呼ばれた場合もここで意図をセットする
            _rearAvailable = false; // 新しいSourceが実際に開き終わるまでは「表示可能」とみなさない
            UpdateRearPipVisibility();

            if (group.RearVideoPath != null)
            {
                PlayerRear.Stop(); // 前回Open失敗(エラー記録ファイル等)の内部状態を必ずリセットしてから開く
                PlayerRear.Source = new Uri(group.RearVideoPath);
            }

            // 追従(keepListSelectionOnly)時もリアリストの選択色は付ける（従来はスキップしており
            // フロント追従再生中にリア側だけ選択色が付かなかった）。スクロール位置だけは
            // 呼び出し側(ScrollSelectedToTop)に任せる。
            SetListSelection(RearList, group, scrollToTop: false);
            if (!keepListSelectionOnly)
                RearList.ScrollIntoView(group);
        }

        // ---- 再生制御 ----

        private async void PlayerFront_MediaOpened(object sender, RoutedEventArgs e)
        {
            _consecutiveFrontFailures = 0;
            PlayerFront.ResetDnnEngineForNewFile();

            // 動画詳細情報の取得と表示（MainWindow.Player_MediaOpenedと同じ仕組み）
            if (PlayerFront.Source?.LocalPath != null)
                AnalyzeAndShowMediaInfo(PlayerFront.Source.LocalPath);

            var duration = PlayerFront.NaturalDuration.HasTimeSpan
                ? PlayerFront.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

            var sensorGroup = _currentFrontGroup;
            string? nmeaPath = sensorGroup?.FrontNmeaPath ?? sensorGroup?.RearNmeaPath;
            var sensorFrames = nmeaPath != null
                ? NmeaSensorParser.Parse(nmeaPath, sensorGroup?.Timestamp, duration)
                : new List<DashcamSensorFrame>();

            // ファイルの先頭/末尾が測位ロスト(トンネル等)なら、前後のファイルの測位速度を取り込んで
            // ロスト区間の推定速度を補間し直す（ファイルをまたぐ長いトンネルにも対応）
            if (nmeaPath != null && sensorGroup != null && sensorFrames.Count > 0 && NmeaSensorParser.MaxEstimateGapSeconds > 0
                && (!sensorFrames[0].HasGpsFix || !sensorFrames[^1].HasGpsFix))
            {
                bool needBefore = !sensorFrames[0].HasGpsFix;
                bool needAfter = !sensorFrames[^1].HasGpsFix;
                var gapContext = await Task.Run(() => BuildSpeedGapContext(sensorGroup, needBefore, needAfter));
                if (!ReferenceEquals(_currentFrontGroup, sensorGroup)) return; // 待機中に別ファイルへ切り替わった
                if (gapContext != null)
                    sensorFrames = NmeaSensorParser.Parse(nmeaPath, sensorGroup.Timestamp, duration, gapContext);
            }
            _sensorFrames = sensorFrames;
            // グラフはファイル全体分をここで一度だけ計算して描画する（毎フレーム全点を再計算して
            // いた従来方式はスレッド負荷が無駄に高かったため）。再生中はSetPlayhead()で現在位置を
            // 反映するだけにする。チャート自体がシークUIも兼ねるため、Maximum等の設定は不要
            // （AccelChart内部でこのdurationを元に比率計算する）。
            AccelChart.SetFullTrack(_sensorFrames, duration);

            // レジューム再生: 位置決めは必ずPlay()より先に行う。
            // StepToVideoOnlyAsyncはドラッグシーク確定時と同じ「指定フレームへ正確に着地させたら
            // 内部的にPause()する」設計のメソッドのため、これより先にPlay()してしまうと
            // 音声(hidden MediaElement側)だけ既に再生が進み、映像(AVEngine側)はシーク後に
            // Pause()されたまま……という「映像は止まったまま音声だけ流れる」不具合になる
            // （実機ログで確認: Play()→Seek()→catch-up→最後にPause()で終わっており、
            // その後Play()を呼び直していなかったのが原因）。
            //
            // また、レジューム直後のシークはSDカード等の低速ストレージからの読み出しが
            // まだ間に合っておらず本来遅くなりがちな処理のため、同じストレージへ同時に
            // アクセスするRefreshMapBufferAsync(前後最大21ファイル分のNMEA読み直し)や
            // SchedulePrefetchIfNeeded(次ファイルの先読み)は、あえてこのシークが完了した
            // "後"に回している（以前は先に走らせておりI/Oが競合して余計に遅くなっていた）。
            if (_pendingResumeSeconds is double resumeSec)
            {
                _pendingResumeSeconds = null;
                if (resumeSec > 0.5 && resumeSec < duration.TotalSeconds)
                    await PlayerFront.StepToVideoOnlyAsync(TimeSpan.FromSeconds(resumeSec), timeoutMs: 3000);
            }

            if (_wantsPlaying)
            {
                PlayerFront.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
            }

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
            {
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
                UpdateZoomAvailability();
            }

            if (_currentFrontGroup != null)
                _ = RefreshMapBufferAsync(_currentFrontGroup);

            SchedulePrefetchIfNeeded();
        }

        // MediaInfoNative で詳細解析。MainWindow.AnalyzeAndShowMediaInfoと同じ仕組み。
        // 音声はFrontのみのため、Front基準でのみ解析する。
        private void AnalyzeAndShowMediaInfo(string path)
        {
            try
            {
                var mi = new MediaInfoNative(path);
                if (!mi.Success)
                {
                    DashcamPlayErrorLogger.Log($"[MediaInfo] failed for {path}");
                    _mediaInfo?.Dispose();
                    _mediaInfo = null;
                    UpdateCodecStatusBar();
                    return;
                }

                _mediaInfo?.Dispose();
                _mediaInfo = mi;
                UpdateCodecStatusBar();
            }
            catch (Exception ex)
            {
                DashcamPlayErrorLogger.Log($"[MediaInfo] EXCEPTION: {ex.Message}");
            }
        }

        // コントロールバーにMainWindow本体と同じコーデック略称を表示する。
        // AnalyzeAndShowMediaInfoの解析が終わるたびに呼ばれる。
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

        private void PlayerRear_MediaOpened(object sender, RoutedEventArgs e)
        {
            DashcamPlayErrorLogger.Log($"[RearOpened] Rear={_currentRearGroup?.TimestampKey ?? "(null)"} wantsPlaying={_wantsPlaying} " +
                $"Front位置={PlayerFront.Position.TotalSeconds:F2}s Rear映像={PlayerRear.NaturalVideoWidth}x{PlayerRear.NaturalVideoHeight}");
            PlayerRear.ResetDnnEngineForNewFile();

            // 時刻ベース同期: リアclipがFrontより前から始まっている/Front再生が進んでいる場合は、
            // 現在のFront時刻に対応する位置から始める（開き終わりまでにFrontが進んだ分も含む）
            if (RearLinked && _frontStart != null && _currentRearClipPath != null)
            {
                var desired = (_frontStart.Value + PlayerFront.Position) - _currentRearClipStart;
                if (desired > TimeSpan.FromMilliseconds(300))
                {
                    var target = desired + _rearSeekLead;
                    if (PlayerRear.NaturalDuration.HasTimeSpan && target > PlayerRear.NaturalDuration.TimeSpan)
                        target = PlayerRear.NaturalDuration.TimeSpan;
                    PlayerRear.Position = target;
                }
            }

            _rearAvailable = true;
            ApplyRearPipLayout(); // 「オリジナル」倍率はリア映像の実サイズで再計算する
            UpdateRearPipVisibility();
            if (_wantsPlaying)
                PlayerRear.Play();
        }

        private void PlayerFront_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            _consecutiveFrontFailures++;
            DashcamPlayErrorLogger.Log($"[MediaFailed] Front={_currentFrontGroup?.TimestampKey ?? "(null)"} 連続失敗={_consecutiveFrontFailures}回");
            if (_consecutiveFrontFailures >= 3)
            {
                _consecutiveFrontFailures = 0;
                AppMessageBox.Show(Window.GetWindow(this),
                    "複数のFront動画が連続して再生できませんでした。ファイルの状態を確認してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
                return;
            }
            AdvanceToNextFrontScene();
        }

        private void PlayerRear_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            // 「エラー記録」ファイル等で開けない場合。スイッチには一切触らず（ユーザーが自分で
            // 操作したものを勝手にOFFにするのは体験として最悪、というご指摘のため）、
            // 単に「今は表示できるものが無い」状態にしてPiPを黙って隠すだけにする。
            DashcamPlayErrorLogger.Log($"[MediaFailed] Rear={_currentRearGroup?.TimestampKey ?? "(null)"}");
            PlayerRear.Stop();
            _rearFailedPath = _currentRearClipPath; // 時刻ベース同期で同じclipを読み直し続けないよう記録
            _currentRearClipPath = null;
            _currentRearGroup = null;
            _rearAvailable = false;
            UpdateRearPipVisibility();
        }

        private void PlayerFront_MediaEnded(object sender, RoutedEventArgs e)
        {
            DashcamPlayErrorLogger.Log($"[MediaEnded] Front={_currentFrontGroup?.TimestampKey ?? "(null)"}");
            AdvanceToNextFrontScene();
        }

        private void NextSceneButton_Click(object sender, RoutedEventArgs e) => AdvanceToNextFrontScene();

        private void PrevSceneButton_Click(object sender, RoutedEventArgs e) => GoToPreviousFrontScene();

        /// <summary>Frontリストの1つ前のグループを再生する（次のシーンの逆方向）。</summary>
        private void GoToPreviousFrontScene()
        {
            if (_currentFrontGroup is null)
            {
                DashcamPlayErrorLogger.Log("[Previous] _currentFrontGroupがnullのため中止");
                return;
            }

            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0)
            {
                string curKey = _currentFrontGroup.TimestampKey;
                idx = _frontGroups.FindIndex(g => g.TimestampKey == curKey); // 再スキャンで実体が入れ替わっていてもキーで探す
            }
            if (idx < 0)
            {
                DashcamPlayErrorLogger.Log($"[Previous] {_currentFrontGroup.TimestampKey}が_frontGroupsに見つからず中止");
                return;
            }
            if (idx <= 0)
            {
                DashcamPlayErrorLogger.Log($"[Previous] {_currentFrontGroup.TimestampKey}は先頭ファイルのため中止");
                return;
            }

            var prev = _frontGroups[idx - 1];
            SetListSelection(FrontList, prev, scrollToTop: false);
            ScrollSelectedToTop(FrontList, idx - 1);
            PlayFrontGroup(prev);
        }

        private void AdvanceToNextFrontScene()
        {
            if (_currentFrontGroup is null)
            {
                DashcamPlayErrorLogger.Log("[Advance] _currentFrontGroupがnullのため中止");
                return;
            }

            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0)
            {
                string curKey = _currentFrontGroup.TimestampKey;
                idx = _frontGroups.FindIndex(g => g.TimestampKey == curKey); // 再スキャンで実体が入れ替わっていてもキーで探す
            }
            if (idx < 0)
            {
                DashcamPlayErrorLogger.Log($"[Advance] {_currentFrontGroup.TimestampKey}が_frontGroupsに見つからず中止（再スキャンで参照が失われた可能性）");
                return;
            }
            if (idx + 1 >= _frontGroups.Count)
            {
                DashcamPlayErrorLogger.Log($"[Advance] {_currentFrontGroup.TimestampKey}は最終ファイルのため中止");
                return;
            }

            var next = _frontGroups[idx + 1];
            SetListSelection(FrontList, next, scrollToTop: false);

            // 連続再生の見た目: 再生中のファイルが常にリストの一番上に来て、後続が下から
            // 順々にせり上がって見えるようにする（単なるScrollIntoViewだと最小限しか動かない）。
            ScrollSelectedToTop(FrontList, idx + 1);

            PlayFrontGroup(next);
        }

        private void PlayerRear_MediaEnded(object sender, RoutedEventArgs e)
        {
            DashcamPlayErrorLogger.Log($"[RearEnded] Rear={_currentRearGroup?.TimestampKey ?? "(null)"} RearLinked={RearLinked} Front位置={PlayerFront.Position.TotalSeconds:F2}s");
            if (RearLinked)
            {
                // 追従中: 次のclipへの切替は時刻ベース(ReconcileRear)が行う。終了したclipは再読込しない
                _rearExhaustedPath = _currentRearClipPath;
                return;
            }
            if (_currentRearGroup is null) return;
            int idx = _rearGroups.IndexOf(_currentRearGroup);
            if (idx < 0)
            {
                string curKey = _currentRearGroup.TimestampKey;
                idx = _rearGroups.FindIndex(g => g.TimestampKey == curKey);
            }
            if (idx < 0 || idx + 1 >= _rearGroups.Count) return;

            PlayRearGroup(_rearGroups[idx + 1]);
            PlayerRear.Play();
        }

        /// <summary>選択中の項目をリストの一番上（ビューポート先頭）へスクロールする。</summary>
        private static void ScrollSelectedToTop(ListBox listBox, int index)
        {
            if (index < 0) return;
            if (FindScrollViewer(listBox) is ScrollViewer sv)
                sv.ScrollToVerticalOffset(index); // ModernListでVirtualizingPanel.ScrollUnit="Item"を設定済み
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindScrollViewer(child);
                if (found != null) return found;
            }
            return null;
        }

        // Segoe Fluent Icons / Segoe MDL2 Assets のグリフ（再生 / 一時停止）
        private const string PlayGlyph = "\uE768";
        private const string PauseGlyph = "\uE769";

        /// <summary>再生/一時停止ボタンのアイコンとTipsを状態に合わせる（playing=trueなら「一時停止」を表示）。</summary>
        private void SetPlayPauseIcon(bool playing)
        {
            PlayPauseButton.Content = playing ? PauseGlyph : PlayGlyph;
            PlayPauseButton.ToolTip = playing ? "一時停止" : (_isStopped ? "再生（先頭から）" : "再生");
        }

        // 停止ボタンで止めた状態（映像を消している）。この間は再生ボタンで現在のシーンを先頭から開き直す。
        private bool _isStopped;

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStopped)
            {
                if (_currentFrontGroup != null)
                    PlayFrontGroup(_currentFrontGroup); // 内部で_isStopped解除・アイコン更新
                return;
            }

            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                SetPlayPauseIcon(false);
                _wantsPlaying = false;
            }
            else
            {
                PlayerFront.Play();
                if (HasActiveRear()) PlayerRear.Play();
                SetPlayPauseIcon(true);
                _wantsPlaying = true;
            }
            _isPlaying = !_isPlaying;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback(keepCurrent: true);

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PlayerFront.Volume = e.NewValue; // Rearは常時Volume=0（音声はFrontのみ）
        }

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFrontGroup?.FrontVideoPath == null || _isStopped)
            {
                AppMessageBox.Show(Window.GetWindow(this), "Front動画を再生してから撮影してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
                return;
            }

            try
            {
                int w = (int)Math.Max(1, PlayerFront.ActualWidth);
                int h = (int)Math.Max(1, PlayerFront.ActualHeight);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(PlayerFront);

                string dir = Path.Combine(AppContext.BaseDirectory, "Screenshots");
                Directory.CreateDirectory(dir);
                string fileName = $"{_currentFrontGroup.TimestampKey}_{PlayerFront.Position:hh\\-mm\\-ss\\-fff}.png";
                string path = Path.Combine(dir, fileName);

                using var fs = new FileStream(path, FileMode.Create);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                encoder.Save(fs);
            }
            catch (Exception ex)
            {
                AppMessageBox.Show(Window.GetWindow(this), $"スクリーンショットの保存に失敗しました。\n{ex.Message}",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode: true);
            }
        }

        /// <summary>再生を停止して映像を消す。keepCurrent=trueは停止ボタン用: 現在のシーン(_currentFrontGroup)を
        /// 覚えておき、再生ボタンで先頭から再生し直せるようにする。既定(false)はドライブ切替・モード終了用で、
        /// 現在のシーンも破棄する（別ドライブの古いシーンを再生し直してしまわないため）。</summary>
        public void StopPlayback(bool keepCurrent = false)
        {
            PlayerFront.Stop();
            PlayerRear.Stop();
            // Stop()だけでは最後のフレームが画面に残り、一時停止と見分けがつかない（＝停止したつもりが
            // 一時停止に見え、再生ボタンも効かない状態になっていた）。映像領域を空にする。
            PlayerFront.ClearDisplay();
            PlayerRear.ClearDisplay();
            _rearAvailable = false;
            UpdateRearPipVisibility();
            _isStopped = keepCurrent && _currentFrontGroup != null;
            if (!keepCurrent)
            {
                _currentFrontGroup = null;
                _currentRearGroup = null;
            }
            AccelChart.SetPlayhead(TimeSpan.Zero);
            Hud.UpdateFrame(null);
            _currentRearClipPath = null;
            _frontStart = null;          // 次に再生を始めるときは連続再生扱いにしない
            _rearExhaustedPath = null;
            _rearFailedPath = null;
            _isPlaying = false;
            _wantsPlaying = false;
            SetPlayPauseIcon(false);
            UpdateSpeedOsd(null);
            CurrentFileChanged?.Invoke(null);
            NotifyPlayingFiles();
        }

        private void RearVisibleCheck_Changed(object sender, RoutedEventArgs e) => UpdateRearPipVisibility();

        /// <summary>
        /// 「リア表示」スイッチはユーザーが完全に手動で制御するものとして扱う（コード側から
        /// 勝手にON/OFFを書き換えることは一切しない）。実際にPiPを見せるかどうかは
        /// 「スイッチがONか」と「今リアが実際に再生可能か(_rearAvailable)」の掛け算で決まる。
        /// 表示できない間はスイッチがONのままでも黙って隠すだけにし、後で表示可能になれば
        /// （このメソッドを呼び直すことで）ユーザーは何も操作し直さなくても自動的に映るようになる。
        /// </summary>
        private void UpdateRearPipVisibility()
        {
            bool show = RearVisibleCheck.IsChecked == true && _rearAvailable;
            RearPipBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // リア倍率の設定UIは「リア表示スイッチがON」の間だけ出す（リアが今再生可能かは問わない）
            if (RearZoomPanel != null)
                RearZoomPanel.Visibility = RearVisibleCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            NotifyPlayingFiles(); // リアの切替/消失/スイッチ変更がタイトルバー表示へ反映される

            if (show && _wantsPlaying)
                PlayerRear.Play();
        }
        private void RearPipCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyRearPipLayout();

        /// <summary>リア倍率(リア映像の等倍を1.0とした縮小倍率)に応じたPiPサイズを返す。映像エリアより大きくなる場合は
        /// 縦横比を保って収まるよう縮小する（「オリジナル」も映像エリアに入らなければ収まる最大サイズになる）。</summary>
        private Size CalcRearPipSize(double scale)
        {
            double nw = PlayerRear.NaturalVideoWidth;
            double nh = PlayerRear.NaturalVideoHeight;
            if (nw <= 0 || nh <= 0) { nw = 1920; nh = 1080; } // リア未オープン時の暫定値（オープン後に再計算される）
            if (scale <= 0.0) scale = DefaultRearZoomScale;

            double w = nw * scale;
            double h = nh * scale;

            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw > 0 && ch > 0)
            {
                double f = Math.Min(1.0, Math.Min(cw / w, ch / h));
                w *= f;
                h *= f;
            }
            return new Size(w, h);
        }

        /// <summary>サイズと位置を保存済みの倍率・比率から組み立て直す（Canvasのサイズ確定時、リアオープン時、復元時）。</summary>
        private void ApplyRearPipLayout()
        {
            if (RearPipBorder == null || RearPipCanvas == null || PlayerRear == null) return;
            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return; // まだレイアウト前。SizeChangedで再度呼ばれる

            var sz = CalcRearPipSize(RearZoomScale);
            RearPipBorder.Width = sz.Width;
            RearPipBorder.Height = sz.Height;

            double freeW = Math.Max(0, cw - sz.Width);
            double freeH = Math.Max(0, ch - sz.Height);
            if (_pipUserPositioned && _pipRatioX >= 0 && _pipRatioY >= 0)
            {
                Canvas.SetLeft(RearPipBorder, _pipRatioX * freeW);
                Canvas.SetTop(RearPipBorder, _pipRatioY * freeH);
            }
            else
            {
                Canvas.SetLeft(RearPipBorder, Math.Min(12, freeW));
                Canvas.SetTop(RearPipBorder, Math.Max(0, ch - sz.Height - 12));
            }
        }

        /// <summary>リア倍率コンボ変更時: サイズだけ差し替える（位置は左上を維持しつつ映像エリア内へ収める）。</summary>
        private void ApplyRearPipSize()
        {
            if (RearPipBorder == null || RearPipCanvas == null || PlayerRear == null) return;
            double cw = RearPipCanvas.ActualWidth, ch = RearPipCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0)
            {
                var early = CalcRearPipSize(RearZoomScale);
                RearPipBorder.Width = early.Width;
                RearPipBorder.Height = early.Height;
                return;
            }

            double curLeft = Canvas.GetLeft(RearPipBorder);
            double curTop = Canvas.GetTop(RearPipBorder);
            if (!_pipUserPositioned || double.IsNaN(curLeft) || double.IsNaN(curTop))
            {
                ApplyRearPipLayout();
                return;
            }

            var sz = CalcRearPipSize(RearZoomScale);
            RearPipBorder.Width = sz.Width;
            RearPipBorder.Height = sz.Height;
            Canvas.SetLeft(RearPipBorder, Math.Clamp(curLeft, 0, Math.Max(0, cw - sz.Width)));
            Canvas.SetTop(RearPipBorder, Math.Clamp(curTop, 0, Math.Max(0, ch - sz.Height)));
            UpdateRearPipRatiosFromCurrent();
        }

        /// <summary>現在のPiP位置を空き領域に対する比率として記録する（永続化用）。</summary>
        private void UpdateRearPipRatiosFromCurrent()
        {
            double left = Canvas.GetLeft(RearPipBorder);
            double top = Canvas.GetTop(RearPipBorder);
            if (double.IsNaN(left) || double.IsNaN(top)) return;

            double freeW = RearPipCanvas.ActualWidth - RearPipBorder.Width;
            double freeH = RearPipCanvas.ActualHeight - RearPipBorder.Height;
            _pipRatioX = freeW > 1 ? Math.Clamp(left / freeW, 0, 1) : 0;
            _pipRatioY = freeH > 1 ? Math.Clamp(top / freeH, 0, 1) : 0;
        }

        private void RearZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyRearPipSize();

        private void RearPipBorder_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipUserPositioned = true;
            _pipDragStart = e.GetPosition(RearPipCanvas);
            _pipDragStartPos = new Point(Canvas.GetLeft(RearPipBorder), Canvas.GetTop(RearPipBorder));
            RearPipBorder.CaptureMouse();
            e.Handled = true;
        }

        private void RearPipBorder_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_pipDragStart is not Point start || _pipDragStartPos is not Point startPos) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var cur = e.GetPosition(RearPipCanvas);
            double newLeft = startPos.X + (cur.X - start.X);
            double newTop = startPos.Y + (cur.Y - start.Y);

            // 映像エリアの外へ出て行方不明にならないよう範囲内にクランプする
            newLeft = Math.Clamp(newLeft, 0, Math.Max(0, RearPipCanvas.ActualWidth - RearPipBorder.Width));
            newTop = Math.Clamp(newTop, 0, Math.Max(0, RearPipCanvas.ActualHeight - RearPipBorder.Height));

            Canvas.SetLeft(RearPipBorder, newLeft);
            Canvas.SetTop(RearPipBorder, newTop);
        }

        private void RearPipBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_pipDragStart != null) UpdateRearPipRatiosFromCurrent();
            _pipDragStart = null;
            _pipDragStartPos = null;
            RearPipBorder.ReleaseMouseCapture();
        }

        private void RearPipResizeGrip_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipResizeStart = e.GetPosition(RearPipCanvas);
            _pipResizeStartSize = new Size(RearPipBorder.Width, RearPipBorder.Height);
            RearPipResizeGrip.CaptureMouse();
            e.Handled = true; // Border側のドラッグ判定へ伝播させない
        }

        private void RearPipResizeGrip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_pipResizeStart is not Point start || _pipResizeStartSize is not Size startSize) return;
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

            var cur = e.GetPosition(RearPipCanvas);
            double newW = Math.Max(120, startSize.Width + (cur.X - start.X));
            double newH = newW * 9.0 / 16.0; // 16:9に近い比率を維持して映像が歪まないようにする

            RearPipBorder.Width = newW;
            RearPipBorder.Height = newH;
        }

        private void RearPipResizeGrip_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _pipResizeStart = null;
            _pipResizeStartSize = null;
            RearPipResizeGrip.ReleaseMouseCapture();

            // グリップでの自由リサイズは、離した時点で最寄りのリア倍率プリセットへ揃える
            // （倍率と永続化の値を常にプリセット1つに保つため）
            ComboBoxItem? best = null;
            double bestDiff = double.MaxValue;
            foreach (var item in RearZoomCombo.Items.OfType<ComboBoxItem>())
            {
                if (!double.TryParse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture, out var s)) continue;
                double diff = Math.Abs(CalcRearPipSize(s).Width - RearPipBorder.Width);
                if (diff < bestDiff) { bestDiff = diff; best = item; }
            }
            if (best == null) return;
            if (ReferenceEquals(RearZoomCombo.SelectedItem, best))
                ApplyRearPipSize(); // 同じ倍率のままでもサイズを揃え直す
            else
                RearZoomCombo.SelectedItem = best; // SelectionChanged経由でApplyRearPipSize
        }

        private void RearLinkedCheck_Changed(object sender, RoutedEventArgs e)
        {
            // RearLinkedCheckはXAMLでIsChecked="True"指定のため、InitializeComponent実行中
            // （まだRearList等が未接続の段階）にもこのイベントが発火する。ガード必須。
            if (RearList == null) return;

            // リアリストは追従ON/OFFに関わらず常に選択可能にする（手動選択のしやすさのため）。

            if (!RearLinked) return;

            if (UseTimeAlignedRear)
            {
                _rearExhaustedPath = null;
                _rearFailedPath = null;
                ReconcileRear();
            }
            else if (_currentFrontGroup?.HasRear == true)
            {
                PlayRearGroup(_currentFrontGroup);
            }
        }

        private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerFront == null) return;

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
        }

        /// <summary>
        /// 動画のネイティブ解像度×各倍率が、いま使っている画面の作業領域に収まるかどうかで
        /// ズーム選択肢のIsEnabledを更新する（「4xは2K/4K環境では選択不能に」というご要望に対応。
        /// 画面の解像度そのものより「動画側の解像度が大きい場合に大倍率が意味をなさなくなる」方が
        /// 本質なため、固定の解像度名で判定せず、実際にウィンドウが収まるかどうかで動的に判定する）。
        /// 選択中の項目が入らなくなった場合は、収まる範囲で一番大きい倍率へ自動的に切り替える。
        /// 右サイドバー幅は地図の向きで動的に変わるため、固定値ではなく実際のActualWidthを使う。
        /// </summary>
        private void UpdateZoomAvailability()
        {
            if (PlayerFront.NaturalVideoWidth <= 0 || PlayerFront.NaturalVideoHeight <= 0) return;

            double screenW = SystemParameters.WorkArea.Width;
            double screenH = SystemParameters.WorkArea.Height;
            const double leftSidebarWidth = 16;
            double rightSidebarWidth = RightSidebarColumnDef.ActualWidth > 0 ? RightSidebarColumnDef.ActualWidth : 260;
            const double titleBarHeight = 48;
            const double controlBarHeight = 56;

            foreach (var item in ZoomCombo.Items.OfType<ComboBoxItem>())
            {
                if (!double.TryParse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture, out var scale))
                    continue;

                double neededW = leftSidebarWidth + rightSidebarWidth + PlayerFront.NaturalVideoWidth * scale;
                double neededH = titleBarHeight + controlBarHeight + PlayerFront.NaturalVideoHeight * scale;
                item.IsEnabled = neededW <= screenW && neededH <= screenH;
            }

            if (ZoomCombo.SelectedItem is ComboBoxItem selected && !selected.IsEnabled)
            {
                var fallback = ZoomCombo.Items.OfType<ComboBoxItem>()
                    .Where(i => i.IsEnabled)
                    .OrderByDescending(i => double.Parse((string)i.Tag, System.Globalization.CultureInfo.InvariantCulture))
                    .FirstOrDefault();
                if (fallback != null) ZoomCombo.SelectedItem = fallback;
            }
        }

        private async void DnnEnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool on = DnnEnabledCheck.IsChecked == true;

            if (!on)
            {
                PlayerFront.DnnSuperResolutionEnabled = false;
                PlayerRear.DnnSuperResolutionEnabled = false;
                return;
            }

            await EnableDnnAsync(PlayerFront);
            if (HasActiveRear())
                await EnableDnnAsync(PlayerRear);
        }

        private async Task EnableDnnAsync(FfmpegMediaElement player)
        {
            if (player.NaturalVideoWidth <= 0 || player.NaturalVideoHeight <= 0)
            {
                player.DnnSuperResolutionEnabled = true;
                return;
            }

            if (player.IsDnnReadyForCurrentResolution)
            {
                player.DnnSuperResolutionEnabled = true;
                return;
            }

            bool wasPlaying = _isPlaying;
            if (wasPlaying) player.Pause();

            bool ok = await player.PrebuildDnnSuperResolutionAsync();
            player.DnnSuperResolutionEnabled = ok;

            if (wasPlaying) player.Play();
        }

        // ---- シーク（AccelChart最上段の独立した帯から通知される。旧SeekSliderと同じ
        //      ドラッグ間引きプレビュー・クリック即シーク・ホバー時刻プレビューを維持） ----

        private void AccelChart_SeekDragStarted()
        {
            _isDragging = true;
            _wasPlayingBeforeSeekDrag = _isPlaying;
            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                _isPlaying = false;
                SetPlayPauseIcon(false);
            }
        }

        private async void AccelChart_SeekPreview(TimeSpan pos)
        {
            if (_dragCompleting || !PlayerFront.NaturalDuration.HasTimeSpan) return;
            await RequestLiveSeekAsync(pos.TotalSeconds);
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
                    await PlayerFront.FastSeekPreviewAsync(TimeSpan.FromSeconds(target));
                }
            }
            finally
            {
                _seekLiveBusy = false;
            }
        }

        private async void AccelChart_SeekDragCompleted(TimeSpan target)
        {
            _dragCompleting = true;
            while (_seekLiveBusy)
                await Task.Delay(15);

            // グラフはファイル全体を静的に表示する方式のため、シークしても消さない
            // （消すとまた全体を再計算することになり本末転倒）。プレイヘッド／シークバーの
            // 位置だけ動かす。
            AccelChart.SetPlayhead(target);
            await PlayerFront.StepToVideoOnlyAsync(target, timeoutMs: 2000);
            await SeekRearToFrontPositionAsync(target);

            _isDragging = false;
            _dragCompleting = false;

            if (_wasPlayingBeforeSeekDrag)
            {
                PlayerFront.Play();
                if (HasActiveRear()) PlayerRear.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
            }
        }

        // ホバー中の位置に対応する時刻をポップアップでプレビュー表示する（MainWindow本体と同等）
        private void AccelChart_HoverTimeChanged(TimeSpan? time, double x)
        {
            if (time == null || !PlayerFront.NaturalDuration.HasTimeSpan)
            {
                SeekPreviewPopup.IsOpen = false;
                return;
            }

            SeekPreviewText.Text = time.Value.ToString(@"hh\:mm\:ss");
            SeekPreviewPopup.IsOpen = true;
            var popupChild = (FrameworkElement)SeekPreviewPopup.Child;
            popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double halfW = popupChild.DesiredSize.Width / 2;
            double width = AccelChart.ActualWidth;
            SeekPreviewPopup.HorizontalOffset = Math.Clamp(x - halfW, 0, Math.Max(0, width - halfW * 2));
        }

        // ---- フルスクリーン（MainWindowから切替。プレイヤーは同一インスタンスのまま表示だけ切り替える）----
        //  ・ツールバー/左サイドバー(リスト)を隠し、映像を全面に広げる。
        //  ・右サイドバー(日時・緯度経度＋走行軌跡マップ)は、車速OSDの下・チャートの上の右端へ
        //    半透明のオーバーレイとして重ねる（Mキー[MainWindow側]で表示/非表示を切替）。
        //  ・加速度チャート(＋速度/加速度HUD)は映像下部へ半透明で重ねる（常時表示）。
        //  ・再生コントロールバーはマウスを動かすと現れ、再生中に無操作が続くと隠れる（一時停止中は出しっぱなし）。

        private bool _isFullScreen;
        private DispatcherTimer? _fsControlsTimer;
        private Point _fsLastMousePos;
        private long _fsLastActivityTick;
        private double _fsControlsHideDelaySec = 2.5;
        private Brush? _fsSavedControlBarBackground;
        private bool _fsMapOverlayVisible = true;

        public bool IsFullScreen => _isFullScreen;

        /// <summary>再生/一時停止のトグル（フルスクリーン中のSpaceキー等、外部から呼ぶ用）。</summary>
        public void TogglePlayPause()
        {
            if (PlayerFront.Source == null) return;
            PlayPauseButton_Click(this, new RoutedEventArgs());
        }

        public void SetFullScreen(bool on, double controlsHideDelaySec = 2.5)
        {
            if (_isFullScreen == on) return;
            _isFullScreen = on;

            if (on)
            {
                _fsControlsHideDelaySec = controlsHideDelaySec > 0 ? controlsHideDelaySec : 2.5;

                ToolbarBar.Visibility = Visibility.Collapsed;
                LeftSidebarHost.Visibility = Visibility.Collapsed;
                LeftSidebarColumn.Width = new GridLength(0);
                // 右サイドバー列は0幅にし、サイドバー本体は映像列(1)の右上へオーバーレイとして移す
                RightSidebarColumnDef.Width = new GridLength(0);
                Grid.SetColumn(RightSidebarHost, 1);
                Panel.SetZIndex(RightSidebarHost, 60);
                RightSidebarHost.HorizontalAlignment = HorizontalAlignment.Right;
                RightSidebarHost.VerticalAlignment = VerticalAlignment.Top;
                RightSidebarHost.Opacity = 0.8;

                // 映像を全行にまたがらせ、下段(コントロールバー/チャート)を映像の上へ重ねる
                Grid.SetRow(MainRowGrid, 0);
                Grid.SetRowSpan(MainRowGrid, 4);

                _fsSavedControlBarBackground = ControlBar.Background;
                ControlBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0x0F, 0x16, 0x23));
                ChartStrip.Opacity = 0.75;

                _fsLastMousePos = System.Windows.Input.Mouse.GetPosition(this);
                _fsLastActivityTick = Environment.TickCount64;
                ControlBar.Visibility = Visibility.Visible;

                UpdateFullScreenMapOverlay();

                PreviewMouseMove += FullScreen_PreviewMouseMove;
                _fsControlsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _fsControlsTimer.Tick += FullScreenControlsTimer_Tick;
                _fsControlsTimer.Start();
            }
            else
            {
                PreviewMouseMove -= FullScreen_PreviewMouseMove;
                _fsControlsTimer?.Stop();
                _fsControlsTimer = null;

                ToolbarBar.Visibility = Visibility.Visible;
                LeftSidebarHost.Visibility = Visibility.Visible;
                LeftSidebarColumn.Width = new GridLength(16);
                LeftSidebarHost_MouseLeave(this, null!); // 折りたたみ状態(幅16px)へ戻す
                // オーバーレイ化していた右サイドバーを通常の右列へ戻す
                Grid.SetColumn(RightSidebarHost, 2);
                Panel.SetZIndex(RightSidebarHost, 0);
                RightSidebarHost.ClearValue(FrameworkElement.WidthProperty);
                RightSidebarHost.ClearValue(FrameworkElement.HeightProperty);
                RightSidebarHost.ClearValue(FrameworkElement.MarginProperty);
                RightSidebarHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                RightSidebarHost.VerticalAlignment = VerticalAlignment.Stretch;
                RightSidebarHost.Opacity = 1.0;
                RightSidebarHost.Visibility = Visibility.Visible;
                RightSidebarColumnDef.Width = new GridLength(_mapHorizontal ? 420 : 260);

                Grid.SetRow(MainRowGrid, 1);
                Grid.SetRowSpan(MainRowGrid, 1);

                ControlBar.Visibility = Visibility.Visible;
                if (_fsSavedControlBarBackground != null)
                    ControlBar.Background = _fsSavedControlBarBackground;
                ChartStrip.Opacity = 1.0;
            }
        }

        /// <summary>全画面中の地図・日時オーバーレイの表示/非表示を切り替える。</summary>
        public void ToggleFullScreenMapOverlay()
        {
            _fsMapOverlayVisible = !_fsMapOverlayVisible;
            UpdateFullScreenMapOverlay();
        }

        /// <summary>全画面中の右サイドバー(日時・緯度経度＋地図)オーバーレイの位置とサイズを更新する。
        /// 上端は車速OSDの下、下端は再生コントロールバーとチャートの上に収める。</summary>
        private void UpdateFullScreenMapOverlay()
        {
            if (!_isFullScreen) return;
            if (!_fsMapOverlayVisible)
            {
                RightSidebarHost.Visibility = Visibility.Collapsed;
                return;
            }

            double width = _mapHorizontal ? 420 : 260;
            double top = SpeedOsdEnabled && SpeedOsdText != null
                ? 10 + SpeedOsdText.FontSize * 1.4 + 8 // OSD(数値＋余白)の下
                : 12;
            double bottomReserve = ChartStrip.ActualHeight + 56 + 16; // チャート＋コントロールバー＋余白
            double available = MainRowGrid.ActualHeight - top - bottomReserve;
            double height = Math.Clamp(width * 1.25, 200, Math.Max(200, available));

            RightSidebarHost.Width = width;
            RightSidebarHost.Height = height;
            RightSidebarHost.Margin = new Thickness(0, top, 12, 0);
            RightSidebarHost.Visibility = Visibility.Visible;
        }

        private void FullScreen_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // カーソルを隠した/レイアウト変化で発生する「位置が変わらない合成MouseMove」は活動とみなさない
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _fsLastMousePos.X) < 2 && Math.Abs(p.Y - _fsLastMousePos.Y) < 2) return;
            _fsLastMousePos = p;
            _fsLastActivityTick = Environment.TickCount64;
            ControlBar.Visibility = Visibility.Visible;
        }

        private void FullScreenControlsTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isFullScreen) return;

            // 一時停止中・操作中(マウスがバー上/ドラッグ中)は出しっぱなし
            if (!_isPlaying || ControlBar.IsMouseOver || System.Windows.Input.Mouse.Captured != null)
            {
                ControlBar.Visibility = Visibility.Visible;
                return;
            }

            if (Environment.TickCount64 - _fsLastActivityTick >= (long)(_fsControlsHideDelaySec * 1000))
                ControlBar.Visibility = Visibility.Collapsed;
        }

        // ---- 測位ロスト区間の速度推定: 前後ファイルからの文脈取得 ----
        private const double NominalClipSeconds = 120; // 1ファイルの公称長(2分)

        private static bool AreConsecutiveClips(DashcamMediaGroup earlier, DashcamMediaGroup later)
        {
            var a = ParseFileStamp(earlier.FrontVideoPath) ?? earlier.Timestamp;
            var b = ParseFileStamp(later.FrontVideoPath) ?? later.Timestamp;
            if (a == null || b == null) return false;
            // 録画は2分ごとに連続する。大きく空いていれば別の走行（トンネルの連続とみなさない）
            return Math.Abs((b.Value - a.Value).TotalSeconds - NominalClipSeconds) <= 15;
        }

        private static List<DashcamSensorFrame> ParseNeighborFrames(DashcamMediaGroup g)
        {
            string? path = g.FrontNmeaPath ?? g.RearNmeaPath;
            return path == null
                ? new List<DashcamSensorFrame>()
                : NmeaSensorParser.Parse(path, g.Timestamp, TimeSpan.FromSeconds(NominalClipSeconds));
        }

        /// <summary>現在ファイルの先頭/末尾の測位ロスト区間が前後のファイルへ続いている場合に、その区間の直前/直後の
        /// 測位速度と、ファイル外で経過している秒数を求める（上限はNmeaSensorParser.MaxEstimateGapSeconds）。
        /// バックグラウンドスレッドから呼ぶ想定（UI要素には触れない）。</summary>
        private NmeaSensorParser.SpeedGapContext? BuildSpeedGapContext(DashcamMediaGroup group, bool needBefore, bool needAfter)
        {
            var groups = _frontGroups;
            int idx = groups.IndexOf(group);
            if (idx < 0)
            {
                string key = group.TimestampKey;
                idx = groups.FindIndex(g => g.TimestampKey == key);
            }
            if (idx < 0) return null;

            double maxGap = NmeaSensorParser.MaxEstimateGapSeconds;
            double? speedBefore = null, speedAfter = null;
            double gapBefore = 0, gapAfter = 0;

            if (needBefore)
            {
                double acc = 0;
                for (int j = idx - 1; j >= 0 && acc <= maxGap; j--)
                {
                    if (!AreConsecutiveClips(groups[j], groups[j + 1])) break;
                    var frames = ParseNeighborFrames(groups[j]);
                    int lastFix = frames.FindLastIndex(f => f.HasGpsFix);
                    if (lastFix >= 0)
                    {
                        speedBefore = frames[lastFix].SpeedKmh;
                        gapBefore = acc + (NominalClipSeconds - frames[lastFix].VideoOffset.TotalSeconds);
                        break;
                    }
                    acc += NominalClipSeconds; // このファイルは全体が測位ロスト
                }
            }

            if (needAfter)
            {
                double acc = 0;
                for (int j = idx + 1; j < groups.Count && acc <= maxGap; j++)
                {
                    if (!AreConsecutiveClips(groups[j - 1], groups[j])) break;
                    var frames = ParseNeighborFrames(groups[j]);
                    int firstFix = frames.FindIndex(f => f.HasGpsFix);
                    if (firstFix >= 0)
                    {
                        speedAfter = frames[firstFix].SpeedKmh;
                        gapAfter = acc + frames[firstFix].VideoOffset.TotalSeconds;
                        break;
                    }
                    acc += NominalClipSeconds;
                }
            }

            if (speedBefore == null && speedAfter == null) return null;
            return new NmeaSensorParser.SpeedGapContext(speedBefore, gapBefore, speedAfter, gapAfter);
        }

        // ---- 車速OSD ----

        private DashcamSensorFrame? _lastOsdFrame;
        private static readonly SolidColorBrush SpeedOsdMeasuredBrush = CreateFrozenBrush(0x00, 0xFF, 0x00);   // 実測: 蛍光緑
        private static readonly SolidColorBrush SpeedOsdEstimatedBrush = CreateFrozenBrush(0xFB, 0xBF, 0x24);  // 推定: 琥珀色

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        private void SpeedOsdCheck_Changed(object sender, RoutedEventArgs e) => UpdateSpeedOsd(_lastOsdFrame);

        private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSpeedOsdFontSize();
            UpdateFullScreenMapOverlay(); // 全画面中のみ動作（OSDの文字サイズ確定後に配置を再計算）
        }

        /// <summary>文字サイズを映像エリアの高さの約9%に追従させる（数値本体。単位は40%）。</summary>
        private void UpdateSpeedOsdFontSize()
        {
            if (SpeedOsdText == null || VideoArea == null) return;
            double h = VideoArea.ActualHeight;
            if (h <= 0) return;
            double size = Math.Max(24, h * 0.09);
            SpeedOsdText.FontSize = size;
            SpeedOsdUnit.FontSize = size * 0.4;
        }

        /// <summary>現在フレームの走行速度をOSDへ反映する。未測位(トンネル内等)は「--」、
        /// センサーデータが無い/OSDがOFFの間は非表示。</summary>
        private void UpdateSpeedOsd(DashcamSensorFrame? frame)
        {
            _lastOsdFrame = frame;
            if (SpeedOsd == null) return; // InitializeComponent中のCheckedイベント対策

            if (!SpeedOsdEnabled || frame == null)
            {
                SpeedOsd.Visibility = Visibility.Collapsed;
                return;
            }

            // 測位ロスト中は前後の測位速度から補間した推定値を「≈」付き・琥珀色で表示（実測の緑と区別）
            bool estimated = !frame.HasGpsFix && frame.SpeedEstimated;
            string text = frame.HasGpsFix ? Math.Round(frame.SpeedKmh).ToString("0")
                : estimated ? "≈" + Math.Round(frame.SpeedKmh).ToString("0")
                : "--";
            if (SpeedOsdValue.Text != text)
                SpeedOsdValue.Text = text;
            var brush = estimated ? SpeedOsdEstimatedBrush : SpeedOsdMeasuredBrush;
            if (!ReferenceEquals(SpeedOsdText.Foreground, brush))
                SpeedOsdText.Foreground = brush;
            if (SpeedOsd.Visibility != Visibility.Visible)
            {
                UpdateSpeedOsdFontSize();
                SpeedOsd.Visibility = Visibility.Visible;
            }
        }

        // ---- HUD更新（Frontの実フレーム表示に同期。FrameDisplayedはAction<double>で秒単位pts） ----

        private void OnFrontFrameDisplayed(double ptsSeconds)
        {
            _fpsFrameCount++;

            var pos = TimeSpan.FromSeconds(ptsSeconds);
            var frame = DashcamSensorLookup.FindNearest(_sensorFrames, pos);
            Hud.UpdateFrame(frame); // Hud側は速度・加速度3軸のみ表示する想定（日時/緯度経度は下記の地図上パネルへ分離）
            UpdateSpeedOsd(frame);
            if (frame != null)
            {
                GeoDateTimeText.Text = frame.Timestamp.ToString("yyyy/MM/dd HH:mm:ss");
                GeoLatText.Text = frame.HasGpsFix ? frame.Latitude.ToString("F6") : "---";
                GeoLngText.Text = frame.HasGpsFix ? frame.Longitude.ToString("F6") : "---";
                MapView.SetCarPosition(frame.Latitude, frame.Longitude, frame.HasGpsFix);
            }
            // チャート本体はファイルを開いた時点で一度だけ計算済み（SetFullTrack）。
            // 毎フレームはプレイヘッド／シークバーの位置を動かすだけの軽量な処理にとどめる。
            // ドラッグ中はAccelChart側で既にマウス位置に応じた更新を行っているため、
            // ここからの上書きは行わない（せめぎ合い防止）。
            if (!_isDragging)
                AccelChart.SetPlayhead(pos);

            var duration = PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.NaturalDuration.TimeSpan : TimeSpan.Zero;
            PositionText.Text = $"{pos:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        }

        // ---- Rearのドリフト追従・実測fps集計（1秒ごと） ----

        private void SyncTimer_Tick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _fpsWindowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                double fps = _fpsFrameCount / elapsed;
                FpsText.Text = $"{fps:F0}fps";
                _fpsFrameCount = 0;
                _fpsWindowStart = now;
            }

            if (RearLinked && !_isDragging)
                ReconcileRear(); // Frontの現在時刻に対してリアclipを切り替える/外す

            bool timeAligned = UseTimeAlignedRear && _currentRearClipPath != null;
            bool rearReady = timeAligned ? _rearAvailable : HasActiveRear();
            if (!RearLinked || !rearReady || _isDragging)
            {
                _rearSettleSamplePending = false;
                return;
            }

            // 時刻ベース: リアの目標位置 = (Front開始時刻+Front位置) − リアclip開始時刻。
            // フォールバック時は従来どおり「同名ペア＝同時開始」としてFront位置に合わせる。
            TimeSpan desiredRearPos = timeAligned
                ? (_frontStart!.Value + PlayerFront.Position) - _currentRearClipStart
                : PlayerFront.Position;
            var diff = desiredRearPos - PlayerRear.Position; // 正=リアが遅れている

            // 再同期の約1.5秒後に残っている遅れを、シーク所要時間ぶんの遅れとして学習し
            // 次回のシーク先へ先乗せする（一時停止中や不安定な状態は学習しない）。
            if (_rearSettleSamplePending && _rearResyncWatch.ElapsedMilliseconds >= 1500)
            {
                _rearSettleSamplePending = false;
                if (_isPlaying)
                {
                    var lead = _rearSeekLead + diff;
                    _rearSeekLead = lead < TimeSpan.Zero ? TimeSpan.Zero : (lead > MaxRearLead ? MaxRearLead : lead);
                }
            }

            bool cooledDown = !_rearResyncWatch.IsRunning || _rearResyncWatch.Elapsed >= ResyncCooldown;
            if (RearLinked && cooledDown && diff.Duration() > ResyncThreshold)
            {
                var target = desiredRearPos + _rearSeekLead;
                if (PlayerRear.NaturalDuration.HasTimeSpan && target > PlayerRear.NaturalDuration.TimeSpan)
                    target = PlayerRear.NaturalDuration.TimeSpan;
                DashcamPlayErrorLogger.Log($"[RearResync] diff={diff.TotalSeconds:F2}s → Rear位置={target.TotalSeconds:F2}s");
                PlayerRear.Position = target;
                _rearResyncWatch.Restart();
                _rearSettleSamplePending = true;
            }
        }

        // ---- 走行軌跡マップ: 前後数ファイル分をつないだ連続した経路として表示する ----
        // NMEAは1ファイル数十KB程度と極めて軽量なため、動画本体(1ファイル150MB級)とは違い
        // 大量にまとめて読み直しても実質コストが無い。前後の範囲は「10ファイルや20ファイルは
        // 楽勝か」というご質問への回答を兼ねて、前後10ファイルずつ（現在込みで最大21ファイル分）
        // まで広げている。必要であればもっと増やしても問題ない。
        private const int MapBufferPrevFiles = 10;
        private const int MapBufferNextFiles = 10;

        private async Task RefreshMapBufferAsync(DashcamMediaGroup currentGroup)
        {
            int idx = _frontGroups.IndexOf(currentGroup);
            if (idx < 0)
            {
                MapView.SetRoute(new List<MapSegment>());
                return;
            }

            int prevCount = Math.Min(MapBufferPrevFiles, idx);
            int nextCount = Math.Min(MapBufferNextFiles, _frontGroups.Count - 1 - idx);
            int startIdx = idx - prevCount;
            int endIdx = idx + nextCount;

            var targetGroup = currentGroup;
            var merged = new List<DashcamSensorFrame>();
            TimeSpan cursor = TimeSpan.Zero;

            for (int i = startIdx; i <= endIdx; i++)
            {
                var g = _frontGroups[i];
                string? nmea = g.FrontNmeaPath ?? g.RearNmeaPath;
                if (nmea == null) continue;

                TimeSpan? durationHint = (i == idx && PlayerFront.NaturalDuration.HasTimeSpan)
                    ? PlayerFront.NaturalDuration.TimeSpan
                    : null;

                var frames = await Task.Run(() => NmeaSensorParser.Parse(nmea, g.Timestamp, durationHint));
                foreach (var f in frames)
                    merged.Add(f with { VideoOffset = cursor + f.VideoOffset });

                if (frames.Count > 0)
                    cursor += frames[^1].VideoOffset + TimeSpan.FromSeconds(1);
            }

            if (!ReferenceEquals(_currentFrontGroup, targetGroup))
                return;

            // 進行方位から縦長/横長を自動判定する（南北方向の移動量 dLat と、経度差を緯度で
            // 補正した東西方向相当の移動量 dLng を比較。dLngの方が大きければ横長を推奨）。
            // 有効なGPS測位点が2点未満の場合は前回の判定を維持する（無理に判定しない）。
            var validPoints = merged.Where(f => f.HasGpsFix).ToList();
            if (validPoints.Count >= 2)
            {
                double minLat = validPoints.Min(p => p.Latitude), maxLat = validPoints.Max(p => p.Latitude);
                double minLng = validPoints.Min(p => p.Longitude), maxLng = validPoints.Max(p => p.Longitude);
                double dLat = maxLat - minLat;
                double lat0 = (minLat + maxLat) / 2.0;
                double dLng = (maxLng - minLng) * Math.Cos(lat0 * Math.PI / 180.0);
                _lastAutoHorizontal = dLng > dLat;
                UpdateEffectiveMapOrientation();
            }

            MapView.SetRoute(DashcamMapPointBuilder.BuildSegments(merged));
        }

        // ---- 地図の縦長/横長切替（自動判定＋手動上書き。Hudの位置には一切影響しない） ----

        /// <summary>
        /// 地図の縦長/横長を、ユーザー選択(MapView.OrientationMode)とルート方位の自動判定結果
        /// (_lastAutoHorizontal)を総合して適用する。手動でVertical/Horizontalを選んでいる間は
        /// 自動判定結果を無視し固定表示にする。
        /// </summary>
        private void UpdateEffectiveMapOrientation()
        {
            bool horizontal = MapView.OrientationMode switch
            {
                DashcamMapOrientation.Horizontal => true,
                DashcamMapOrientation.Vertical => false,
                _ => _lastAutoHorizontal
            };
            ApplyMapOrientation(horizontal);
        }

        /// <summary>
        /// 実際のレイアウト切替本体。センサー情報(Hud)は常にAccelChartの右隣に固定表示のため
        /// ここでは一切動かさない。地図が占める右サイドバーの列幅だけを調整する
        /// （縦長ルート想定=狭め／横長ルート想定=広め）。
        /// </summary>
        private void ApplyMapOrientation(bool horizontal)
        {
            if (_mapHorizontal == horizontal) return;
            _mapHorizontal = horizontal;
            if (_isFullScreen) { UpdateFullScreenMapOverlay(); return; } // 全画面中は列幅を触らずオーバーレイの幅だけ更新。復帰時に_mapHorizontalから戻す
            RightSidebarColumnDef.Width = new GridLength(horizontal ? 420 : 260);
        }

        // ---- 次ファイルの先読み（OSファイルキャッシュ温め） ----
        private void SchedulePrefetchIfNeeded()
        {
            if (_currentFrontGroup == null) return;
            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0) return;

            var toPrefetch = new List<string>();
            for (int off = 1; off <= 2 && idx + off < _frontGroups.Count; off++)
            {
                var g = _frontGroups[idx + off];
                if (g.FrontVideoPath != null) toPrefetch.Add(g.FrontVideoPath);
                if (RearLinked && g.RearVideoPath != null) toPrefetch.Add(g.RearVideoPath);
            }

            foreach (var path in toPrefetch)
            {
                if (_prefetchedPaths.Contains(path)) continue;
                _prefetchedPaths.Add(path);
                string p = path;
                _ = Task.Run(() => WarmFileCache(p));
            }

            if (_prefetchedPaths.Count > 12) _prefetchedPaths.Clear();
        }

        private static void WarmFileCache(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
                var buffer = new byte[1 << 20];
                while (fs.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch { }
        }

        // ---- レジューム再生 ----
        public async Task<bool> TryResumeAsync(string? drivePath, string? groupKey, double positionSeconds, string? eventFolderName = null)
        {
            if (string.IsNullOrEmpty(drivePath) || string.IsNullOrEmpty(groupKey))
                return false;

            _isResuming = true;
            try
            {
                if (!string.IsNullOrEmpty(eventFolderName) && Enum.TryParse<DashcamEventFolder>(eventFolderName, out var folder))
                {
                    foreach (var obj in EventFolderCombo.Items)
                    {
                        if (obj is ComboBoxItem ci && (string)ci.Tag == folder.ToString())
                        {
                            EventFolderCombo.SelectedItem = ci;
                            break;
                        }
                    }
                }

                RefreshDriveList();
                var options = DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>;
                var drive = options?.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, drivePath, StringComparison.OrdinalIgnoreCase));

                if (drive == null && Directory.Exists(drivePath))
                {
                    // ドライブレターとしては見つからない（例: フォルダコピー運用）が、パス自体は存在する場合
                    drive = new DriveOrFolderOption { DisplayName = drivePath, RootPath = drivePath };
                    var list = new List<DriveOrFolderOption>(options ?? Enumerable.Empty<DriveOrFolderOption>());
                    list.Insert(Math.Max(0, list.Count - 1), drive);
                    DriveCombo.ItemsSource = list;
                }

                if (drive == null)
                    return false;

                DriveCombo.SelectedItem = drive;
                ScanDrive(drive.RootPath!, CurrentEventFolder);

                var group = _frontGroups.FirstOrDefault(g => g.TimestampKey == groupKey);
                if (group == null)
                    return false;

                _pendingResumeSeconds = positionSeconds;
                SetListSelection(FrontList, group, scrollToTop: true);
                PlayFrontGroup(group);

                await Task.CompletedTask;
                return true;
            }
            finally
            {
                _isResuming = false;
            }
        }
        /// <summary>
        /// 起動時にコマンドライン引数でフォルダが渡された場合の直接読み込み。
        /// レジューム(ドライブ/ファイル選択/再生位置の復元)は一切行わず、指定フォルダを
        /// ドライブ一覧に追加してスキャンするだけに留める（ファイル選択・再生開始はユーザーに委ねる）。
        /// </summary>
        public void LoadFolderDirect(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return;

            RefreshDriveList();
            var options = DriveCombo.ItemsSource as IEnumerable<DriveOrFolderOption>;
            var existing = options?.FirstOrDefault(o => !o.IsBrowseOption && string.Equals(o.RootPath, folderPath, StringComparison.OrdinalIgnoreCase));

            DriveOrFolderOption target;
            if (existing != null)
            {
                target = existing;
            }
            else
            {
                target = new DriveOrFolderOption { DisplayName = folderPath, RootPath = folderPath };
                var list = new List<DriveOrFolderOption>(options ?? Enumerable.Empty<DriveOrFolderOption>());
                list.Insert(Math.Max(0, list.Count - 1), target);
                DriveCombo.ItemsSource = list;
            }

            // DriveCombo_SelectionChanged経由でRescanCurrentSelection→ScanDriveが走り、
            // フロント/リアリストが表示される（ファイルの自動選択・再生は行わない）。
            DriveCombo.SelectedItem = target;
        }

    }
}
