using System.Runtime.InteropServices;

namespace Pathleap.UI.Platform;

/// <summary>
/// 快速切换栏需要的 Win32 能力：把 WinUI 窗口作为子窗口挂进文件对话框，
/// 以及读取对话框客户区、转换坐标系。
/// </summary>
internal static class QuickSwitchNative
{
    public const int GwlStyle = -16;
    public const int GwlExstyle = -20;
    public const int GwlHwndParent = -8;

    public const long WsChild = 0x40000000L;
    public const long WsPopup = unchecked((long)0x80000000L);
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExNoActivate = 0x08000000L;

    public const uint SwpNoZorder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const int SwShowNoActivate = 4;
    public const int SwHide = 0;

    public const int DwmwaExtendedFrameBounds = 9;
    public const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hwnd, out NativeMethods.Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int MapWindowPoints(IntPtr from, IntPtr to, ref NativeMethods.Rect points, int count);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.Rect value, int size);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
}
