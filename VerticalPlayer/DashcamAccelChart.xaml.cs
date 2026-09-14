using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 加速度(前後/左右/上下)および速度の波形をファイル全体（約2分間）の固定スケールで表示するチャート。
    /// </summary>
    public partial class DashcamAccelChart : UserControl
    {
        private const double ScaleG = 1.5; // ±1.5Gでチャート上下いっぱいになるようにする
        private const double MaxSpeedKmh = 195.0; // 0〜195km/hでチャート下端〜上端いっぱいにする

        private TimeSpan _totalDuration = TimeSpan.FromMinutes(2); // ファイル全体の総再生時間（既定値2分）

        private readonly List<(TimeSpan t, double x, double y, double z, double speed)> _samples = new();

        public DashcamAccelChart()
        {
            InitializeComponent();
        }

        /// <summary>動画全体の総再生時間を設定し、表示範囲を更新する。</summary>
        public void SetTotalDuration(TimeSpan duration)
        {
            _totalDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(2);
            Redraw();
        }

        /// <summary>現在の再生位置(t)における3軸Gおよび速度(km/h)データをつぎ足す。</summary>
        public void AddSample(TimeSpan t, double x, double y, double z, double speedKmh)
        {
            _samples.Add((t, x, y, z, speedKmh));
            Redraw();
        }

        /// <summary>現在の再生位置(t)における3軸Gを追加する（従来互換用：速度0扱い）。</summary>
        public void AddSample(TimeSpan t, double x, double y, double z)
        {
            AddSample(t, x, y, z, 0.0);
        }

        /// <summary>シーク等で連続性が失われた際、またはファイル切り替え時にバッファを空にする。</summary>
        public void Clear()
        {
            _samples.Clear();
            LineX.Points.Clear();
            LineY.Points.Clear();
            LineZ.Points.Clear();
            if (LineSpeed != null)
            {
                LineSpeed.Points.Clear();
            }
        }

        private void DashcamAccelChart_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || _samples.Count == 0) return;

            double midY = h / 2;
            ZeroLine.X1 = 0; ZeroLine.X2 = w; ZeroLine.Y1 = midY; ZeroLine.Y2 = midY;

            double totalSec = _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : 120.0;

            var px = new System.Windows.Media.PointCollection();
            var py = new System.Windows.Media.PointCollection();
            var pz = new System.Windows.Media.PointCollection();
            var pSpeed = new System.Windows.Media.PointCollection();

            foreach (var s in _samples)
            {
                // 0秒〜TotalDuration間での固定位置(ratio)を算出
                double ratio = Math.Clamp(s.t.TotalSeconds / totalSec, 0.0, 1.0);
                double cx = ratio * w;

                // 加速度描画 (±1.5G)
                px.Add(new Point(cx, midY - Clamp(s.x) / ScaleG * midY));
                py.Add(new Point(cx, midY - Clamp(s.y) / ScaleG * midY));
                pz.Add(new Point(cx, midY - Clamp(s.z) / ScaleG * midY));

                // 速度描画 (0〜200km/h : 下端h〜上端0)
                double clampedSpeed = Math.Clamp(s.speed, 0.0, MaxSpeedKmh);
                double speedY = h - (clampedSpeed / MaxSpeedKmh * h);
                pSpeed.Add(new Point(cx, speedY));
            }

            LineX.Points = px;
            LineY.Points = py;
            LineZ.Points = pz;

            if (LineSpeed != null)
            {
                LineSpeed.Points = pSpeed;
            }
        }

        private static double Clamp(double g) => Math.Max(-ScaleG, Math.Min(ScaleG, g));
    }
}