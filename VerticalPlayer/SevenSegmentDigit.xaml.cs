using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace VerticalPlayer.Controls
{
    /// <summary>
    /// 7セグメントLED風の1桁表示。Value(0〜9)に応じて各セグメントのFillを
    /// OnBrush / OffBrush で切り替える。変化したセグメントのFillだけを更新するため、
    /// レイアウトの再計算は発生せず再描画のみで済む。
    /// </summary>
    public partial class SevenSegmentDigit : UserControl
    {
        // bit0=A, bit1=B, bit2=C, bit3=D, bit4=E, bit5=F, bit6=G
        private static readonly byte[] DigitMasks =
        {
            0x3F, // 0 : A B C D E F
            0x06, // 1 : B C
            0x5B, // 2 : A B D E G
            0x4F, // 3 : A B C D G
            0x66, // 4 : B C F G
            0x6D, // 5 : A C D F G
            0x7D, // 6 : A C D E F G
            0x07, // 7 : A B C
            0x7F, // 8 : 全点灯
            0x6F  // 9 : A B C D F G
        };

        internal static readonly Brush DefaultOnBrush = CreateFrozenBrush(Color.FromRgb(0x00, 0xFF, 0x00));
        internal static readonly Brush DefaultOffBrush = CreateFrozenBrush(Color.FromArgb(0x0C, 0xA0, 0xA0, 0xA0));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value), typeof(int?), typeof(SevenSegmentDigit),
                new PropertyMetadata(null, OnValueChanged));

        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register(
                nameof(OnBrush), typeof(Brush), typeof(SevenSegmentDigit),
                new PropertyMetadata(DefaultOnBrush, OnBrushChanged));

        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register(
                nameof(OffBrush), typeof(Brush), typeof(SevenSegmentDigit),
                new PropertyMetadata(DefaultOffBrush, OnBrushChanged));

        private readonly ShapePath[] _segments;
        private Brush _on = DefaultOnBrush;
        private Brush _off = DefaultOffBrush;
        private int _litMask;

        public SevenSegmentDigit()
        {
            InitializeComponent();
            _segments = new[] { SegA, SegB, SegC, SegD, SegE, SegF, SegG };
            ApplyAll();
        }

        /// <summary>0〜9で該当数字を点灯。null・範囲外は全セグメント消灯。</summary>
        public int? Value
        {
            get => (int?)GetValue(ValueProperty);
            set
            {
                if (Value != value)
                {
                    SetValue(ValueProperty, value);
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

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (SevenSegmentDigit)d;
            if (self._segments == null)
            {
                return;
            }

            int newMask = 0;
            if (e.NewValue is int v && v >= 0 && v <= 9)
            {
                newMask = DigitMasks[v];
            }

            self.UpdateSegments(newMask);
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var self = (SevenSegmentDigit)d;
            self._on = self.OnBrush ?? DefaultOnBrush;
            self._off = self.OffBrush ?? DefaultOffBrush;
            if (self._segments != null)
            {
                self.ApplyAll();
            }
        }

        // 点灯状態が変わったセグメントだけFillを差し替える
        private void UpdateSegments(int newMask)
        {
            int diff = _litMask ^ newMask;
            if (diff == 0)
            {
                return;
            }

            for (int i = 0; i < 7; i++)
            {
                if (((diff >> i) & 1) != 0)
                {
                    _segments[i].Fill = ((newMask >> i) & 1) != 0 ? _on : _off;
                }
            }

            _litMask = newMask;
        }

        // ブラシ変更時は全セグメントを現在の点灯状態で塗り直す
        private void ApplyAll()
        {
            for (int i = 0; i < 7; i++)
            {
                _segments[i].Fill = ((_litMask >> i) & 1) != 0 ? _on : _off;
            }
        }
    }
}
