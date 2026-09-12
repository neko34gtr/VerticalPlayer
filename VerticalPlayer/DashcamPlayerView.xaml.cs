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
using VerticalPlayer.Media;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ドラレコ再生モードのメインビュー。
    /// ドライブ選択→Front/Rear動画リスト化→リストから選択再生（Frontは連続再生、
    /// Rearはリア追従設定でFrontに連動 or 独立動作）・シーク追従・センサーHUD表示を担当する。
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
        // 独立フラグとして保持し、どちらのMediaOpenedが先に来ても正しく再生開始できるようにする
        // （このフラグが無いと、Frontの方が先に開いた瞬間だけRearをPlay()してしまい、Rearの
        // オープンがまだ終わっていない時はPlay()が無視されて「次ファイルでリアだけ固まる」
        // 不具合になる。一度固まるとその後は何もPlay()を呼び直さないため、リア表示のON/OFFでも
        // 復帰せず再起動待ちになっていた）
        private bool _wantsPlaying;

        // ---- シーク（MainWindowのSeekBarと同じ「ドラッグ中は間引きプレビュー、確定時に正確着地」方式） ----
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

        public DashcamPlayerView()
        {
            InitializeComponent();

            // ドラレコは1ファイルあたり約2分間隔で次々切り替わり、かつSDカード等の低速
            // ストレージ運用が前提のため、既存の「パケット先読み（低速ストレージ対策）」
            // パイプラインをこの画面のFront/Rear両方で既定ONにする（MainWindow本体は
            // 既定OFFだが、ここは切替頻度・ストレージ特性が明確に異なるため独立して有効化）。
            PlayerFront.PacketPrefetch = true;
            PlayerRear.PacketPrefetch = true;

            PlayerFront.FrameDisplayed += OnFrontFrameDisplayed;

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _syncTimer.Tick += SyncTimer_Tick;
            _syncTimer.Start();
        }

        private void DashcamPlayerView_Loaded(object sender, RoutedEventArgs e) => RefreshDriveList();

        // ---- ドライブ選択 ----

        private void RefreshDrivesButton_Click(object sender, RoutedEventArgs e) => RefreshDriveList();

        private void RefreshDriveList()
        {
            string? selected = (DriveCombo.SelectedItem as DriveInfo)?.Name;

            DriveCombo.ItemsSource = null;
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
            DriveCombo.ItemsSource = drives;
            DriveCombo.DisplayMemberPath = "Name";

            var restore = drives.FirstOrDefault(d => d.Name == selected);
            DriveCombo.SelectedItem = restore ?? drives.FirstOrDefault();
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            if (DriveCombo.SelectedItem is not DriveInfo drive)
            {
                MessageBox.Show("ドライブを選択してください。", "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _groups = DashcamFileScanner.Scan(drive.RootDirectory.FullName);
            _frontGroups = _groups.Where(g => g.HasFront).ToList();
            _rearGroups = _groups.Where(g => g.HasRear).ToList();

            FrontList.ItemsSource = _frontGroups;
            RearList.ItemsSource = _rearGroups;

            if (_frontGroups.Count == 0 && _rearGroups.Count == 0)
            {
                MessageBox.Show("対応する動画が見つかりませんでした（NORMALフォルダ等を確認してください）。",
                    "ドラレコモード", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---- リスト選択 → 再生 ----

        private void FrontList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvent) return;
            if (FrontList.SelectedItem is not DashcamMediaGroup group) return;
            PlayFrontGroup(group);
        }

        private void RearList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvent) return;
            if (RearLinked) return; // 追従中はFront選択がRearを決めるため、独立選択は無効
            if (RearList.SelectedItem is not DashcamMediaGroup group) return;
            PlayRearGroup(group);
        }

        private void PlayFrontGroup(DashcamMediaGroup group)
        {
            _currentFrontGroup = group;
            _sensorFrames = new List<DashcamSensorFrame>(); // Frontの動画長が判明してからParseし直す（MediaOpened側）
            _wantsPlaying = true; // Front/RearどちらのMediaOpenedが先に来ても再生開始させる意図フラグ

            if (group.FrontVideoPath != null)
                PlayerFront.Source = new Uri(group.FrontVideoPath);

            if (RearLinked)
            {
                if (group.HasRear)
                    PlayRearGroup(group, keepListSelectionOnly: true);
                else
                {
                    PlayerRear.Stop();
                    _currentRearGroup = null;
                }
            }
        }

        private void PlayRearGroup(DashcamMediaGroup group, bool keepListSelectionOnly = false)
        {
            _currentRearGroup = group;
            _wantsPlaying = true; // 独立選択(リア追従OFF時)から呼ばれた場合もここで意図をセットする
            if (group.RearVideoPath != null)
                PlayerRear.Source = new Uri(group.RearVideoPath);

            if (!keepListSelectionOnly)
            {
                _suppressSelectionEvent = true;
                RearList.SelectedItem = group;
                _suppressSelectionEvent = false;
            }
        }

        // ---- 再生制御 ----

        private void PlayerFront_MediaOpened(object sender, RoutedEventArgs e)
        {
            PlayerFront.ResetDnnEngineForNewFile();

            var duration = PlayerFront.NaturalDuration.HasTimeSpan
                ? PlayerFront.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            SeekSlider.Maximum = duration.TotalSeconds;

            // NMEAはFront/Rearどちらのファイルにも紐づき得るためFront優先で読む。
            // RawTicksの単位が不明なため、動画長でレコードを均等按分する。
            string? nmeaPath = _currentFrontGroup?.FrontNmeaPath ?? _currentFrontGroup?.RearNmeaPath;
            _sensorFrames = nmeaPath != null
                ? NmeaSensorParser.Parse(nmeaPath, _currentFrontGroup?.Timestamp, duration)
                : new List<DashcamSensorFrame>();

            MapView.SetRoute(DashcamMapPointBuilder.BuildSegments(_sensorFrames));

            if (_wantsPlaying)
            {
                PlayerFront.Play();
                _isPlaying = true;
                PlayPauseButton.Content = "⏸";
            }

            if (PlayerFront.NaturalVideoWidth > 0 && PlayerFront.NaturalVideoHeight > 0)
                RequestWindowFit?.Invoke(PlayerFront.NaturalVideoWidth * ZoomScale, PlayerFront.NaturalVideoHeight * ZoomScale);
        }

        private void PlayerRear_MediaOpened(object sender, RoutedEventArgs e)
        {
            PlayerRear.ResetDnnEngineForNewFile();

            // Frontの方が先に開いてPlay()済みでも、Rearのオープンはこの時点で初めて完了するため、
            // ここで改めてPlay()を呼ぶ（Front側からの直接Play()呼び出しに依存すると、Rearが
            // まだ開き切っていないタイミングでPlay()が無視され、以後何もPlay()を呼ばなくなり
            // 「次ファイルでリアだけ固まる／リア表示のON/OFFでも復帰しない」不具合になっていた）。
            if (_wantsPlaying)
                PlayerRear.Play();
        }

        // Front連続再生: リスト内の次のグループへ自動的に進む
        private void PlayerFront_MediaEnded(object sender, RoutedEventArgs e) => AdvanceToNextFrontScene();

        // 「次のシーン」ボタン: 再生終了を待たず手動で次のグループへ進む
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
            PlayFrontGroup(next);
        }

        // Rear単独連続再生: リア追従OFF時のみ、Rear自身のリストで次へ進む
        private void PlayerRear_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (RearLinked) return;
            if (_currentRearGroup is null) return;
            int idx = _rearGroups.IndexOf(_currentRearGroup);
            if (idx < 0 || idx + 1 >= _rearGroups.Count) return;

            PlayRearGroup(_rearGroups[idx + 1]);
            PlayerRear.Play();
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

        /// <summary>
        /// Front/Rearのデコードを完全に停止する。ドラレコモードから通常モードへ戻る際、
        /// バックグラウンドのデコードスレッドを残さないためMainWindow側から呼び出す。
        /// </summary>
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

            // 表示ONにした際、何らかの理由でRearが再生開始できていなければここで念のため再試行する
            // （本来は_wantsPlayingベースのMediaOpened処理で解決済みのはずだが、保険として残す）
            if (RearVisibleCheck.IsChecked == true && _wantsPlaying)
                PlayerRear.Play();
        }

        private void RearLinkedCheck_Changed(object sender, RoutedEventArgs e)
        {
            // RearLinkedCheckはXAMLでIsChecked="True"指定のため、InitializeComponent実行中
            // （まだRearList等が未接続の段階）にもこのイベントが発火する。ガード必須。
            if (RearList == null) return;

            RearList.IsEnabled = !RearLinked;

            if (RearLinked && _currentFrontGroup?.HasRear == true)
                PlayRearGroup(_currentFrontGroup);
        }

        private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ZoomCombo初期項目にIsSelected="True"を指定しているため、InitializeComponent実行中
            // （まだPlayerFront等が未接続の段階）にもこのイベントが発火する。ガード必須。
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

        // MainWindowのEnableDnnSuperResolutionAsyncと同じ「未ビルドなら一時停止して待つ」方式
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
        // MainWindowのSeekBar実装と同じ二段構え:
        //  ・ドラッグ中(ValueChanged): 直近の要求だけを残して間引きながらFastSeekPreviewAsync（軽量プレビュー）
        //  ・DragCompleted: 進行中のプレビューが終わるのを待ってからStepToVideoOnlyAsyncで正確に着地→Play()
        // （旧実装はこの間引き・完了待ちが無く、ドラッグ中の重複呼び出しにより「映像が反映されない」
        //   不具合の原因になっていた）

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
    }
}
