using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// SD_DBLIST.DBに保存されているFront/Rearペアリング結果を一覧表示し、Rearを手動で
    /// 差し替えられるようにする確認・編集用ウィンドウ。保存すると次回のスキャンから反映される
    /// （このウィンドウ自体は開いている再生リストをその場では更新しない。ドライブ/フォルダを
    /// 選び直す＝再スキャンすれば反映される）。
    /// メインウィンドウと同じ自前タイトルバー（WindowStyle="None"、ドラッグ移動・最小化・
    /// 閉じるのみ）にしてアプリ全体の見た目と統一している。
    /// </summary>
    public partial class DashcamPairListWindow : Window
    {
        private readonly List<PairEditRow> _rows;

        public sealed class PairEditRow
        {
            public long No { get; set; }
            public string FrontFileName { get; set; } = "";
            public string RearDisplay { get; set; } = "(なし)";
            public string UpdatedAt { get; set; } = "";
        }

        public DashcamPairListWindow(string driveRoot, string folderInfo, IReadOnlyList<string> availableRearFileNames)
        {
            InitializeComponent();

            var comboItems = new List<string> { "(なし)" };
            comboItems.AddRange(availableRearFileNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            Col_Rear.ItemsSource = comboItems;

            _rows = DashcamPairDatabase.LoadRows(driveRoot, folderInfo)
                .Select(r => new PairEditRow
                {
                    No = r.No,
                    FrontFileName = r.FrontFileName,
                    RearDisplay = r.RearFileName ?? "(なし)",
                    UpdatedAt = r.UpdatedAt
                })
                .ToList();

            Grid_Rows.ItemsSource = _rows;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            Grid_Rows.CommitEdit(DataGridEditingUnit.Row, true);

            foreach (var row in _rows)
            {
                string? rear = row.RearDisplay == "(なし)" ? null : row.RearDisplay;
                DashcamPairDatabase.UpdateRearByNo(row.No, rear);
            }

            AppMessageBox.Show(this, "保存しました。再生リストへ反映するには、ドライブ/フォルダを選び直して再スキャンしてください。",
                "ペア一覧", MessageBoxButton.OK, MessageBoxImage.Information, isDarkMode: true);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
