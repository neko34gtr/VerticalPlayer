using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// AccelChart右隣（幅220×高さ130固定）に常時表示するセンサー情報パネル。
    /// 日時・緯度経度は地図上部の専用パネル(DashcamPlayerView側のGeoDateTimeText等)へ
    /// 分離済みのため、ここでは速度・加速度3軸のみを保持・表示する。
    /// 測位ロスト中(トンネル等)の速度は、前後の測位速度から補間した推定値(SpeedEstimated)を
    /// 「≈」付き・琥珀色・ラベル「推定」で表示し、実測と区別する。推定値が無い場合は「--」。
    /// </summary>
    public partial class DashcamHudOverlay : UserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public DashcamHudOverlay()
        {
            InitializeComponent();
            DataContext = this;
        }

        private string _speedText = "0";
        public string SpeedText
        {
            get => _speedText;
            private set { _speedText = value; Raise(); }
        }

        private static readonly Brush MeasuredBrush = Freeze(Colors.White);
        private static readonly Brush EstimatedBrush = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));

        private static Brush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private Brush _speedBrush = MeasuredBrush;
        public Brush SpeedBrush
        {
            get => _speedBrush;
            private set { _speedBrush = value; Raise(); }
        }

        private string _speedLabelText = "速度 (km/h)";
        public string SpeedLabelText
        {
            get => _speedLabelText;
            private set { _speedLabelText = value; Raise(); }
        }

        private string _accelXText = "0.00";
        public string AccelXText { get => _accelXText; private set { _accelXText = value; Raise(); } }

        private string _accelYText = "0.00";
        public string AccelYText { get => _accelYText; private set { _accelYText = value; Raise(); } }

        private string _accelZText = "0.00";
        public string AccelZText { get => _accelZText; private set { _accelZText = value; Raise(); } }

        /// <summary>現在の再生位置に対応するセンサーフレームでHUD表示を更新する（速度・加速度3軸のみ）。</summary>
        public void UpdateFrame(DashcamSensorFrame? frame)
        {
            if (frame is null)
            {
                SpeedText = "0";
                AccelXText = AccelYText = AccelZText = "0.00";
                return;
            }

            bool estimated = !frame.HasGpsFix && frame.SpeedEstimated;
            SpeedText = frame.HasGpsFix ? frame.SpeedKmh.ToString("F0", CultureInfo.InvariantCulture)
                : estimated ? "≈" + frame.SpeedKmh.ToString("F0", CultureInfo.InvariantCulture)
                : "--";
            SpeedBrush = estimated ? EstimatedBrush : MeasuredBrush;
            SpeedLabelText = estimated ? "推定 (km/h)" : "速度 (km/h)"; // HUD幅が狭いためラベル長は通常時と同程度に収める
            AccelXText = frame.AccelX.ToString("F2", CultureInfo.InvariantCulture);
            AccelYText = frame.AccelY.ToString("F2", CultureInfo.InvariantCulture);
            AccelZText = frame.AccelZ.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
