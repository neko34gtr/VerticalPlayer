using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 加速度(前後/左右/上下)および速度の波形をファイル全体の固定スケールで表示するチャート。
    /// 従来は毎フレームAddSample()で1点ずつ追加し、そのたびに全点を再計算していたため
    /// スレッド負荷が無駄に高かった。ファイルを開いた時点で一度だけ全体を計算・描画し
    /// （SetFullTrack）、再生中はSetPlayhead()で現在位置を反映するだけにしている。
    /// 速度はGPSロスト区間(HasGpsFix=false)では計測値が無いため、0へ張り付けない。前後の測位速度から
    /// 補間した推定値(SpeedEstimated)がある区間は破線で描き（前後の実線とつなぐ）、推定値も無い区間は
    /// 線を途切れさせる（区間ごとに別々のPolylineとして描画する）。
    /// 左側にG軸(-3〜3)、右側に速度軸(0〜180km/h)の目盛り・グリッド線を表示する。
    ///
    /// 旧SeekSliderを廃止し、シーク操作は最上段の独立した帯(SeekBarCanvas)へ統合した
    /// （波形プロット下端に重ねる旧方式は0km/hのグリッド線と視覚的に被っていたため分離）。
    /// 見た目はボリュームスライダーと同じ「トラック＋塗り＋丸いツマミ」形式
    /// （SeekTrackBg/SeekTrackFill/SeekThumb）。トラックの左端〜右端はChartCanvasと同じ列
    /// （XAML上で列定義を共有）のため、グラフのプロット開始〜終了位置と常に完全に一致する。
    /// 波形プロット(ChartCanvas)自体はクリック/ドラッグを受け付けない見るだけの領域にした。
    /// 実際のシーク処理（FastSeekPreviewAsync/StepToVideoOnlyAsync呼び出し）はホスト側
    /// (DashcamPlayerView)が行うため、このクラスは「マウス位置→時刻」の変換とイベント
    /// 通知・見た目の描画だけを担当する。
    /// </summary>
    public partial class DashcamAccelChart : UserControl
    {
        private const double ScaleG = 3.0; // ±3Gでチャート上下いっぱいになるようにする（左軸目盛りと連動）
        private const double MaxSpeedKmh = 180.0; // 0〜180km/hでチャート下端〜上端いっぱいにする（右軸目盛りと連動）

        private TimeSpan _totalDuration = TimeSpan.FromMinutes(2);
        private IReadOnlyList<DashcamSensorFrame> _frames = Array.Empty<DashcamSensorFrame>();
        private readonly List<Polyline> _speedSegmentLines = new(); // GPSロスト区間で線を分けるため複数保持

        private bool _isSeekDragging;
        private double _lastRatio; // 直近の再生位置比率(0〜1)。SizeChanged時の再描画に使う

        // ── イベントマーカー（トンネル/SA-PA/Gセンサー急変点）。生成はホスト側がバックグラウンドで
        //    ファイルオープン時に1回だけ行い、ここでは受け取った集合を描画するだけ（再生中は何も計算しない） ──
        private IReadOnlyList<DashcamEventMarker> _markers = Array.Empty<DashcamEventMarker>();
        private readonly List<UIElement> _markerShapes = new();
        private const double MarkerSnapPx = 6.0;            // クリック時にマーカーへ吸着する距離(px)
        private const double GEventStrongThresholdG = 0.8;  // これ以上の急変は赤、未満はオレンジ

        /// <summary>シーク操作の開始（旧SeekSlider.Thumb.DragStartedに相当）。</summary>
        public event Action? SeekDragStarted;

        /// <summary>クリック直後・ドラッグ中に連続して呼ばれる、シーク先候補（旧SeekSlider.ValueChangedに相当）。</summary>
        public event Action<TimeSpan>? SeekPreview;

        /// <summary>マウスを離した時点の最終シーク先（旧SeekSlider.Thumb.DragCompletedに相当）。</summary>
        public event Action<TimeSpan>? SeekDragCompleted;

        /// <summary>
        /// ドラッグ中でない間のホバー時刻通知。時刻表示中はtimeが非null、
        /// マウスがSeekBarCanvas外に出た場合はtime=nullで呼ばれる（ポップアップを閉じる合図）。
        /// xはSeekBarCanvas内のX座標（ホスト側でのポップアップ位置決めに使う）。
        /// </summary>
        public event Action<TimeSpan?, double>? HoverTimeChanged;

        public DashcamAccelChart()
        {
            InitializeComponent();
        }

        /// <summary>
        /// ファイル全体分のセンサーフレームを渡し、チャート全体を一度だけ計算・描画する。
        /// ファイルを開いた直後に1回呼べば良い（再生中に毎フレーム呼ぶ必要はない）。
        /// </summary>
        public void SetFullTrack(IReadOnlyList<DashcamSensorFrame> frames, TimeSpan duration)
        {
            _frames = frames;
            _totalDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(2);
            SetPlayhead(TimeSpan.Zero);
            Redraw();
        }

        /// <summary>
        /// 現在の再生位置に合わせて、波形側の薄いガイド線と、独立したシークバー
        /// （トラック塗り＋丸ツマミ）の両方を動かす。軽量な処理なので毎フレーム呼んでよい。
        /// </summary>
        public void SetPlayhead(TimeSpan pos)
        {
            double totalSec = _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : 120.0;
            double ratio = Math.Clamp(pos.TotalSeconds / totalSec, 0.0, 1.0);
            _lastRatio = ratio;

            // 波形との対応を見るための薄い縦ガイド線
            double cw = ChartCanvas.ActualWidth, ch = ChartCanvas.ActualHeight;
            if (cw > 0 && ch > 0)
            {
                double gx = ratio * cw;
                Playhead.X1 = gx; Playhead.X2 = gx;
                Playhead.Y1 = 0; Playhead.Y2 = ch;
            }

            // 独立したシークバー本体（ボリュームスライダーと同じトラック＋塗り＋丸ツマミ）
            double sw = SeekBarCanvas.ActualWidth, sh = SeekBarCanvas.ActualHeight;
            if (sw <= 0 || sh <= 0) return;

            double x = ratio * sw;
            double trackY = sh / 2;
            SeekTrackBg.X1 = 0; SeekTrackBg.X2 = sw; SeekTrackBg.Y1 = trackY; SeekTrackBg.Y2 = trackY;
            SeekTrackFill.X1 = 0; SeekTrackFill.X2 = x; SeekTrackFill.Y1 = trackY; SeekTrackFill.Y2 = trackY;
            Canvas.SetLeft(SeekThumb, x - SeekThumb.Width / 2);
            Canvas.SetTop(SeekThumb, trackY - SeekThumb.Height / 2);
        }

        /// <summary>
        /// シークバー上に描画するイベントマーカーの集合を差し替える（UIスレッドから呼ぶ）。
        /// トンネル=シアンの帯、SA/PA=緑の逆三角、Gセンサー=オレンジ(強いものは赤)の縦線。
        /// 空/nullで全消去。
        /// </summary>
        public void SetEventMarkers(IReadOnlyList<DashcamEventMarker>? markers)
        {
            _markers = markers ?? Array.Empty<DashcamEventMarker>();
            RedrawMarkers();
        }

        /// <summary>
        /// SeekBarCanvas内のX座標に重なるイベントのラベルを返す（無ければnull）。ホバー時刻ポップアップへ
        /// 並べて出す用。点イベント(SA/PA・G)は±MarkerSnapPx以内で最寄りの1件、区間イベント(トンネル)は
        /// 帯の上にあれば1件。両方該当すれば " / " でつなぐ。
        /// </summary>
        public string? FindMarkerLabelAt(double x)
        {
            double sw = SeekBarCanvas.ActualWidth;
            if (sw <= 0 || _markers.Count == 0) return null;
            double total = TotalSeconds();

            var labels = new List<string>(2);
            DashcamEventMarker? point = null;
            double best = MarkerSnapPx;
            foreach (var m in _markers)
            {
                if (m.IsRange) continue;
                double d = Math.Abs(m.VideoOffsetSeconds / total * sw - x);
                if (d <= best) { best = d; point = m; }
            }
            if (point != null) labels.Add(point.Label);

            foreach (var m in _markers)
            {
                if (!m.IsRange) continue;
                double x0 = m.VideoOffsetSeconds / total * sw;
                double x1 = m.EndOffsetSeconds / total * sw;
                if (x >= x0 - 2 && x <= Math.Max(x1, x0 + 2) + 2) { labels.Add(m.Label); break; }
            }
            return labels.Count == 0 ? null : string.Join(" / ", labels);
        }

        /// <summary>
        /// クリック位置の近く(±MarkerSnapPx)にマーカーがあれば、その再生位置へ吸着させる
        /// （点イベントはその地点、区間イベントは開始端）。無ければ元の位置を返す。
        /// </summary>
        private TimeSpan SnapToMarker(double x, TimeSpan pos)
        {
            double sw = SeekBarCanvas.ActualWidth;
            if (sw <= 0 || _markers.Count == 0) return pos;
            double total = TotalSeconds();

            double best = MarkerSnapPx;
            double? snapSec = null;
            foreach (var m in _markers)
            {
                double d = Math.Abs(m.VideoOffsetSeconds / total * sw - x);
                if (d <= best) { best = d; snapSec = m.VideoOffsetSeconds; }
            }
            return snapSec.HasValue ? TimeSpan.FromSeconds(snapSec.Value) : pos;
        }

        private double TotalSeconds() => _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : 120.0;

        private void RedrawMarkers()
        {
            foreach (var e in _markerShapes)
                MarkerLayer.Children.Remove(e);
            _markerShapes.Clear();

            double sw = SeekBarCanvas.ActualWidth, sh = SeekBarCanvas.ActualHeight;
            if (sw <= 0 || sh <= 0 || _markers.Count == 0) return;
            double total = TotalSeconds();
            double trackY = sh / 2;

            // 描画順: トンネル帯(奥) → SA/PA → Gセンサー(手前)
            foreach (var m in _markers.Where(m => m.EventType == DashcamEventType.Tunnel))
            {
                double x0 = Math.Clamp(m.VideoOffsetSeconds / total, 0, 1) * sw;
                double x1 = Math.Clamp(m.EndOffsetSeconds / total, 0, 1) * sw;
                var band = new Rectangle
                {
                    Width = Math.Max(2.0, x1 - x0),
                    Height = 10,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x22, 0xD3, 0xEE)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(band, x0);
                Canvas.SetTop(band, trackY - 5);
                AddMarkerShape(band);
            }

            foreach (var m in _markers.Where(m => m.EventType == DashcamEventType.SaPa))
            {
                double x = Math.Clamp(m.VideoOffsetSeconds / total, 0, 1) * sw;
                var tri = new Polygon
                {
                    Points = new PointCollection { new Point(x - 4.5, 1), new Point(x + 4.5, 1), new Point(x, 8.5) },
                    Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x1E)),
                    StrokeThickness = 0.8,
                    IsHitTestVisible = false
                };
                AddMarkerShape(tri);
            }

            foreach (var m in _markers.Where(m => m.EventType == DashcamEventType.GSensor))
            {
                double x = Math.Clamp(m.VideoOffsetSeconds / total, 0, 1) * sw;
                bool strong = m.Magnitude >= GEventStrongThresholdG;
                var line = new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = 2,
                    Y2 = Math.Max(4, sh - 2),
                    Stroke = new SolidColorBrush(strong ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0xF9, 0x73, 0x16)),
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                AddMarkerShape(line);
            }
        }

        private void AddMarkerShape(UIElement shape)
        {
            MarkerLayer.Children.Add(shape);
            _markerShapes.Add(shape);
        }

        /// <summary>ファイル切り替え時にチャートを空にする。</summary>
        public void Clear()
        {
            SetEventMarkers(null);
            _frames = Array.Empty<DashcamSensorFrame>();
            LineX.Points.Clear();
            LineY.Points.Clear();
            LineZ.Points.Clear();
            ClearSpeedSegments();
        }

        private void ClearSpeedSegments()
        {
            foreach (var line in _speedSegmentLines)
                ChartCanvas.Children.Remove(line);
            _speedSegmentLines.Clear();
        }

        private void DashcamAccelChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Redraw();
            RedrawMarkers();
            SetPlayhead(TimeSpan.FromSeconds(_totalDuration.TotalSeconds * _lastRatio));
        }

        // ---- シーク操作（SeekBarCanvas専用。波形プロット(ChartCanvas)はクリックを受け付けない） ----

        private TimeSpan PositionFromX(double x, double width)
        {
            double totalSec = _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : 120.0;
            double ratio = width > 0 ? Math.Clamp(x / width, 0.0, 1.0) : 0.0;
            return TimeSpan.FromSeconds(ratio * totalSec);
        }

        private void SeekBarCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_totalDuration <= TimeSpan.Zero) return; // ファイル未読込時は無視
            _isSeekDragging = true;
            SeekBarCanvas.CaptureMouse();

            double downX = e.GetPosition(SeekBarCanvas).X;
            var pos = SnapToMarker(downX, PositionFromX(downX, SeekBarCanvas.ActualWidth)); // マーカー付近のクリックはその位置へジャンプ
            SetPlayhead(pos); // 押した瞬間から見た目のシークバーも動かす（クリック即シークの体感を合わせる）
            SeekDragStarted?.Invoke();
            SeekPreview?.Invoke(pos);
            e.Handled = true;
        }

        private void SeekBarCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            double x = e.GetPosition(SeekBarCanvas).X;
            var pos = PositionFromX(x, SeekBarCanvas.ActualWidth);

            if (_isSeekDragging)
            {
                SetPlayhead(pos);
                SeekPreview?.Invoke(pos);
            }
            else if (_totalDuration > TimeSpan.Zero)
            {
                HoverTimeChanged?.Invoke(pos, x);
            }
        }

        private void SeekBarCanvas_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isSeekDragging) return;
            _isSeekDragging = false;
            SeekBarCanvas.ReleaseMouseCapture();

            var pos = PositionFromX(e.GetPosition(SeekBarCanvas).X, SeekBarCanvas.ActualWidth);
            SetPlayhead(pos);
            SeekDragCompleted?.Invoke(pos);
            e.Handled = true;
        }

        private void SeekBarCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isSeekDragging)
                HoverTimeChanged?.Invoke(null, 0);
        }

        private void Redraw()
        {
            double w = ChartCanvas.ActualWidth;
            double h = ChartCanvas.ActualHeight;
            ClearSpeedSegments();

            // 目盛り・グリッド線はフレームが無くても常に最新のCanvasサイズへ追従させる
            UpdateAxisLayout(w, h);

            if (w <= 0 || h <= 0 || _frames.Count == 0)
            {
                LineX.Points.Clear();
                LineY.Points.Clear();
                LineZ.Points.Clear();
                return;
            }

            double midY = h / 2;
            double totalSec = _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : 120.0;
            var ordered = _frames.OrderBy(f => f.VideoOffset).ToList();

            var px = new PointCollection();
            var py = new PointCollection();
            var pz = new PointCollection();

            List<Point>? currentSpeedSegment = null; // 測位あり(実線)
            List<Point>? estimatedSegment = null;    // 測位ロスト中の推定速度(破線)

            foreach (var f in ordered)
            {
                double ratio = Math.Clamp(f.VideoOffset.TotalSeconds / totalSec, 0.0, 1.0);
                double cx = ratio * w;

                // 加速度は測位が無くてもIMU単体で常時サンプリングされているため常に描画する
                px.Add(new Point(cx, midY - ClampG(f.AccelX) / ScaleG * midY));
                py.Add(new Point(cx, midY - ClampG(f.AccelY) / ScaleG * midY));
                pz.Add(new Point(cx, midY - ClampG(f.AccelZ) / ScaleG * midY));

                // 速度はGPSロスト中(HasGpsFix=false)は値が信頼できない（0に張り付くだけ）ため、
                // 0を描画せず線を途切れさせる。ロスト区間ごとに新しいPolylineへ切り替える。
                if (f.HasGpsFix)
                {
                    var p = new Point(cx, SpeedToY(Math.Clamp(f.SpeedKmh, 0.0, MaxSpeedKmh), h));
                    if (estimatedSegment != null)
                    {
                        // 推定区間(破線)の終点を、復帰後の最初の測位点へつないでから確定する
                        estimatedSegment.Add(p);
                        FlushSpeedSegment(estimatedSegment, dashed: true);
                        estimatedSegment = null;
                    }
                    currentSpeedSegment ??= new List<Point>();
                    currentSpeedSegment.Add(p);
                }
                else if (f.SpeedEstimated)
                {
                    var p = new Point(cx, SpeedToY(Math.Clamp(f.SpeedKmh, 0.0, MaxSpeedKmh), h));
                    if (estimatedSegment == null)
                    {
                        // 実線の最終点を破線の起点にして、線が途切れて見えないようにする
                        estimatedSegment = new List<Point>();
                        if (currentSpeedSegment != null && currentSpeedSegment.Count > 0)
                            estimatedSegment.Add(currentSpeedSegment[^1]);
                    }
                    if (currentSpeedSegment != null)
                    {
                        FlushSpeedSegment(currentSpeedSegment, dashed: false);
                        currentSpeedSegment = null;
                    }
                    estimatedSegment.Add(p);
                }
                else
                {
                    if (currentSpeedSegment != null)
                    {
                        FlushSpeedSegment(currentSpeedSegment, dashed: false);
                        currentSpeedSegment = null;
                    }
                    if (estimatedSegment != null)
                    {
                        FlushSpeedSegment(estimatedSegment, dashed: true);
                        estimatedSegment = null;
                    }
                }
            }
            if (currentSpeedSegment != null)
                FlushSpeedSegment(currentSpeedSegment, dashed: false);
            if (estimatedSegment != null)
                FlushSpeedSegment(estimatedSegment, dashed: true);

            LineX.Points = px;
            LineY.Points = py;
            LineZ.Points = pz;
        }

        /// <summary>
        /// 左軸(G)・右軸(km/h)のグリッド線とラベル位置をCanvasサイズに合わせて更新する。
        /// G軸は-3〜+3を7段階、速度軸は0〜180km/hを7段階で均等割りする（各定数と連動）。
        /// </summary>
        private void UpdateAxisLayout(double w, double h)
        {
            if (h <= 0) return;
            double midY = h / 2;

            SetGridLine(GridPlus3, GToY(3, midY), w);
            SetGridLine(GridPlus2, GToY(2, midY), w);
            SetGridLine(GridPlus1, GToY(1, midY), w);
            SetGridLine(ZeroLine, midY, w);
            SetGridLine(GridMinus1, GToY(-1, midY), w);
            SetGridLine(GridMinus2, GToY(-2, midY), w);
            SetGridLine(GridMinus3, GToY(-3, midY), w);

            PlaceLabel(GLabel3, GToY(3, midY));
            PlaceLabel(GLabel2, GToY(2, midY));
            PlaceLabel(GLabel1, GToY(1, midY));
            PlaceLabel(GLabel0, GToY(0, midY));
            PlaceLabel(GLabelM1, GToY(-1, midY));
            PlaceLabel(GLabelM2, GToY(-2, midY));
            PlaceLabel(GLabelM3, GToY(-3, midY));

            PlaceLabel(SpeedLabel180, SpeedToY(180, h));
            PlaceLabel(SpeedLabel150, SpeedToY(150, h));
            PlaceLabel(SpeedLabel120, SpeedToY(120, h));
            PlaceLabel(SpeedLabel90, SpeedToY(90, h));
            PlaceLabel(SpeedLabel60, SpeedToY(60, h));
            PlaceLabel(SpeedLabel30, SpeedToY(30, h));
            PlaceLabel(SpeedLabel0, SpeedToY(0, h));
        }

        private static void SetGridLine(Line line, double y, double w)
        {
            line.X1 = 0; line.X2 = w;
            line.Y1 = y; line.Y2 = y;
        }

        private static void PlaceLabel(TextBlock label, double y)
        {
            // テキストの縦中央がグリッド線位置に来るよう、フォントサイズ相当分だけ上へオフセットする
            Canvas.SetTop(label, Math.Max(0, y - 6));
        }

        private static double GToY(double g, double midY) => midY - (g / ScaleG) * midY;

        private static double SpeedToY(double speed, double h) => h - (speed / MaxSpeedKmh * h);

        private void FlushSpeedSegment(List<Point> points, bool dashed)
        {
            if (points.Count < 2) return; // 1点だけの区間は線として描けないので無視する

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                StrokeThickness = 1.5,
                Opacity = 0.9,
                Points = new PointCollection(points)
            };
            if (dashed) // 測位ロスト中の推定速度は破線で表す（実測の実線と区別）
                line.StrokeDashArray = new DoubleCollection { 3, 2 };
            ChartCanvas.Children.Add(line);
            _speedSegmentLines.Add(line);
        }

        private static double ClampG(double g) => Math.Max(-ScaleG, Math.Min(ScaleG, g));
    }
}
