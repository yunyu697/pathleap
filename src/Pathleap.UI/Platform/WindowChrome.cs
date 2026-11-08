using Microsoft.UI.Xaml;
using Pathleap.Shared.Diagnostics;
using Windows.UI;

namespace Pathleap.UI.Platform;

/// <summary>
/// 窗口外轮廓统一处理。
/// WinUI 3 的 OverlappedPresenter 关掉标题栏后，DWM 仍会在窗口最外圈按 DPI 画一圈 1 DIP 的边框
/// （200% 缩放下是 2 物理像素）。这一圈无法用 XAML 覆盖，若再用 RootBorder 自绘描边，
/// 就会出现「系统框 + 自绘框」两层，正是实测反馈的双层边框。
/// 因此这里把 DWM 边框直接设成设计稿 Border2 的实色，让系统框成为唯一的一层；
/// 圆角交给 <see cref="NcFrame"/> 的窗口区域裁剪，DWM 侧关闭系统圆角以免两种半径打架。
/// </summary>
internal static class WindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const uint DwmwcpRound = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    public static void Apply(Window window, Color border, bool isDark)
    {
        try
        {
            var lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // 应用内主题独立于系统主题，DWM 默认按系统（浅色）画内描边，必须显式告诉它。
            var lpDark = isDark ? 1u : 0u;
            DwmSetWindowAttribute(lpHwnd, DwmwaUseImmersiveDarkMode, ref lpDark, sizeof(uint));

            var lpCorner = DwmwcpRound;
            DwmSetWindowAttribute(lpHwnd, DwmwaWindowCornerPreference, ref lpCorner, sizeof(uint));

            // DWMWA_BORDER_COLOR 取 COLORREF：0x00BBGGRR，无 alpha。
            var lpBorder = (uint)(border.B << 16 | border.G << 8 | border.R);
            DwmSetWindowAttribute(lpHwnd, DwmwaBorderColor, ref lpBorder, sizeof(uint));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"窗口外轮廓设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 无系统标题栏的窗口自己发起拖动。把标题栏区域的按下消息转成 HTCAPTION 交给系统，
    /// 由系统走标准移动循环，缩放、吸附与多显示器行为都保持不变。
    /// </summary>
    public static void BeginDrag(Window window)
    {
        try
        {
            var lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ReleaseCapture();
            SendMessage(lpHwnd, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"窗口拖动失败: {ex.Message}");
        }
    }

    private const uint WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
