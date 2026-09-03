using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
    /// <summary>表示倍率モード。Autoはウィンドウいっぱいに黒帯なしで埋める（クロップ）、それ以外は動画本来のピクセル寸法基準の固定倍率。</summary>
    public enum VideoScaleMode { Auto, Zoom1x, Zoom2x, Zoom2_5x, Zoom3x, Zoom4x }

    /// <summary>Auto時に基準とするアスペクト比。Nativeは動画本来の比率、Freeはウィンドウに完全一致（縦横比無視）、
    /// Force系は指定比率に強制（映像は非一様に引き伸ばされる＝アナモルフィック補正用途）。</summary>
    public enum VideoAspectMode { Native, Free, Force16x9, Force4x3, Force9x16 }

    /// <summary>
    /// MediaFailed イベント用の引数。
    /// System.Windows.Controls.ExceptionRoutedEventArgs はコンストラクタが protected internal のため
    /// 自前で代替クラスを用意している。プロパティ名 ErrorException は MediaElement と同名にしてあるので
    /// 既存の Player_MediaFailed(object sender, ExceptionRoutedEventArgs e) は
    /// 引数の型だけ FfmpegMediaFailedEventArgs に差し替えれば本体コードは無修正で動く。
    /// </summary>
    public sealed class FfmpegMediaFailedEventArgs : RoutedEventArgs
    {
        public Exception? ErrorException { get; }
        public FfmpegMediaFailedEventArgs(RoutedEvent routedEvent, object source, Exception? ex)
            : base(routedEvent, source)
        {
            ErrorException = ex;
        }
    }

    /// <summary>
    /// System.Windows.Controls.MediaElement とほぼ同じ表面API（Source/Volume/SpeedRatio/Position/
    /// NaturalDuration/NaturalVideoWidth/NaturalVideoHeight/LoadedBehavior/Play/Pause/Stop/
    /// MediaOpened/MediaEnded/MediaFailed）を持つ、FFmpeg.AutoGen(映像) + MediaElement(音声) の
    /// ハイブリッド構成コントロール。
    ///
    /// 【設計】
    /// - 表示される映像：FFmpeg.AutoGenでデコードした frame を WriteableBitmap に描画（AVEngine）。
    /// - 音声・シーク・速度制御・マスタークロック：非表示の System.Windows.Controls.MediaElement
    ///   （_audio、Visibility=Collapsed）をそのまま流用。実績のあるMediaElementの音声パイプラインを
    ///   信頼し、自前の音声デコード/NAudio同期は行わない（破綻リスクが高いため撤去）。
    /// - _audio.Position を約50msごとにポーリングして AVEngine.SetExternalClock() へ渡し、
    ///   映像フレームの表示タイミングをそれに追従させる（早ければ待つ、40ms以上遅れたら間引く）。
    ///
    /// 内部の映像表示は Image(+WriteableBitmap) のみで完結しているため、WPFのLayoutTransform/
    /// RenderTransform（既存のPlayerRotation等）はそのまま従来通り効く。ネイティブ子ウィンドウを
    /// 使わないため、フルスクリーン時にOSDが映像の下に隠れる、というAirspace由来の不具合も
    /// 構造的に発生しない。
    /// </summary>
    public class FfmpegMediaElement : Decorator
    {
        private readonly Grid _root = new() { ClipToBounds = true };
        private readonly Image _image = new()
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        private readonly MediaElement _audio = new()
        {
            LoadedBehavior = MediaState.Manual,
            Visibility = Visibility.Collapsed,
            Width = 0,
            Height = 0
        };
        //private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Send)
        //{
        //    Interval = TimeSpan.FromMilliseconds(50)
        //};

        private readonly AVEngine _engine;
        private readonly GpuFramePresenter _gpuPresenter = new();
        /// <summary>true にすると、表示をWriteableBitmapからD3DImage経由（GPU土台）へ切り替える。
        /// 「高画質化エンジン設計提案」段階1の検証用フラグ。既定はfalse（従来どおり）。
        /// D3D9Ex/D3D11初期化に失敗した環境では自動的にfalse相当（WriteableBitmap）にフォールバックする。</summary>
        public bool UseGpuPresenter { get; set; }
        /// <summary>D3D9Ex/D3D11の初期化に成功しGPU描画パスが実際に使えるかどうか。
        /// falseの場合、UseGpuPresenter=trueにしていてもWriteableBitmap経路のまま
        /// （GPU専用機能＝コントラスト/ダイナミックコントラスト/超解像/比較ビューは無効）。</summary>
        public bool IsGpuPresenterAvailable => _gpuPresenter.IsAvailable;
        private Uri? _source;
        private bool _isPlaying;

        public static readonly RoutedEvent MediaOpenedEvent = EventManager.RegisterRoutedEvent(
            nameof(MediaOpened), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(FfmpegMediaElement));
        public event RoutedEventHandler MediaOpened
        {
            add => AddHandler(MediaOpenedEvent, value);
            remove => RemoveHandler(MediaOpenedEvent, value);
        }

        public static readonly RoutedEvent MediaEndedEvent = EventManager.RegisterRoutedEvent(
            nameof(MediaEnded), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(FfmpegMediaElement));
        public event RoutedEventHandler MediaEnded
        {
            add => AddHandler(MediaEndedEvent, value);
            remove => RemoveHandler(MediaEndedEvent, value);
        }

        public static readonly RoutedEvent MediaFailedEvent = EventManager.RegisterRoutedEvent(
            nameof(MediaFailed), RoutingStrategy.Bubble, typeof(EventHandler<FfmpegMediaFailedEventArgs>), typeof(FfmpegMediaElement));
        public event EventHandler<FfmpegMediaFailedEventArgs> MediaFailed
        {
            add => AddHandler(MediaFailedEvent, new EventHandler<FfmpegMediaFailedEventArgs>(value));
            remove => RemoveHandler(MediaFailedEvent, new EventHandler<FfmpegMediaFailedEventArgs>(value));
        }

        /// <summary>実際に使われたデコードモードが判明/変化した時に発火（例: "HW (D3D11VA)" / "SW"）。</summary>
        public event Action<string>? DecodeModeChanged
        {
            add => _engine.DecodeModeChanged += value;
            remove => _engine.DecodeModeChanged -= value;
        }

        /// <summary>ファイルのチャプター開始秒（先頭0秒は除く）をUIスレッドで通知。</summary>
        public event Action<System.Collections.Generic.List<double>>? ChaptersLoaded
        {
            add => _engine.ChaptersLoaded += value;
            remove => _engine.ChaptersLoaded -= value;
        }

        /// <summary>実際に1フレームが描画された（WritePixels済み）タイミングで、そのフレームの
        /// 再生時刻(秒)を伴って発火する。コマ送り/戻しのように「指定位置のフレームが実際に
        /// 画面に出るまで待ちたい」用途向け。固定ディレイでのポーリングより確実。</summary>
        public event Action<double>? FrameDisplayed
        {
            add => _engine.FrameDisplayed += value;
            remove => _engine.FrameDisplayed -= value;
        }

        /// <summary>次に開くファイルからハードウェアデコード(D3D11VA)を試みるかどうか。非対応/失敗時はSWへ自動フォールバック。</summary>
        public bool HardwareAcceleration
        {
            get => _engine.HardwareAccelRequested;
            set => _engine.HardwareAccelRequested = value;
        }

        private double _contrast, _saturation, _gamma;
        public double Contrast
        {
            get => _contrast;
            set { _contrast = value; _engine.SetEffects(_contrast, _saturation, _gamma); }
        }
        public double Saturation
        {
            get => _saturation;
            set { _saturation = value; _engine.SetEffects(_contrast, _saturation, _gamma); }
        }
        public double Gamma
        {
            get => _gamma;
            set { _gamma = value; _engine.SetEffects(_contrast, _saturation, _gamma); }
        }

        public bool Deinterlace
        {
            get => _engine.DeinterlaceEnabled;
            set => _engine.DeinterlaceEnabled = value;
        }

        public bool Denoise
        {
            get => _engine.DenoiseRequested;
            set => _engine.DenoiseRequested = value;
        }

        private bool _dynamicContrast;
        /// <summary>ダイナミックコントラスト（段階4、シーン平均輝度ベースの簡易オートレベル）。
        /// GPU描画パス(UseGpuPresenter)が有効な時のみ実際に効果がある。
        /// _gpuPresenterへ直接設定する（AVEngine.GpuPresenterはファイルを開いた後にしかセットされない
        /// ため、それ経由だと起動直後の設定復元タイミングで値が握りつぶされるバグがあった）。</summary>
        public bool DynamicContrast
        {
            get => _dynamicContrast;
            set
            {
                _dynamicContrast = value;
                _gpuPresenter.SetDynamicContrast(value ? 0.6f : 0f);
            }
        }

        private float _superResolutionScale = 1f;
        /// <summary>超解像（段階5、Lanczos-3＋アンシャープ）の拡大倍率。1.0=無効。
        /// GPU描画パス(UseGpuPresenter)が有効な時のみ実際に効果がある。</summary>
        public float SuperResolutionScale
        {
            get => _superResolutionScale;
            set
            {
                _superResolutionScale = value;
                _gpuPresenter.SetSuperResolution(value);
            }
        }

        private int _compareViewMode; // 0=通常、1=1枚分割（ワイプ）、2=2枚分割（フル画像を左右に並べる）
        /// <summary>PowerDVD TrueTheater風の比較表示モード。GPU描画パス(UseGpuPresenter)が
        /// 有効な時のみ実際に効果がある。</summary>
        public int CompareViewMode
        {
            get => _compareViewMode;
            set
            {
                _compareViewMode = value;
                _gpuPresenter.SetCompareMode(value);
            }
        }

        private float _sharpAmount = 0.5f;
        public float SharpAmount { get => _sharpAmount; set { _sharpAmount = value; _gpuPresenter.SetSharpAmount(value); } }
        private int _colorMatrixMode;
        public int ColorMatrixMode { get => _colorMatrixMode; set { _colorMatrixMode = value; _gpuPresenter.SetColorMatrixMode(value); } }

        // ── 表示倍率／アスペクト比（黒帯なしフィット・固定ズーム・強制比率） ──
        private VideoScaleMode _scaleMode = VideoScaleMode.Auto;
        public VideoScaleMode ScaleMode
        {
            get => _scaleMode;
            set { _scaleMode = value; RecomputeLayout(); }
        }

        private VideoAspectMode _aspectMode = VideoAspectMode.Native;
        public VideoAspectMode AspectMode
        {
            get => _aspectMode;
            set { _aspectMode = value; RecomputeLayout(); }
        }

        public Uri? Source
        {
            get => _source;
            set
            {
                _source = value;
                if (value != null)
                {
                    _audio.Source = value;
                    _engine.Open(value);
                }
                else
                {
                    _audio.Source = null;
                    _engine.Stop();
                }
            }
        }

        public MediaState LoadedBehavior { get; set; } = MediaState.Manual;

        public double Volume
        {
            get => _audio.Volume;
            set => _audio.Volume = value;
        }

        public double SpeedRatio
        {
            get => _audio.SpeedRatio;
            set
            {
                _audio.SpeedRatio = value;
                _engine.SetSpeedRatio(value);
            }
        }

        public TimeSpan Position
        {
            get => _audio.Position;
            set
            {
                _audio.Position = value;
                _engine.Seek(value);
                _engine.SetExternalClock(value.TotalSeconds, _isPlaying);
            }
        }

        public Duration NaturalDuration { get; private set; } = Duration.Automatic;
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }

        public Stretch Stretch
        {
            get => _image.Stretch;
            set => _image.Stretch = value;
        }

        public FfmpegMediaElement()
        {
            _root.Children.Add(_image);
            _root.Children.Add(_audio);
            Child = _root;

            _engine = new AVEngine(Dispatcher);
            _engine.Opened += OnEngineOpened;
            _engine.Failed += OnEngineFailed;

            _audio.MediaEnded += (s, e) => RaiseEvent(new RoutedEventArgs(MediaEndedEvent, this));
            _audio.MediaFailed += (s, e) => RaiseEvent(new FfmpegMediaFailedEventArgs(MediaFailedEvent, this, e.ErrorException));

            CompositionTarget.Rendering += OnRendering;

            // クライアント領域サイズが変わるたびに再フィット計算（黒帯なしレイアウトの追従）
            this.SizeChanged += (s, e) => RecomputeLayout();
            this.Loaded += (s, e) => RecomputeLayout();
            this.Unloaded += (s, e) => CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_isPlaying) return;
            try
            {
                _engine.SetExternalClock(_audio.Position.TotalSeconds, _isPlaying);
            }
            catch { }
        }

        private void OnEngineOpened(int w, int h, TimeSpan duration)
        {
            NaturalVideoWidth = w;
            NaturalVideoHeight = h;
            NaturalDuration = new Duration(duration);

            bool useGpu = UseGpuPresenter && _gpuPresenter.IsAvailable;
            _engine.GpuPresenter = useGpu ? _gpuPresenter : null;
            _image.Source = useGpu ? (ImageSource)_gpuPresenter.D3DImage : _engine.Bitmap;

            // デコード準備が実際に整ったこの時点でクロックを再アンカーする。
            // Source設定直後にPosition/Playが呼ばれた場合、デコード開始が間に合わず
            // マスタークロックだけ先に進んでしまい、シーク直後に大量フレームドロップが
            // 起きる問題への対策。
            _engine.SetExternalClock(_audio.Position.TotalSeconds, _isPlaying);
            RaiseEvent(new RoutedEventArgs(MediaOpenedEvent, this));
            RecomputeLayout();
        }

        private double _displayRotation;
        /// <summary>現在の表示回転角（0/90/180/270）。90/270時は黒帯なしフィット計算の基準を
        /// コンテナ側で縦横入れ替えて計算する（回転後の見た目がクライアント領域を正しく埋めるため）。</summary>
        public double DisplayRotation
        {
            get => _displayRotation;
            set { _displayRotation = value; RecomputeLayout(); }
        }

        /// <summary>
        /// 現在の ScaleMode / AspectMode / コンテナサイズ / 動画本来サイズ から、
        /// 黒帯（レターボックス・ピラーボックス）を一切出さずに埋めるための _image の
        /// Width/Height を計算して適用する。_root は ClipToBounds=true なので、
        /// 計算結果がコンテナよりはみ出た分は自動的にクロップされ、中央基準で表示される。
        /// </summary>
        private void RecomputeLayout()
        {
            if (NaturalVideoWidth <= 0 || NaturalVideoHeight <= 0) return;

            double containerW = ActualWidth, containerH = ActualHeight;
            if (containerW <= 0 || containerH <= 0) return;

            // 90°/270°回転時は、回転後の見た目がコンテナに一致するよう、
            // 回転前の計算では縦横を入れ替えたコンテナ寸法を基準にする
            bool swapped = Math.Abs(((_displayRotation % 360) + 360) % 360 - 90) < 0.01
                        || Math.Abs(((_displayRotation % 360) + 360) % 360 - 270) < 0.01;
            double effContainerW = swapped ? containerH : containerW;
            double effContainerH = swapped ? containerW : containerH;

            if (_scaleMode != VideoScaleMode.Auto)
            {
                // 固定倍率：常に動画本来のピクセルサイズが基準（回転はLayoutTransform側で処理されるため未考慮）
                double zoom = _scaleMode switch
                {
                    VideoScaleMode.Zoom1x => 1.0,
                    VideoScaleMode.Zoom2x => 2.0,
                    VideoScaleMode.Zoom2_5x => 2.5,
                    VideoScaleMode.Zoom3x => 3.0,
                    VideoScaleMode.Zoom4x => 4.0,
                    _ => 1.0
                };
                _image.Width = NaturalVideoWidth * zoom;
                _image.Height = NaturalVideoHeight * zoom;
                return;
            }

            if (_aspectMode == VideoAspectMode.Free)
            {
                // 縦横比を無視してクライアント領域に完全一致
                _image.Width = effContainerW;
                _image.Height = effContainerH;
                return;
            }

            // Auto + (Native or Force比率): アスペクト比を維持したまま収まる方に合わせる
            // （黒帯は出来る限り最小化されるが、クロップはしない＝コンテンツは常に全部見える）
            (double ratioW, double ratioH) = _aspectMode switch
            {
                VideoAspectMode.Force16x9 => (16.0, 9.0),
                VideoAspectMode.Force4x3 => (4.0, 3.0),
                VideoAspectMode.Force9x16 => (9.0, 16.0),
                _ => (NaturalVideoWidth, NaturalVideoHeight) // Native
            };

            double scale = Math.Min(effContainerW / ratioW, effContainerH / ratioH);
            _image.Width = ratioW * scale;
            _image.Height = ratioH * scale;

            Trace($"RecomputeLayout: container={containerW:F0}x{containerH:F0} eff={effContainerW:F0}x{effContainerH:F0} " +
                  $"rotation={_displayRotation} scaleMode={_scaleMode} aspectMode={_aspectMode} " +
                  $"ratio={ratioW:F0}:{ratioH:F0} -> image={_image.Width:F0}x{_image.Height:F0}");
        }

        private static void Trace(string msg)
        {
#if DEBUG
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} | [FfmpegMediaElement] {msg}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
#endif
        }

        private void OnEngineFailed(Exception ex)
        {
            RaiseEvent(new FfmpegMediaFailedEventArgs(MediaFailedEvent, this, ex));
        }

        public void Play()
        {
            _isPlaying = true;
            _audio.Play();
            _engine.Play();
            _engine.SetExternalClock(_audio.Position.TotalSeconds, true);
        }

        public void Pause()
        {
            _isPlaying = false;
            _audio.Pause();
            _engine.Pause();
            _engine.SetExternalClock(_audio.Position.TotalSeconds, false);
        }

        public void Stop()
        {
            _isPlaying = false;
            _audio.Stop();
            _engine.Stop();
            NaturalDuration = Duration.Automatic;
            NaturalVideoWidth = 0;
            NaturalVideoHeight = 0;
        }

        /// <summary>コマ送り/戻し専用。音声の再生（Play/Pause）には一切触れず、
        /// 映像デコードだけを指定位置へシークして1フレーム表示する。
        /// 音声再生を伴わないため、これまでの「シーク直後に音声が実時間で進み続け
        /// クロックの目標が逃げる」系の不具合を構造的に回避できる。
        /// Position（_audio.Position）はシークバー/時刻表示の整合のためだけに更新し、
        /// 実際の音の再生は行わない（Pause状態のまま位置だけ動かす）。</summary>
        public async Task<bool> StepToVideoOnlyAsync(TimeSpan target, int timeoutMs = 500)
        {
            var tcs = new TaskCompletionSource<bool>();
            double targetSec = target.TotalSeconds;
            void OnFrame(double pts)
            {
                if (pts >= targetSec - 0.06) tcs.TrySetResult(true);
            }
            _engine.FrameDisplayed += OnFrame;
            try
            {
                _audio.Position = target; // 音は出さず位置だけ合わせる
                _engine.SetExternalClock(targetSec, false);
                _engine.Seek(target);
                _engine.Play(); // 映像デコードのみ再開（_audio.Play()は呼ばない）
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return completed == tcs.Task;
            }
            finally
            {
                _engine.FrameDisplayed -= OnFrame;
                _engine.Pause();
            }
        }

        /// <summary>シークバードラッグ中の軽量プレビュー専用。目標フレームへの正確な追いつきは
        /// 行わず、直近のキーフレームへ即シークしてそのまま最初の1枚を表示する
        /// （低遅延優先、精度は犠牲）。ドラッグ終了時はStepToVideoOnlyAsyncで正確な1枚に
        /// 合わせ直すこと。StepToVideoOnlyAsyncと同様、音声のPlay/Pauseには一切触れない。</summary>
        public async Task FastSeekPreviewAsync(TimeSpan target, int timeoutMs = 300)
        {
            var tcs = new TaskCompletionSource<bool>();
            void OnFrame(double pts) => tcs.TrySetResult(true);
            _engine.FrameDisplayed += OnFrame;
            try
            {
                _audio.Position = target; // シークバー/時刻表示の整合のためだけ
                _engine.FastSeekPreview = true;
                _engine.SetExternalClock(target.TotalSeconds, false);
                _engine.Seek(target);
                _engine.Play();
                await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            }
            finally
            {
                _engine.FastSeekPreview = false;
                _engine.FrameDisplayed -= OnFrame;
                _engine.Pause();
            }
        }
    }
}
