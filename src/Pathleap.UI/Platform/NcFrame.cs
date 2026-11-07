using Microsoft.UI.Xaml;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>
/// 去掉 WinUI 窗口残留的非客户区。即使调用 SetBorderAndTitleBar(false, false)，
/// 窗口仍保留 1 DIP 的非客户区，系统会在客户区外沿用 3D 亮色画一层细边，
/// 与 XAML 自绘描边叠成两层且无法通过 DWM 属性覆盖。
/// 这里子类化窗口：WM_NCCALCSIZE 返回 0 让客户区铺满窗口矩形，WM_NCPAINT 返回 0
/// 阻止系统再画非客户区内容。外轮廓只保留 DWM 按 <see cref="WindowChrome"/>
/// 设定颜色画出的那一圈边框，因此 XAML 侧不要再叠自绘描边。
/// 只用于无系统标题栏的窗口；保留标题栏的窗口不要调用，否则会连标题栏按钮一起失去。
/// </summary>
internal static class NcFrame
{
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCPAINT = 0x0085;

    private static readonly Dictionary<IntPtr, SubclassProc> Handles = new();

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data);

    [System.Runtime.InteropServices.DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id, IntPtr data);

    public static void Apply(Window window)
    {
        try
        {
            var lpHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (Handles.ContainsKey(lpHwnd))
            {
                return;
            }

            var lpCallback = new SubclassProc(OnSubclass);
            Handles[lpHwnd] = lpCallback;
            if (!SetWindowSubclass(lpHwnd, lpCallback, UIntPtr.Zero, IntPtr.Zero))
            {
                Handles.Remove(lpHwnd);
                AppLog.Warn($"窗口非客户区子类化失败，HWND=0x{lpHwnd.ToInt64():X}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"窗口非客户区处理失败: {ex.Message}");
        }
    }

    private static IntPtr OnSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr data)
    {
        switch (message)
        {
            case WM_NCCALCSIZE:
            case WM_NCPAINT:
                return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
