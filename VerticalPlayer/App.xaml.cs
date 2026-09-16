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

            // 起動引数でフォルダが渡された場合: ドラレコモードへ直接入り、そのフォルダを
            // 読み込む（レジューム経路＝前回のドライブ/ファイル選択/再生位置の復元は
            // スキップする。リア追従・リア表示・ズーム等のオプション/スイッチ類は
            // Window_Loaded側のRestoreSettingsで従来通り復元される）。
            if (e.Args.Length > 0 && Directory.Exists(e.Args[0]))
                win.SetStartupFolder(e.Args[0]);

            win.Show();

            // 引数がファイルの場合は従来通り最初のファイルを再生（フォルダの場合はここでは何もしない）
            if (e.Args.Length > 0 && File.Exists(e.Args[0]))
                win.LoadVideoFromArg(e.Args[0]);
        }
    }
}