using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VerticalPlayer.Media
{
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
        private readonly Grid _root = new();
        private readonly Image _image = new() { Stretch = Stretch.Uniform };
        private readonly MediaElement _audio = new()
        {
            LoadedBehavior = MediaState.Manual,
            Visibility = Visibility.Collapsed,
            Width = 0,
            Height = 0
        };
        private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        private readonly AVEngine _engine;
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

            _clockTimer.Tick += (s, e) =>
            {
                _engine.SetExternalClock(_audio.Position.TotalSeconds, _isPlaying);
            };
            _clockTimer.Start();
        }

        private void OnEngineOpened(int w, int h, TimeSpan duration)
        {
            NaturalVideoWidth = w;
            NaturalVideoHeight = h;
            NaturalDuration = new Duration(duration);
            _image.Source = _engine.Bitmap;
            RaiseEvent(new RoutedEventArgs(MediaOpenedEvent, this));
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
    }
}
