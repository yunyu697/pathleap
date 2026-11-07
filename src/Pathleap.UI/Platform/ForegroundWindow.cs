using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Pathleap.UI.Platform;

/// <summary>
/// 通过热键从别的程序唤出浮层时，WinUI 的 Activate() 常被前台锁挡住。
/// 把当前 UI 线程挂到前台线程的输入队列上，再 SetForegroundWindow，浮层才能真正拿到键盘焦点。
/// </summary>
internal static class ForegroundWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    public static void ForceActivate(Window window)
    {
        var lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var lpForeground = GetForegroundWindow();
        if (lpForeground == lpHwnd)
        {
            return;
        }

        var lpCurrentThread = GetCurrentThreadId();
        var lpForegroundThread = GetWindowThreadProcessId(lpForeground, IntPtr.Zero);
        var lpAttached = lpForegroundThread != 0
                         && lpCurrentThread != lpForegroundThread
                         && AttachThreadInput(lpCurrentThread, lpForegroundThread, true);
        try
        {
            BringWindowToTop(lpHwnd);
            SetForegroundWindow(lpHwnd);
            SetFocus(lpHwnd);
        }
        finally
        {
            if (lpAttached)
            {
                AttachThreadInput(lpCurrentThread, lpForegroundThread, false);
            }
        }
    }
}
