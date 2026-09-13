using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VerticalPlayer;

/// <summary>
/// Windows 10 1809以降のDWM API (DwmSetWindowAttribute) を使い、
/// ウィンドウのタイトルバーをダークモード表示にする共通ヘルパー。
/// MainWindowに元々あった実装（ApplyDarkTitleBar）を、他ウィンドウ
/// （EditorWindow / UnsavedChangesDialog 等）からも使えるように切り出したもの。
/// </summary>
public static class LightDarkTitleBarHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // DWMWA_USE_IMMERSIVE_DARK_MODE。Windows 10 1903以降は20、それより前のビルドでは19。
    // 古いビルド向けに両方試すことで、対応バージョンの差異を吸収する。
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    /// <summary>
    /// 指定ウィンドウのタイトルバーをダーク表示にする。
    /// ウィンドウハンドルが確定済み（Loaded以降、またはShow済み）である必要がある。
    /// </summary>
    public static void Apply(Window window, bool isDarkMode = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Apply(hwnd, isDarkMode);
    }

    /// <summary>ウィンドウハンドルを直接指定する版。</summary>
    public static void Apply(IntPtr hwnd, bool isDarkMode = true)
    {
        if (hwnd == IntPtr.Zero) return;

        int value = isDarkMode ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
    }

    /// <summary>
    /// ウィンドウの Loaded イベントに自動的にフックし、タイトルバーをダーク化する。
    /// コンストラクタやXAMLの Loaded="..." を書きたくない場合の簡易呼び出し用。
    /// </summary>
    public static void ApplyOnLoaded(Window window, bool isDarkMode = true)
    {
        if (window.IsLoaded)
        {
            Apply(window, isDarkMode);
        }
        else
        {
            window.Loaded += (_, _) => Apply(window, isDarkMode);
        }
    }
}