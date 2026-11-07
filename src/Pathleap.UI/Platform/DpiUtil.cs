using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Pathleap.UI.Platform;

/// <summary>
/// 窗口在构造函数里定位时 XamlRoot 还没就绪，读不到 RasterizationScale；这里直接向窗口问 DPI，
/// 保证 MoveAndResize 用的是物理像素，窗口按设计稿的 DIP 尺寸显示。
/// </summary>
internal static class DpiUtil
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public static double Scale(Window window)
    {
        try
        {
            var lpHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var lpDpi = GetDpiForWindow(lpHandle);
            return lpDpi > 0 ? lpDpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }
}
