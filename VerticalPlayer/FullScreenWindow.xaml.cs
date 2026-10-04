using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>MainWindow側の表示関連設定をFullScreenWindowへ引き継ぐためのスナップショット。
    /// FullScreenWindowのPlayerはMainWindowのPlayerとは別インスタンス（別のAVEngineを内部に持つ）
    /// のため、明示的に渡さない限りHW/Denoise/DynamicContrast/DNN超解像/色空間補正/
    /// コントラスト等の設定は一切引き継がれず、既定値（多くはOFF、UseGpuPresenterに至っては
    /// 従来は一度もtrueにされていなかった）のまま再生されてしまっていた。</summary>
    public sealed class FullScreenVisualSettings
    {
        public bool HwAccel;
        public bool Denoise;
        public bool DynamicContrast;
        public bool Deinterlace;
        public int ColorMatrixMode;
        public double Contrast;
        public double Saturation;
        public double Gamma;
        public float SharpAmount;
        public bool DnnEnabled;
        public string? DnnModelFileName;
        public float SuperResolutionScale;
    }

    public partial class FullScreenWindow : Window
    {
        private readonly MainWindow _owner;
        private bool _isPlaying = false;
        private bool _isMuted = false;
        private double _prevVol = 0.7;
        private bool _isDragging = false;
        private bool _seekLiveBusy = false;
        private double? _seekLivePendingSeconds = null;
        private bool _wasPlayingBeforeSeekDrag = false;
        private bool _dragCompleting = false;
        private double _frameMs = 100;
        private MediaInfoNative? _mediaInfo = null;
        private int _actualFrameCount = 0;
        private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _osdTimer = new() { Interval = TimeSpan.FromSeconds(3) };

        // ── コンストラクタ ──
        public FullScreenWindow(MainWindow owner, Uri source, TimeSpan position,
                                double volume, bool isMuted, double speed,
                                double frameMs, double rotationAngle,
                                FullScreenVisualSettings visual)
        {
            InitializeComponent();
            _owner = owner;
            _frameMs = frameMs;
            _isMuted = isMuted;
            _prevVol = volume;

            // ── MainWindow側の表示設定を引き継ぐ ──
            // UseGpuPresenter=trueにしない限り、GPU描画パス（D3DImage経由）が有効化されず
            // WriteableBitmapフォールバックのままになる。ダイナミックコントラスト・超解像
            // （Lanczos/DNN両方）・色空間補正はすべてこのGPU描画パス上でのみ動作するため、
            // これが未設定だとフルスクリーンだけ画質が明らかに落ちる（コントラスト/彩度/
            // ガンマのみCPU版フォールバックがあるため多少は反映されるが、それ以外は完全無効）。
            Player.UseGpuPresenter = true;
            // ここを追加：パケット先読みはMainWindowと同じく常時ON。未設定（既定OFF）だと音声パケットの取り込みが映像デコードと
            // 同じスレッドで直列になり、映像が時計待ちでブロックしている間に音声が枯渇して、音声クロックが実時間の約1/5に遅れ、
            // フレームもガクつく（フルスクリーンだけ症状が出ていた原因）。
            Player.PacketPrefetch = true;
            Player.HardwareAcceleration = visual.HwAccel;
            Player.Denoise = visual.Denoise;
            Player.Deinterlace = visual.Deinterlace;
            Player.DynamicContrast = visual.DynamicContrast;
            Player.ColorMatrixMode = visual.ColorMatrixMode;
            Player.Contrast = visual.Contrast;
            Player.Saturation = visual.Saturation;
            Player.Gamma = visual.Gamma;
            Player.SharpAmount = visual.SharpAmount;
            if (!string.IsNullOrEmpty(visual.DnnModelFileName))
                Player.DnnModelFileName = visual.DnnModelFileName;
            if (visual.DnnEnabled)
                Player.DnnSuperResolutionEnabled = true; // 未ビルドの解像度ならバックグラウンドでビルドされる
            else
                Player.SuperResolutionScale = visual.SuperResolutionScale;

            VolSlider.Value = volume;
            SpeedLabel.Text = $"{speed:F1}×";
            Player.Volume = isMuted ? 0 : volume;
            Player.SpeedRatio = speed;
            Player.Source = source;

            if (rotationAngle != 0)
                Player.LayoutTransform = new RotateTransform(rotationAngle);
            Player.DisplayRotation = rotationAngle;

            // 実際のデコードモード（HW/SW）表示。MainWindow側と同じ考え方でDecodeModeChangedに連動させる。
            Player.DecodeModeChanged += mode =>
            {
                HwStatusLabel.Text = mode.StartsWith("HW") ? "H/W" : "S/W";
                HwStatusLabel.Foreground = mode.StartsWith("HW")
                    ? new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE))
                    : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            };

            // 実測FPS表示（1秒間隔で実際に表示されたフレーム数を集計。MainWindow側と同じ方式）
            Player.FrameDisplayed += pts =>
            {
                OnFrameDisplayedForFps();
                OnFrameDisplayedForStep(pts); // ここを変更：コマ送りの基準位置管理にも表示ptsを渡す
            };

            // DNN超解像エンジンのビルド状態表示（ビルド中は赤、ビルド済み＆再生中はTensorRT再生中を黄色）
            Player.DnnBuildStateChanged += building =>
            {
                if (building)
                {
                    StatusText.Text = "超解像エンジンをビルド中…";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
                }
                else
                {
                    UpdateDnnStatusText();
                }
            };

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

            // 新しいファイルを開いたので、旧ファイル用にビルド中/ビルド済みだったDNNエンジンは
            // 手放し新ファイル用に作り直す（MainWindow側と同じ対策。JumpFileでのファイル送り時に必要）
            Player.ResetDnnEngineForNewFile();

            // HW/コーデック/fps系の状態表示更新用にMediaInfoNativeで詳細解析
            if (Player.Source?.LocalPath != null)
                AnalyzeAndShowMediaInfo(Player.Source.LocalPath);
        }

        // MediaInfoNativeで詳細解析し、コーデック表示ラベルとコマ送り間隔を更新する
        // （MainWindow.AnalyzeAndShowMediaInfoと同じ考え方。FullScreenWindowは別インスタンスの
        // Playerを持つため、コーデック表示もこちら側で独立して取得する必要がある）
        private void AnalyzeAndShowMediaInfo(string path)
        {
            try
            {
                var mi = new MediaInfoNative(path);
                if (!mi.Success)
                {
                    Trace($"FS MediaInfo: failed for {path}");
                    _mediaInfo?.Dispose();
                    _mediaInfo = null;
                    UpdateCodecStatusBar();
                    return;
                }

                double fps = mi.VideoFrameRate;
                if (fps > 0) _frameMs = 1000.0 / fps;

                _mediaInfo?.Dispose();
                _mediaInfo = mi;
                UpdateCodecStatusBar();
            }
            catch (Exception ex)
            {
                Trace($"FS AnalyzeAndShowMediaInfo EXCEPTION: {ex.Message}");
            }
        }

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

        // TensorRTエンジンで実際に再生中かどうかをStatusTextへ反映する（黄色）。MainWindow側と同じ考え方。
        private void UpdateDnnStatusText()
        {
            if (Player.DnnSuperResolutionEnabled && Player.IsDnnReadyForCurrentResolution)
            {
                StatusText.Text = "TensorRTエンジンで再生中";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            }
            else
            {
                StatusText.Text = "";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            }
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
        private void Seek_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            _frameStepPosValid = false; // ここを追加：シークバー操作後はコマ送りの論理位置を取り直す
            _wasPlayingBeforeSeekDrag = _isPlaying;
            if (_isPlaying) { Player.Pause(); _isPlaying = false; UpdateIcon(); _timer.Stop(); }
        }

        private async void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragCompleting = true;
            while (_seekLiveBusy)
                await Task.Delay(15);

            var target = TimeSpan.FromSeconds(SeekBar.Value);
            await Player.StepToVideoOnlyAsync(target, timeoutMs: 2000);

            _isDragging = false;
            _dragCompleting = false;
            if (_wasPlayingBeforeSeekDrag)
            {
                Player.Play();
                _isPlaying = true;
                UpdateIcon();
                _timer.Start();
            }
        }

        // ドラッグ中はThumb移動のたびに軽量プレビュー（直近キーフレーム即表示、音声には触れない）。
        // 前回のプレビューが終わっていない間に来た移動要求は最新値だけ残して間引く。
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
                    await Player.FastSeekPreviewAsync(TimeSpan.FromSeconds(target));
                }
            }
            finally
            {
                _seekLiveBusy = false;
            }
        }

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
        {
            _frameStepPosValid = false; // ここを変更：±10秒移動後はコマ送りの論理位置を取り直す
            Player.Position -= TimeSpan.FromSeconds(10);
        }
        private void FastForward_Click(object sender, RoutedEventArgs e)
        {
            _frameStepPosValid = false;
            Player.Position += TimeSpan.FromSeconds(10);
        }

        // ── コマ送り / コマ戻し（メインウィンドウ・ドラレコモードと同仕様）──
        // ・ボタンを押した瞬間に1回送り、押し続けると0.4秒後から約0.09秒ごとに繰り返す（Shift+←/→のキーリピートも同様）
        // ・1回の量はメインウィンドウの「コマ」設定（_owner.FrameStepFramesSetting）に従う
        // ・基準位置は送り自身が保持する論理位置（表示中フレームのptsはキーフレーム丸めでぶれるため毎回の基準にしない）
        private double FrameStepSeconds
        {
            get
            {
                double fps = _mediaInfo is { Success: true } ? _mediaInfo.VideoFrameRate : 0;
                return fps is > 1 and < 240 ? 1.0 / fps : 1.0 / 30.0;
            }
        }
        private bool _frameStepBusy;
        private long _lastStepEndTs;            // 直近のコマ送りが終わった時刻(Stopwatch)
        private TimeSpan _frameStepPos;         // 連続コマ送り時の論理位置
        private bool _frameStepPosValid;
        private DispatcherTimer? _frameStepRepeatTimer;
        private int _frameStepRepeatDir;
        private double _lastDisplayedPts = -1;  // 直近に画面へ出たフレームの時刻(秒)

        private void FrameStepButton_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not Button b || !int.TryParse((string)b.Tag, out int dir)) return;
            _frameStepRepeatDir = dir;
            _ = StepFrameAsync(dir);

            if (_frameStepRepeatTimer == null)
            {
                _frameStepRepeatTimer = new DispatcherTimer(DispatcherPriority.Input);
                _frameStepRepeatTimer.Tick += FrameStepRepeatTimer_Tick;
            }
            _frameStepRepeatTimer.Interval = TimeSpan.FromMilliseconds(400);
            _frameStepRepeatTimer.Start();
        }

        private void FrameStepRepeatTimer_Tick(object? sender, EventArgs e)
        {
            if (_frameStepRepeatTimer != null) _frameStepRepeatTimer.Interval = TimeSpan.FromMilliseconds(90);
            _ = StepFrameAsync(_frameStepRepeatDir); // 前の送りが終わっていなければStepFrameAsync側で読み飛ばす
        }

        private void FrameStepButton_Up(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _frameStepRepeatTimer?.Stop();
        }

        // コマ送り以外の要因で位置が動いたら論理位置を無効にし、次回は表示中のフレームから始め直す。
        private void OnFrameDisplayedForStep(double ptsSeconds)
        {
            _lastDisplayedPts = ptsSeconds;
            if (_frameStepPosValid && !_frameStepBusy)
            {
                bool stepsEnded = Stopwatch.GetElapsedTime(_lastStepEndTs).TotalMilliseconds > 700;
                if (_isPlaying || (stepsEnded && Math.Abs(ptsSeconds - _frameStepPos.TotalSeconds) > 1.5))
                    _frameStepPosValid = false;
            }
        }

        private async Task StepFrameAsync(int dir)
        {
            if (_frameStepBusy || _isDragging || _dragCompleting) return;
            if (Player.Source == null || !Player.NaturalDuration.HasTimeSpan) return;

            _frameStepBusy = true;
            try
            {
                if (_isPlaying) { Player.Pause(); _isPlaying = false; UpdateIcon(); _timer.Stop(); }

                var oneFrame = TimeSpan.FromSeconds(FrameStepSeconds);
                int frames = _owner.FrameStepFramesSetting;
                var step = TimeSpan.FromTicks(oneFrame.Ticks * frames);
                var dur = Player.NaturalDuration.TimeSpan;

                bool rebased = false;
                var prevStepPos = _frameStepPos;
                if (!_frameStepPosValid)
                {
                    // 新しいコマ送りの開始。いま画面に出ているフレームの時刻を起点にする
                    var basePos = Player.Position;
                    if (_lastDisplayedPts >= 0)
                    {
                        var shown = TimeSpan.FromSeconds(_lastDisplayedPts);
                        if ((shown - basePos).Duration() < TimeSpan.FromSeconds(2)) basePos = shown; // 前のファイルの古い値は使わない
                    }
                    _frameStepPos = basePos;
                    rebased = true;
                }

                var next = _frameStepPos + TimeSpan.FromTicks(step.Ticks * dir);
                var last = dur - oneFrame;
                if (last < TimeSpan.Zero) last = TimeSpan.Zero;
                if (next < TimeSpan.Zero) next = TimeSpan.Zero;
                if (next > last) next = last;

                _frameStepPos = next;
                _frameStepPosValid = true;

                // 半フレーム先へ着地させ、丸め誤差で前のフレームに落ちるのを防ぐ
                var seekTarget = next + TimeSpan.FromTicks(oneFrame.Ticks / 2);
                var swStep = Stopwatch.StartNew();
                double shownBefore = _lastDisplayedPts;
                // コマ送りは音声再生を伴わないため、映像デコードだけをシークして1フレーム表示する専用APIを使う
                await Player.StepToVideoOnlyAsync(seekTarget, timeoutMs: 1000);
                Trace($"FS [Step] dir={dir:+0;-0} {frames}コマ(1コマ={oneFrame.TotalMilliseconds:F1}ms) " +
                      $"Position={Player.Position.TotalSeconds:F3} 表示pts: {shownBefore:F3}→{_lastDisplayedPts:F3} " +
                      $"起点の取り直し={(rebased ? "あり" : "なし")} 前回の論理位置={(rebased ? double.NaN : prevStepPos.TotalSeconds):F3} " +
                      $"目標={next.TotalSeconds:F3} 所要{swStep.ElapsedMilliseconds}ms");
                Timer_Tick(null, EventArgs.Empty); // 一時停止中はタイマーが止まっているため、シークバー/時刻表示を1回更新する
            }
            finally
            {
                _lastStepEndTs = Stopwatch.GetTimestamp();
                _frameStepBusy = false;
            }
        }

        // ── 前/次ファイル ──
        private void PrevFile_Click(object sender, RoutedEventArgs e) => JumpFile(-1);
        private void NextFile_Click(object sender, RoutedEventArgs e) => JumpFile(+1);

        private void JumpFile(int delta)
        {
            string? path = _owner.GetAdjacentFile(delta);
            if (path == null) return;
            Player.Source = new Uri(path);
            Player.SpeedRatio = 1.0; // 特殊再生（スロー等）状態はファイル単位で持ち回さない
            SpeedLabel.Text = "1.0×";
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

        // ここから変更：フルスクリーン側のPlayer（別AVEngine）を必ず停止・解放してからMainWindowへ戻す。
        // 従来はPlayerを止めずにWindowを閉じていたため、閉じたあとも裏でデコード/音声再生が走り続け、
        // ノーマル<>フルスクリーンを往復するたびにエンジンが増えて、音声の二重再生とパフォーマンス低下を起こしていた。
        private bool _returnedToOwner;

        private void ExitFs()
        {
            Trace("FullScreenWindow: ExitFs");
            ReturnToOwnerOnce();
            this.Close();
        }

        private void ReturnToOwnerOnce()
        {
            if (_returnedToOwner) return;
            _returnedToOwner = true;

            _timer.Stop(); _osdTimer.Stop(); _frameStepRepeatTimer?.Stop();
            _mediaInfo?.Dispose();
            _mediaInfo = null;

            // 停止前に、MainWindowへ引き継ぐ状態を控える
            var src = Player.Source;
            var pos = Player.Position;
            var vol = Player.Volume;
            var speed = Player.SpeedRatio;
            bool playing = _isPlaying;

            // MainWindowが再生を再開する前に、こちらのエンジンを完全に止めて音声デバイスを解放する
            try
            {
                Player.Pause();
                Player.Stop();
                Player.Source = null;
            }
            catch (Exception ex) { Trace($"FS Player shutdown EXCEPTION: {ex.Message}"); }
            _isPlaying = false;

            _owner.ReturnFromFullScreen(src, pos, vol, speed, playing);
        }

        // ×ボタン/Alt+F4/タスクバーなど、ExitFsを通らずに閉じられた場合も同じ後始末を行う
        protected override void OnClosed(EventArgs e)
        {
            ReturnToOwnerOnce();
            base.OnClosed(e);
        }
        // ここまで

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
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrameAsync(-1); // 押しっぱなしのキーリピートで連続
                    else { _frameStepPosValid = false; Player.Position -= TimeSpan.FromSeconds(10); }
                    e.Handled = true; break;
                case Key.Right:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _ = StepFrameAsync(+1);
                    else { _frameStepPosValid = false; Player.Position += TimeSpan.FromSeconds(10); }
                    e.Handled = true; break;
                case Key.Up:
                    VolSlider.Value = Math.Min(VolSlider.Value + 0.05, 1.0); e.Handled = true; break;
                case Key.Down:
                    VolSlider.Value = Math.Max(VolSlider.Value - 0.05, 0.0); e.Handled = true; break;
            }
        }

        private static void Trace(string msg)
        {
#if DEBUG
            try
            {
                File.AppendAllText(
                    AppLogPaths.GetPath("trace.log"), // ここを変更：MainWindowと同じ出力先ルール(AppLogPaths)に統一
                    $"{DateTime.Now:HH:mm:ss.fff} | {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
#endif
        }
    }
}
