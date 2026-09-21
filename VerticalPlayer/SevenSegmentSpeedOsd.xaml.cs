using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VerticalPlayer.Controls
{
    /// <summary>
    /// 7セグメントLED風の車速OSD(3桁＋単位)。Viewbox内に配置しているため、
    /// 親が Height または Width を与えるだけでベクターのまま滑らかに拡縮する。
    /// Speed が変わっても桁の値が変わった時だけ該当桁のFillを更新する。
    /// </summary>
    public partial class SevenSegmentSpeedOsd : UserControl
    {
        private static readonly Brush DefaultEstimatedOnBrush = CreateFrozenBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static readonly Brush DefaultEstimatedOffBrush = CreateFrozenBrush(Color.FromArgb(0x0C, 0xA0, 0xA0, 0xA0));

        public static readonly DependencyProperty SpeedProperty =
            DependencyProperty.Register(
                nameof(Speed), typeof(int?), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(null, OnSpeedChanged));

        public static readonly DependencyProperty UnitTextProperty =
            DependencyProperty.Register(
                nameof(UnitText), typeof(string), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata("km/h", OnUnitTextChanged));

        public static readonly DependencyProperty IsEstimatedProperty =
            DependencyProperty.Register(
                nameof(IsEstimated), typeof(bool), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(false, OnBrushStateChanged));

        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register(
                nameof(OnBrush), typeof(Brush), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(SevenSegmentDigit.DefaultOnBrush, OnBrushStateChanged));

        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register(
                nameof(OffBrush), typeof(Brush), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(SevenSegmentDigit.DefaultOffBrush, OnBrushStateChanged));

        public static readonly DependencyProperty EstimatedOnBrushProperty =
            DependencyProperty.Register(
                nameof(EstimatedOnBrush), typeof(Brush), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(DefaultEstimatedOnBrush, OnBrushStateChanged));

        public static readonly DependencyProperty EstimatedOffBrushProperty =
            DependencyProperty.Register(
                nameof(EstimatedOffBrush), typeof(Brush), typeof(SevenSegmentSpeedOsd),
                new PropertyMetadata(DefaultEstimatedOffBrush, OnBrushStateChanged));

        // 現在各桁に設定済みの値(-1 = 消灯)。同じ値なら何もしない
        private int _hundreds = -1;
        private int _tens = -1;
        private int _ones = -1;
        private readonly bool _ready;

        public SevenSegmentSpeedOsd()
        {
            InitializeComponent();
            _ready = true;
            UnitBlock.Text = UnitText;
            ApplyBrushes();
        }

        /// <summary>表示する車速。null・負数は全桁消灯、999超は999。</summary>
        public int? Speed
        {
            get => (int?)GetValue(SpeedProperty);
            set
            {
                if (Speed != value)
                {
                    SetValue(SpeedProperty, value);
                }
            }
        }

        /// <summary>単位表示("km/h" / "mph")。</summary>
        public string UnitText
        {
            get => (string)GetValue(UnitTextProperty);
            set => SetValue(UnitTextProperty, value);
        }

        /// <summary>測位ロスト中の推定速度か。trueで琥珀色＋「≈」表示。</summary>
        public bool IsEstimated
        {
            get => (bool)GetValue(IsEstimatedProperty);
            set
            {
                if (IsEstimated != value)
                {
                    SetValue(IsEstimatedProperty, value);
                }
            }
        }

        public Brush OnBrush
        {
            get => (Brush)GetValue(OnBrushProperty);
            set => SetValue(OnBrushProperty, value);
        }

        public Brush OffBrush
        {
            get => (Brush)GetValue(OffBrushProperty);
            set => SetValue(OffBrushProperty, value);
        }

        public Brush EstimatedOnBrush
        {
            get => (Brush)GetValue(EstimatedOnBrushProperty);
            set => SetValue(EstimatedOnBrushProperty, value);
        }

        public Brush EstimatedOffBrush
        {
            get => (Brush)GetValue(EstimatedOffBrushProperty);
            set => SetValue(EstimatedOffBrushProperty, value);
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (SevenSegmentSpeedOsd)d;
            if (self._ready)
            {
                self.UpdateDigits();
            }
        }

        private static void OnUnitTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (SevenSegmentSpeedOsd)d;
            if (self._ready)
            {
                self.UnitBlock.Text = e.NewValue as string ?? string.Empty;
            }
        }

        private static void OnBrushStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (SevenSegmentSpeedOsd)d;
            if (self._ready)
            {
                self.ApplyBrushes();
            }
        }

        // 桁分散: 100の桁/10の桁は先頭ゼロを消灯(null)。0は1の桁のみ「0」を点灯
        private void UpdateDigits()
        {
            int hundreds = -1;
            int tens = -1;
            int ones = -1;

            int? speed = Speed;
            if (speed.HasValue && speed.Value >= 0)
            {
                int v = speed.Value > 999 ? 999 : speed.Value;
                ones = v % 10;
                if (v >= 10)
                {
                    tens = (v / 10) % 10;
                }
                if (v >= 100)
                {
                    hundreds = v / 100;
                }
            }

            if (hundreds != _hundreds)
            {
                _hundreds = hundreds;
                Digit100.Value = hundreds < 0 ? (int?)null : hundreds;
            }
            if (tens != _tens)
            {
                _tens = tens;
                Digit10.Value = tens < 0 ? (int?)null : tens;
            }
            if (ones != _ones)
            {
                _ones = ones;
                Digit1.Value = ones < 0 ? (int?)null : ones;
            }
        }

        private void ApplyBrushes()
        {
            bool estimated = IsEstimated;
            Brush on = (estimated ? EstimatedOnBrush : OnBrush) ?? SevenSegmentDigit.DefaultOnBrush;
            Brush off = (estimated ? EstimatedOffBrush : OffBrush) ?? SevenSegmentDigit.DefaultOffBrush;

            Digit100.OnBrush = on;
            Digit100.OffBrush = off;
            Digit10.OnBrush = on;
            Digit10.OffBrush = off;
            Digit1.OnBrush = on;
            Digit1.OffBrush = off;

            UnitBlock.Foreground = on;
            ApproxMark.Foreground = on;
            ApproxMark.Opacity = estimated ? 1.0 : 0.0;
        }
    }
}
