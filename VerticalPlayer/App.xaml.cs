using System.IO;
using System.Windows;

namespace VerticalPlayer
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var win = new MainWindow();
            win.Show();

            // 引数が渡されていれば最初のファイルを再生
            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
                win.LoadVideoFromArg(e.Args[0]);
        }
    }
}
