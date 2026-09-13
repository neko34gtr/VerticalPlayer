using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 加速度(前後/左右/上下)の直近数秒間を0G基準線付きの折れ線でスクロール表示する
    /// 簡易ストリップチャート。速度はスケール(軸)が異なるため意図的に含めていない。
    /// </summary>
    public partial class DashcamAccelChart : UserControl
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
        private const double ScaleG = 1.5; // ±1.5Gでチャート上下いっぱいになるようにする

        private readonly List<(TimeSpan t, double x, double y, double z)> _samples = new();

        public DashcamAccelChart()
        {
            InitializeComponent();
        }

        /// <summary>現在の再生位置(t)における3軸Gを追加する。tより古い(t - Window以前の)サンプルは間引く。</summary>
        public void AddSample(TimeSpan t, double x, double y, double z)
        {
            _samples.Add((t, x, y, z));
            while (_samples.Count > 0 && t - _samples[0].t > Window)
                _samples.RemoveAt(0);

            Redraw();
        }

        /// <summary>シーク等で連続性が失われた際にバッファを空にする。</summary>
        public void Clear()
        {
            _samples.Clear();
            LineX.Points.Clear();
            LineY.Points.Clear();
            LineZ.Points.Clear();
        }

        private void DashcamAccelChart_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

        private void Redraw()
        {
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            if (w <= 0 || h <= 0 || _samples.Count == 0) return;

            double midY = h / 2;
            ZeroLine.X1 = 0; ZeroLine.X2 = w; ZeroLine.Y1 = midY; ZeroLine.Y2 = midY;

            TimeSpan latest = _samples[^1].t;
            TimeSpan earliest = latest - Window;

            var px = new System.Windows.Media.PointCollection();
            var py = new System.Windows.Media.PointCollection();
            var pz = new System.Windows.Media.PointCollection();

            foreach (var s in _samples)
            {
                double ratio = (s.t - earliest).TotalSeconds / Window.TotalSeconds; // 0(左端)〜1(右端)
                double cx = ratio * w;

                px.Add(new Point(cx, midY - Clamp(s.x) / ScaleG * midY));
                py.Add(new Point(cx, midY - Clamp(s.y) / ScaleG * midY));
                pz.Add(new Point(cx, midY - Clamp(s.z) / ScaleG * midY));
            }

            LineX.Points = px;
            LineY.Points = py;
            LineZ.Points = pz;
        }

        private static double Clamp(double g) => Math.Max(-ScaleG, Math.Min(ScaleG, g));
    }
}
