using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using VerticalPlayer.Media;

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
        private static readonly TimeSpan ResyncThreshold = TimeSpan.FromMilliseconds(150);

        private bool _suppressSelectionEvent;
        private bool _isPlaying;

        // Front/RearのMediaOpenedは非同期でタイミングがずれるため、「再生する意図」を
        // 独立フラグとして保持し、どちらのMediaOpenedが先に来ても正しく再生開始できるようにする。
        private bool _wantsPlaying;

        // 壊れた/読めないファイルが連続した場合に無限ループでスキップし続けないための保険
        private int _consecutiveFrontFailures;

        // 起動時レジューム用: MediaOpened後にシークすべき秒数（該当なければnull）
        private double? _pendingResumeSeconds;

        // 次ファイルの先読み（OSファイルキャッシュ温め）: 同じパスを何度も読み直さないための記録
        private readonly HashSet<string> _prefetchedPaths = new();

        // ---- シーク（MainWindowのSeekBarと同等の機能: ドラッグ間引きプレビュー、
        //      クリックで即シーク、ホバーで時刻プレビュー表示） ----
        private bool _isDragging;
        private bool _dragCompleting;
        private bool _wasPlayingBeforeSeekDrag;
        private bool _seekLiveBusy;
        private double? _seekLivePendingSeconds;
        private bool _suppressSliderEvent;

        /// <summary>ズーム変更・動画オープン時に、映像本来のサイズ×倍率での
        /// ウィンドウフィットをホスト(MainWindow)へ依頼する。引数は動画のネイティブ幅・高さ×倍率(px)。</summary>
        public event Action<double, double>? RequestWindowFit;

        public bool RearLinked
        {
            get => RearLinkedCheck.IsChecked == true;
            set => RearLinkedCheck.IsChecked = value;
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

            // ドラレコは1ファイルあたり約2分間隔で次々切り替わり、かつSDカード等の低速
            // ストレージ運用が前提のため、既存の「パケット先読み（低速ストレージ対策）」
            // パイプラインをこの画面のFront/Rear両方で既定ONにする。
            PlayerFront.PacketPrefetch = true;
            PlayerRear.PacketPrefetch = true;

            // ハードウェアデコード(D3D11VA、非対応/失敗時は自動でSWへフォールバック)・
            // ノイズリダクション・ダイナミックコントラストも、MainWindow本体の既定(true)に
            // 合わせてドラレコ側でも既定ONにする。
            PlayerFront.HardwareAcceleration = true;
            PlayerRear.HardwareAcceleration = true;
            PlayerFront.Denoise = true;
            PlayerRear.Denoise = true;
            PlayerFront.DynamicContrast = true;
            PlayerRear.DynamicContrast = true;

            // 実際に使われたデコードモード（"HW (D3D11VA)" / "SW"）をツールバーに表示して確認できるようにする
            PlayerFront.DecodeModeChanged += mode => Dispatcher.Invoke(() => DecodeModeText.Text = $"デコード: {mode}");

            PlayerFront.FrameDisplayed += OnFrontFrameDisplayed;

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();
        }

        private void DashcamPlayerView_Loaded(object sender, RoutedEventArgs e) => RefreshDriveList();

        // ---- 左サイドバー: マウスオーバーで展開 ----

        private void LeftSidebarHost_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            LeftSidebarColumn.Width = new GridLength(220);
            CollapsedHint.Visibility = Visibility.Collapsed;
            SidebarContent.Visibility = Visibility.Visible;
        }

        private void LeftSidebarHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            LeftSidebarColumn.Width = new GridLength(16);
            SidebarContent.Visibility = Visibility.Collapsed;
            CollapsedHint.Visibility = Visibility.Visible;
        }

        // ---- 録画フォルダ種別・ドライブ選択（選択が変わったら自動で読み込み直す） ----

        private void EventFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveCombo == null) return; // InitializeComponent中の初期選択イベントは無視
            RescanCurrentSelection();
        }

        private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => RefreshDriveList();

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

        private void RescanCurrentSelection()
        {
            if (DriveCombo.SelectedItem is not DriveOrFolderOption option || option.IsBrowseOption || option.RootPath == null)
                return;

            ScanDrive(option.RootPath, CurrentEventFolder);

            if (_frontGroups.Count == 0 && _rearGroups.Count == 0)
            {
                MessageBox.Show($"対応する動画が見つかりませんでした（{CurrentEventFolder}フォルダ等を確認してください）。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ScanDrive(string rootPath, DashcamEventFolder folder)
        {
            _groups = DashcamFileScanner.Scan(rootPath, folder);
            _frontGroups = _groups.Where(g => g.HasFront).ToList();
            _rearGroups = _groups.Where(g => g.HasRear).ToList();

            FrontList.ItemsSource = _frontGroups;
            RearList.ItemsSource = _rearGroups;
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
            _sensorFrames = new List<DashcamSensorFrame>(); // Frontの動画長が判明してからParseし直す（MediaOpened側）
            _wantsPlaying = true; // Front/RearどちらのMediaOpenedが先に来ても再生開始させる意図フラグ

            if (group.FrontVideoPath != null)
            {
                PlayerFront.Stop(); // 前回のOpen失敗等で内部状態が残っていても確実にリセットしてから開く
                PlayerFront.Source = new Uri(group.FrontVideoPath);
            }

            if (RearLinked)
            {
                if (group.HasRear)
                {
                    PlayRearGroup(group, keepListSelectionOnly: true);
                    int rearIdx = _rearGroups.IndexOf(group);
                    ScrollSelectedToTop(RearList, rearIdx);
                }
                else
                {
                    // フロントと同名のリアファイルが存在しないケース。Rearは単に非表示のまま何もしない。
                    PlayerRear.Stop();
                    _currentRearGroup = null;
                    RearPipBorder.Visibility = Visibility.Collapsed;
                    RearVisibleCheck.IsChecked = false;
                }
            }
        }

        private void PlayRearGroup(DashcamMediaGroup group, bool keepListSelectionOnly = false)
        {
            _currentRearGroup = group;
            _wantsPlaying = true; // 独立選択(リア追従OFF時)から呼ばれた場合もここで意図をセットする

            if (group.RearVideoPath != null)
            {
                PlayerRear.Stop(); // 前回Open失敗(エラー記録ファイル等)の内部状態を必ずリセットしてから開く
                PlayerRear.Source = new Uri(group.RearVideoPath);
            }

            if (!keepListSelectionOnly)
            {
                _suppressSelectionEvent = true;
                RearList.SelectedItem = group;
                _suppressSelectionEvent = false;
                RearList.ScrollIntoView(group);
            }
        }

        // ---- 再生制御 ----

        private async void PlayerFront_MediaOpened(object sender, RoutedEventArgs e)
        {
            _consecutiveFrontFailures = 0;
            PlayerFront.ResetDnnEngineForNewFile();

            var duration = PlayerFront.NaturalDuration.HasTimeSpan
                ? PlayerFront.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            SeekSlider.Maximum = duration.TotalSeconds;

            string? nmeaPath = _currentFrontGroup?.FrontNmeaPath ?? _currentFrontGroup?.RearNmeaPath;
            _sensorFrames = nmeaPath != null
                ? NmeaSensorParser.Parse(nmeaPath, _currentFrontGroup?.Timestamp, duration)
                : new List<DashcamSensorFrame>();

            if (_currentFrontGroup != null)
                _ = RefreshMapBufferAsync(_currentFrontGroup);

            if (_wantsPlaying)
            {
                PlayerFront.Play();
                _isPlaying = true;
                PlayPauseButton.Content = "⏸";
            }

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);

            if (_pendingResumeSeconds is double resumeSec)
            {
                _pendingResumeSeconds = null;
                if (resumeSec > 0.5 && resumeSec < duration.TotalSeconds)
                    await PlayerFront.StepToVideoOnlyAsync(TimeSpan.FromSeconds(resumeSec), timeoutMs: 3000);
            }

            SchedulePrefetchIfNeeded();
        }

        private void PlayerRear_MediaOpened(object sender, RoutedEventArgs e)
        {
            PlayerRear.ResetDnnEngineForNewFile();
            if (_wantsPlaying)
                PlayerRear.Play();
        }

        private void PlayerFront_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            _consecutiveFrontFailures++;
            if (_consecutiveFrontFailures >= 3)
            {
                _consecutiveFrontFailures = 0;
                MessageBox.Show("複数のFront動画が連続して再生できませんでした。ファイルの状態を確認してください。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AdvanceToNextFrontScene();
        }

        private void PlayerRear_MediaFailed(object sender, FfmpegMediaFailedEventArgs e)
        {
            PlayerRear.Stop();
            RearPipBorder.Visibility = Visibility.Collapsed;
            RearVisibleCheck.IsChecked = false;
            _currentRearGroup = null;
        }

        private void PlayerFront_MediaEnded(object sender, RoutedEventArgs e) => AdvanceToNextFrontScene();

        private void NextSceneButton_Click(object sender, RoutedEventArgs e) => AdvanceToNextFrontScene();

        private void AdvanceToNextFrontScene()
        {
            if (_currentFrontGroup is null) return;
            int idx = _frontGroups.IndexOf(_currentFrontGroup);
            if (idx < 0 || idx + 1 >= _frontGroups.Count) return;

            var next = _frontGroups[idx + 1];
            _suppressSelectionEvent = true;
            FrontList.SelectedItem = next;
            _suppressSelectionEvent = false;

            // 連続再生の見た目: 再生中のファイルが常にリストの一番上に来て、後続が下から
            // 順々にせり上がって見えるようにする（単なるScrollIntoViewだと最小限しか動かない）。
            ScrollSelectedToTop(FrontList, idx + 1);

            PlayFrontGroup(next);
        }

        private void PlayerRear_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (RearLinked) return;
            if (_currentRearGroup is null) return;
            int idx = _rearGroups.IndexOf(_currentRearGroup);
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

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                PlayPauseButton.Content = "▶";
                _wantsPlaying = false;
            }
            else
            {
                PlayerFront.Play();
                if (_currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null)) PlayerRear.Play();
                PlayPauseButton.Content = "⏸";
                _wantsPlaying = true;
            }
            _isPlaying = !_isPlaying;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            PlayerFront.Volume = e.NewValue; // Rearは常時Volume=0（音声はFrontのみ）
        }

        private void ScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFrontGroup?.FrontVideoPath == null)
            {
                MessageBox.Show("Front動画を再生してから撮影してください。", "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information);
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
                MessageBox.Show($"スクリーンショットの保存に失敗しました。\n{ex.Message}", "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void StopPlayback()
        {
            PlayerFront.Stop();
            PlayerRear.Stop();
            _isPlaying = false;
            _wantsPlaying = false;
            PlayPauseButton.Content = "▶";
        }

        private void RearVisibleCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool hasRear = _currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null);
            if (!hasRear)
            {
                RearVisibleCheck.IsChecked = false;
                return;
            }
            RearPipBorder.Visibility = RearVisibleCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (RearVisibleCheck.IsChecked == true && _wantsPlaying)
                PlayerRear.Play();
        }

        private void RearLinkedCheck_Changed(object sender, RoutedEventArgs e)
        {
            // RearLinkedCheckはXAMLでIsChecked="True"指定のため、InitializeComponent実行中
            // （まだRearList等が未接続の段階）にもこのイベントが発火する。ガード必須。
            if (RearList == null) return;

            // リアリストは追従ON/OFFに関わらず常に選択可能にする（手動選択のしやすさのため）。

            if (RearLinked && _currentFrontGroup?.HasRear == true)
                PlayRearGroup(_currentFrontGroup);
        }

        private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlayerFront == null) return;

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
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
            if (_currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null))
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

        // ---- シーク ----

        private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            _wasPlayingBeforeSeekDrag = _isPlaying;
            if (_isPlaying)
            {
                PlayerFront.Pause();
                PlayerRear.Pause();
                _isPlaying = false;
                PlayPauseButton.Content = "▶";
            }
        }

        private async void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSliderEvent) return;
            if (!_isDragging || _dragCompleting || !PlayerFront.NaturalDuration.HasTimeSpan) return;
            await RequestLiveSeekAsync(e.NewValue);
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

        private async void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragCompleting = true;
            while (_seekLiveBusy)
                await Task.Delay(15);

            var target = TimeSpan.FromSeconds(SeekSlider.Value);
            await PlayerFront.StepToVideoOnlyAsync(target, timeoutMs: 2000);
            if (_currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null))
                await PlayerRear.StepToVideoOnlyAsync(target, timeoutMs: 2000);

            _isDragging = false;
            _dragCompleting = false;

            if (_wasPlayingBeforeSeekDrag)
            {
                PlayerFront.Play();
                if (_currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null)) PlayerRear.Play();
                _isPlaying = true;
                PlayPauseButton.Content = "⏸";
            }
        }

        // クリックした位置に即座にシークする（MainWindow本体のSeekBarと同じ挙動）
        private void SeekSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !PlayerFront.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            PlayerFront.Position = TimeSpan.FromSeconds(t);
            if (_currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null))
                PlayerRear.Position = TimeSpan.FromSeconds(t);
        }

        private void SeekSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Slider sl || !PlayerFront.NaturalDuration.HasTimeSpan) return;
            double pct = Math.Clamp(e.GetPosition(sl).X / sl.ActualWidth, 0, 1);
            double t = sl.Maximum * pct;
            sl.Value = t;
            PlayerFront.Position = TimeSpan.FromSeconds(t);
        }

        // ホバー中の位置に対応する時刻をポップアップでプレビュー表示する（MainWindow本体と同等）
        private void SeekSlider_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            double width = SeekSlider.ActualWidth;
            if (!PlayerFront.NaturalDuration.HasTimeSpan || width <= 0) { SeekPreviewPopup.IsOpen = false; return; }

            double ratio = Math.Clamp(e.GetPosition(SeekSlider).X / width, 0, 1);
            double total = PlayerFront.NaturalDuration.TimeSpan.TotalSeconds;
            SeekPreviewText.Text = TimeSpan.FromSeconds(ratio * total).ToString(@"hh\:mm\:ss");

            SeekPreviewPopup.IsOpen = true;
            var popupChild = (FrameworkElement)SeekPreviewPopup.Child;
            popupChild.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double halfW = popupChild.DesiredSize.Width / 2;
            double cursorX = e.GetPosition(SeekSlider).X;
            SeekPreviewPopup.HorizontalOffset = Math.Clamp(cursorX - halfW, 0, Math.Max(0, width - halfW * 2));
        }

        private void SeekSlider_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            SeekPreviewPopup.IsOpen = false;
        }

        // ---- HUD更新（Frontの実フレーム表示に同期。FrameDisplayedはAction<double>で秒単位pts） ----

        private void OnFrontFrameDisplayed(double ptsSeconds)
        {
            var pos = TimeSpan.FromSeconds(ptsSeconds);

            var frame = DashcamSensorLookup.FindNearest(_sensorFrames, pos);
            Hud.UpdateFrame(frame);
            if (frame != null)
                MapView.SetCarPosition(frame.Latitude, frame.Longitude, frame.HasGpsFix);

            if (!_isDragging)
            {
                _suppressSliderEvent = true;
                SeekSlider.Value = ptsSeconds;
                _suppressSliderEvent = false;
            }

            var duration = PlayerFront.NaturalDuration.HasTimeSpan ? PlayerFront.NaturalDuration.TimeSpan : TimeSpan.Zero;
            PositionText.Text = $"{pos:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        }

        // ---- Rearのドリフト追従 ----

        private void SyncTimer_Tick(object? sender, EventArgs e)
        {
            bool hasActiveRear = _currentFrontGroup?.HasRear == true || (!RearLinked && _currentRearGroup != null);
            if (!hasActiveRear || _isDragging)
                return;

            var diff = PlayerFront.Position - PlayerRear.Position;
            if (RearLinked && diff.Duration() > ResyncThreshold)
                PlayerRear.Position = PlayerFront.Position;
        }

        // ---- 走行軌跡マップ: 前後数ファイル分をつないだ連続した経路として表示する ----
        private async Task RefreshMapBufferAsync(DashcamMediaGroup currentGroup)
        {
            int idx = _frontGroups.IndexOf(currentGroup);
            if (idx < 0)
            {
                MapView.SetRoute(new List<MapSegment>());
                return;
            }

            int prevCount = Math.Min(1, idx);
            int nextCount = Math.Min(2, _frontGroups.Count - 1 - idx);
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

            MapView.SetRoute(DashcamMapPointBuilder.BuildSegments(merged));
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
            _suppressSelectionEvent = true;
            FrontList.SelectedItem = group;
            _suppressSelectionEvent = false;
            FrontList.ScrollIntoView(group);
            PlayFrontGroup(group);

            await Task.CompletedTask;
            return true;
        }
    }
}
