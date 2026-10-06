using System.Windows.Controls;

namespace VerticalPlayer.Dashcam
{
    /// <summary>
    /// 情報一覧ウィンドウの「イベント」タブの中身。DataContextにDashcamEventListViewModelを渡して使う。
    /// 行の選択（ダブルクリック／Enter）はViewModelのSelectEventCommandへ転送するだけで、
    /// 表示・フィルター・ジャンプの実処理はすべてViewModel側にある。
    /// </summary>
    public partial class DashcamEventTabView : UserControl
    {
        public DashcamEventTabView()
        {
            InitializeComponent();
        }

        private void Row_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.Item is EventItemViewModel item)
            {
                Execute(item);
                e.Handled = true;
            }
        }

        private void EventGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && EventGrid.SelectedItem is EventItemViewModel item)
            {
                Execute(item);
                e.Handled = true;
            }
        }

        private void Execute(EventItemViewModel item)
        {
            var command = (DataContext as DashcamEventListViewModel)?.SelectEventCommand;
            if (command != null && command.CanExecute(item))
                command.Execute(item);
        }
    }
}
