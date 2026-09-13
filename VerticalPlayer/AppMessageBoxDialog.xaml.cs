using System.Windows;
using System.Windows.Media;
using SwmColor = System.Windows.Media.Color;

namespace VerticalPlayer;

/// <summary>
/// System.Windows.MessageBox 互換の静的 Show API を持つ、ダークテーマ準拠のカスタムメッセージダイアログ。
/// OS標準の MessageBox（ライトテーマの白いダイアログ）がアプリ全体のダークデザインから浮いてしまうため、
/// Editor の UnsavedChangesDialog と同じビジュアル言語（#1E1E1E背景、角丸ボタン、Segoe UI/Yu Gothic UI）で統一する。
///
/// 呼び出し側は既存の
///     System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning)
/// を
///     VerticalPlayer.AppMessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning, isDarkMode)
/// に置き換えるだけで移行できるよう、引数の形・戻り値（MessageBoxResult）を揃えている。
/// </summary>
public static class AppMessageBox
{
    /// <summary>Owner とテーマを指定して表示する（isDarkMode の既定値は false とし、アプリに合わせます）</summary>
    public static MessageBoxResult Show(Window? owner, string message, string title = "VerticalPlayer",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information, bool isDarkMode = false)
    {
        var dlg = new AppMessageBoxDialog(message, title, button, icon, isDarkMode) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    /// <summary>Owner なしで表示する（MessageBox.Show(message, title, ...) 互換のオーバーロード）。</summary>
    public static MessageBoxResult Show(string message, string title = "VerticalPlayer",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information, bool isDarkMode = false)
        => Show(null, message, title, button, icon, isDarkMode);
}

public partial class AppMessageBoxDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    private readonly bool _isDarkMode;

    public AppMessageBoxDialog(string message, string title, MessageBoxButton button, MessageBoxImage icon, bool isDarkMode)
    {
        InitializeComponent();
        _isDarkMode = isDarkMode;

        // ライトモードの場合は、動的リソースを明るい配色で上書きする
        if (!isDarkMode)
        {
            Resources["WindowBgBrush"] = new SolidColorBrush(SwmColor.FromRgb(0xF9, 0xF9, 0xF9));
            Resources["TextBrush"] = new SolidColorBrush(SwmColor.FromRgb(0x33, 0x33, 0x33));
            Resources["SubTextBrush"] = new SolidColorBrush(SwmColor.FromRgb(0x66, 0x66, 0x66));
            Resources["BtnBgBrush"] = new SolidColorBrush(SwmColor.FromRgb(0xE1, 0xE1, 0xE1));
            Resources["BtnHoverBrush"] = new SolidColorBrush(SwmColor.FromRgb(0xD1, 0xD1, 0xD1));
            Resources["BtnBorderBrush"] = new SolidColorBrush(SwmColor.FromRgb(0xCC, 0xCC, 0xCC));
        }

        Title = title;
        TbTitle.Text = title;
        TbMessage.Text = message;

        ApplyIcon(icon);
        ApplyButtons(button);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // タイトルバーの色もテーマフラグに合わせて処理する
        LightDarkTitleBarHelper.Apply(this, _isDarkMode);
    }

    /// <summary>アイコン種別に応じて絵文字・配色・既定の Result（×ボタン等で閉じた場合）を設定する。</summary>
    private void ApplyIcon(MessageBoxImage icon)
    {
        switch (icon)
        {
            case MessageBoxImage.Error:
                TbIcon.Text = "⛔";
                IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0xFF, 0x6B, 0x6B));
                TbIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B));
                break;
            case MessageBoxImage.Warning:
                TbIcon.Text = "⚠";
                IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0xFF, 0xC1, 0x07));
                TbIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07));
                break;
            case MessageBoxImage.Question:
                TbIcon.Text = "？";
                IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0x00, 0x78, 0xD4));
                TbIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
                break;
            default: // Information / None
                TbIcon.Text = "ℹ";
                IconBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 0x78, 0xDC, 0x78));
                TbIcon.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x78, 0xDC, 0x78));
                break;
        }
    }

    /// <summary>ボタン構成に応じて表示するボタンを切り替える。</summary>
    private void ApplyButtons(MessageBoxButton button)
    {
        switch (button)
        {
            case MessageBoxButton.YesNo:
                BtnYes.Visibility = Visibility.Visible;
                BtnNo.Visibility = Visibility.Visible;
                BtnYes.Focus();
                break;
            case MessageBoxButton.OKCancel:
                // 本アプリでは未使用想定だが、互換性のため OK/キャンセル相当として
                // OK のみ表示しキャンセルは×ボタン（Result=Cancel）に委ねる。
                BtnOk.Visibility = Visibility.Visible;
                BtnOk.Focus();
                break;
            default: // OK
                BtnOk.Visibility = Visibility.Visible;
                BtnOk.Focus();
                break;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => CloseWith(MessageBoxResult.OK);
    private void BtnYes_Click(object sender, RoutedEventArgs e) => CloseWith(MessageBoxResult.Yes);
    private void BtnNo_Click(object sender, RoutedEventArgs e) => CloseWith(MessageBoxResult.No);

    private void CloseWith(MessageBoxResult result)
    {
        Result = result;
        DialogResult = true;
        Close();
    }
}