using System.Runtime.InteropServices;

namespace Pathleap.UI.Platform;

/// <summary>按 Z 序找最上层的资源管理器文件夹窗口。</summary>
internal static class DialogWindowFinder
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static IntPtr FindTopExplorerWindow(HashSet<IntPtr> candidates)
    {
        var lpFound = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || !candidates.Contains(hwnd))
            {
                return true;
            }

            lpFound = hwnd;
            return false;
        }, IntPtr.Zero);
        return lpFound;
    }
}
