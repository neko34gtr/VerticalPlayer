using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
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

        private string _timestampText = "----/--/-- --:--:--";
        public string TimestampText
        {
            get => _timestampText;
            private set { _timestampText = value; Raise(); }
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

        /// <summary>現在の再生位置に対応するセンサーフレームでHUD表示を更新する。</summary>
        public void UpdateFrame(DashcamSensorFrame? frame)
        {
            if (frame is null)
            {
                TimestampText = "----/--/-- --:--:--";
                SpeedText = "0";
                AccelXText = AccelYText = AccelZText = "0.00";
                return;
            }

            TimestampText = frame.Timestamp.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
            SpeedText = frame.SpeedKmh.ToString("F0", CultureInfo.InvariantCulture);
            AccelXText = frame.AccelX.ToString("F2", CultureInfo.InvariantCulture);
            AccelYText = frame.AccelY.ToString("F2", CultureInfo.InvariantCulture);
            AccelZText = frame.AccelZ.ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
