using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// ファイル名の時刻から算出したFront/Rearの対応関係を表示する確認用ウィンドウ（表示専用）。
    /// FrontとRearは録画開始の位相がずれており（リアの再起動等で数秒〜100秒超）、1対1にはならないため、
    /// 「Frontに対して重なるRear」「Rearに対して重なるFront」を重なる区間ごとに1行で表示する（n対n）。
    /// 再生側(DashcamPlayerView)も同じ時刻情報で絶対時刻同期している。
    /// メインウィンドウと同じ自前タイトルバー（WindowStyle="None"、ドラッグ移動・最小化・
    /// 閉じるのみ）にしてアプリ全体の見た目と統一している。
    /// モーダルではなく別ウィンドウ（Show）として開く。開いたままメイン画面の再生・操作ができ、
    /// メイン画面が無効化されて暗く見えることもない。
    /// </summary>
    public partial class DashcamPairListWindow : Window
    {
        public enum RowKind
        {
            /// <summary>FrontとRearが重なっている区間。</summary>
            Overlap,
            /// <summary>Frontのうち、Rearの録画が無い区間。</summary>
            FrontOnly,
            /// <summary>Rearのうち、Frontの録画が無い区間。</summary>
            RearOnly,
        }

        public sealed class PairOverlapRow
        {
            public int No { get; set; }
            public RowKind Kind { get; init; }
            public string FrontFileName { get; init; } = "(なし)";
            public string FrontStartText { get; init; } = "";
            public string RearFileName { get; init; } = "(なし)";
            public string RearStartText { get; init; } = "";
            /// <summary>Rear開始 − Front開始（秒）。どちらかが無い行は空。</summary>
            public string OffsetText { get; init; } = "";
            /// <summary>この行が占めるFrontファイル内の位置範囲（例: 0:29 – 2:00）。</summary>
            public string FrontRangeText { get; init; } = "";
            public string RearRangeText { get; init; } = "";

            // 並べ替え用（表示しない）
            public DateTime? FrontStart { get; init; }
            public DateTime? RearStart { get; init; }
            public DateTime SpanStart { get; init; }
        }

        private readonly List<PairOverlapRow> _all;

        public DashcamPairListWindow(IReadOnlyList<PairOverlapRow> rows, string note)
        {
            InitializeComponent();
            _all = rows.ToList();
            NoteText.Text = note;
            ApplyViewMode();
        }

        /// <summary>開いたままのウィンドウの内容を最新の算出結果へ差し替える（再スキャン時・再度「ペア一覧」を押したとき）。</summary>
        public void UpdateRows(IReadOnlyList<PairOverlapRow> rows, string note)
        {
            _all.Clear();
            _all.AddRange(rows);
            NoteText.Text = note;
            ApplyViewMode();
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e) => ApplyViewMode();

        private void ApplyViewMode()
        {
            // InitializeComponent中(IsChecked指定によるCheckedイベント)は未接続のため何もしない
            if (Grid_Rows == null || _all == null) return;

            IEnumerable<PairOverlapRow> view;
            if (ViewRearRadio.IsChecked == true)
            {
                // Rear基準: Rearの開始時刻順。Frontが無い区間(RearOnly)も含め、Front側だけの行(FrontOnly)は除く
                view = _all.Where(r => r.Kind != RowKind.FrontOnly)
                    .OrderBy(r => r.RearStart ?? r.SpanStart)
                    .ThenBy(r => r.SpanStart);
            }
            else
            {
                // Front基準: Frontの開始時刻順。Rearが無い区間(FrontOnly)も含め、Rear側だけの行(RearOnly)は除く
                view = _all.Where(r => r.Kind != RowKind.RearOnly)
                    .OrderBy(r => r.FrontStart ?? r.SpanStart)
                    .ThenBy(r => r.SpanStart);
            }

            var list = view.ToList();
            for (int i = 0; i < list.Count; i++) list[i].No = i + 1;
            Grid_Rows.ItemsSource = null;
            Grid_Rows.ItemsSource = list;
        }

        /// <summary>「通知情報」タブの内容を最新のスナップショットへ更新する。
        /// DashcamPlayerView側が、このウィンドウが開いている間だけ定期的に呼ぶ想定
        /// （閉じている間は呼ばれない＝コストをかけない）。</summary>
        public void UpdateDebugSnapshot(MapInfoDebugSnapshot s)
        {
            DebugLatLngText.Text = s.HasLastFrame
                ? $"{s.CurrentLat:F6}, {s.CurrentLng:F6}  (GPS:{(s.HasGpsFix ? "有効" : "ロスト")})"
                : "(フレーム無し)";
            DebugCumKmText.Text = $"{s.CurrentCumKm:F3} km  /  {(s.HeadingDeg.HasValue ? s.HeadingDeg.Value.ToString("F0") + "°" : "-")}";
            DebugCountsText.Text = $"route:{s.RoutePointCount}pt  tunnel:{s.TunnelCount}  SA/PA:{s.SaPaCount}  place:{s.PlaceCount}  road:{s.HighwayWayCount}";
            DebugFlagsText.Text =
                $"RouteReady={s.RouteReady}  IsOnExpressway={s.IsOnExpressway}  IsPassingTunnel={s.IsPassingTunnel}  ShouldShowOverlay={s.ShouldShowOverlay}\n" +
                $"HighwayName=\"{s.HighwayName}\"  Location=\"{s.CurrentLocationName}\"";

            TunnelDebugGrid.ItemsSource = s.NearbyTunnels;
            SaPaDebugGrid.ItemsSource = s.NearbySaPas;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
