using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// AccelChart右隣（幅220×高さ130固定）に常時表示するセンサー情報パネル。
    /// 日時・緯度経度は地図上部の専用パネル(DashcamPlayerView側のGeoDateTimeText等)へ
    /// 分離済みのため、ここでは速度・加速度3軸のみを保持・表示する。
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

            SpeedText = frame.SpeedKmh.ToString("F0", CultureInfo.InvariantCulture);
            AccelXText = frame.AccelX.ToString("F2", CultureInfo.InvariantCulture);
            AccelYText = frame.AccelY.ToString("F2", CultureInfo.InvariantCulture);
            AccelZText = frame.AccelZ.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
