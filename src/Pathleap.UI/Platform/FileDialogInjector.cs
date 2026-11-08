using System.Runtime.InteropServices;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>前台文件对话框的识别结果；<see cref="Kind"/> 为空表示不是受支持的文件对话框。</summary>
public sealed record FileDialogInfo(IntPtr Handle, string Title, string Kind)
{
    public static FileDialogInfo None { get; } = new(IntPtr.Zero, string.Empty, string.Empty);

    public bool IsValid => Handle != IntPtr.Zero;

    public bool IsFileDialog => IsValid && Kind.Length > 0;
}

/// <summary>窗口在屏幕坐标系里的可见外框（已扣除 DWM 阴影）。</summary>
public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

/// <summary>
/// 标准 Windows 文件对话框（打开 / 另存为 / 选择文件夹）注入。
/// 只处理窗口类 <c>#32770</c> 的系统对话框；自绘的第三方选择器不在此列，注入前会用标题白名单校验，
/// 避免把路径误写进「运行」「属性」等同样使用 #32770 的对话框。
/// </summary>
internal static class FileDialogInjector
{
    private const string DialogClass = "#32770";
    private const uint WmSetText = 0x000C;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint VkReturn = 0x0D;
    private const int GwlStyle = -16;
    private const int EsReadOnly = 0x0800;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, char[] className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.Rect rect);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int DwmwaExtendedFrameBounds = 9;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeMethods.Rect value, int size);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    /// <summary>仅按窗口类判断是否为系统 #32770 对话框，用于监视阶段的诊断日志。</summary>
    public static bool HasStandardDialogClass(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero
               && IsWindow(hwnd)
               && string.Equals(ClassName(hwnd), DialogClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在热键触发时调用：记录当前前台的文件对话框（此时浮层还没抢走前台）。</summary>
    public static FileDialogInfo CaptureActive() => Describe(GetForegroundWindow());

    /// <summary>枚举当前所有可见、受支持的文件对话框（用于多对话框各自挂一条栏）。</summary>
    public static List<FileDialogInfo> EnumerateFileDialogs()
    {
        var lpResult = new List<FileDialogInfo>();
        EnumWindows((hwnd, _) =>
        {
            var lpInfo = Describe(hwnd);
            if (lpInfo.IsFileDialog)
            {
                lpResult.Add(lpInfo);
            }

            return true;
        }, IntPtr.Zero);
        return lpResult;
    }

    /// <summary>读取对话框的实际可见边界：优先 DWM 扩展外框，避免把阴影算进尺寸。</summary>
    public static ScreenBounds GetFrameBounds(IntPtr hwnd)
    {
        NativeMethods.Rect lpRect;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out lpRect, Marshal.SizeOf<NativeMethods.Rect>()) == 0
            || GetWindowRect(hwnd, out lpRect))
        {
            return new ScreenBounds(lpRect.Left, lpRect.Top, lpRect.Right, lpRect.Bottom);
        }

        return new ScreenBounds(0, 0, 0, 0);
    }

    public static FileDialogInfo Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return FileDialogInfo.None;
        }

        if (!string.Equals(ClassName(hwnd), DialogClass, StringComparison.OrdinalIgnoreCase))
        {
            return FileDialogInfo.None;
        }

        var lpTitle = WindowText(hwnd);
        var lpKind = ClassifyKind(lpTitle);
        return lpKind.Length == 0
            ? FileDialogInfo.None
            : new FileDialogInfo(hwnd, lpTitle, lpKind);
    }

    /// <summary>浮层隐藏后，把文件夹路径写进对话框文件名框并回车，让对话框跳转到该文件夹。</summary>
    public static bool TryInject(IntPtr dialog, string path)
    {
        var lpInfo = Describe(dialog);
        if (!lpInfo.IsFileDialog)
        {
            AppLog.Warn($"文件对话框注入取消：目标已失效或不是受支持对话框（hwnd={dialog}）");
            return false;
        }

        var lpEdit = FindFileNameEdit(dialog);
        if (lpEdit == IntPtr.Zero)
        {
            AppLog.Warn($"文件对话框注入失败：未找到文件名输入框（title={lpInfo.Title}）");
            return false;
        }

        SendMessageW(lpEdit, WmSetText, IntPtr.Zero, path);
        FocusDialog(dialog, lpEdit);

        // 只发回车：文件夹路径会被对话框当作「进入该目录」，不会误当作文件确认而关闭；
        // 「选择文件夹」对话框按回车则是选中并关闭，符合预期。额外发 WM_COMMAND(IDOK)
        // 会让打开文件对话框把目录当成已选文件直接关掉，因此不再发送。
        PostMessageW(lpEdit, WmKeyDown, new IntPtr(VkReturn), IntPtr.Zero);
        PostMessageW(lpEdit, WmKeyUp, new IntPtr(VkReturn), IntPtr.Zero);

        AppLog.Info($"文件对话框注入已发送: kind={lpInfo.Kind} path={path} edit={lpEdit}");
        return true;
    }

    private static string ClassifyKind(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Contains("打开方式", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var lpLower = title.ToLowerInvariant();
        if (lpLower.Contains("文件夹") || lpLower.Contains("folder") || lpLower.Contains("browse"))
        {
            return "选择文件夹";
        }

        if (lpLower.Contains("另存为") || lpLower.Contains("保存") || lpLower.Contains("save as") || lpLower.Contains("save"))
        {
            return "保存";
        }

        if (lpLower.Contains("打开") || lpLower.Contains("open") || lpLower.Contains("选择文件") || lpLower.Contains("select file"))
        {
            return "打开";
        }

        return string.Empty;
    }

    /// <summary>
    /// 文件名输入框优先取 ComboBox 内的 Edit；找不到时回退到位置最靠下的可见 Edit
    /// （新版对话框顶部还有搜索框，靠下的才是文件名）。
    /// </summary>
    private static IntPtr FindFileNameEdit(IntPtr dialog)
    {
        var lpBest = IntPtr.Zero;
        var lpBestScore = 0;
        var lpBestTop = int.MinValue;

        EnumChildWindows(dialog, (hwnd, _) =>
        {
            if (!string.Equals(ClassName(hwnd), "Edit", StringComparison.OrdinalIgnoreCase) || !IsWindowVisible(hwnd))
            {
                return true;
            }

            if ((GetWindowLongW(hwnd, GwlStyle) & EsReadOnly) != 0)
            {
                return true;
            }

            if (!GetWindowRect(hwnd, out var lpRect))
            {
                return true;
            }

            var lpWidth = lpRect.Right - lpRect.Left;
            var lpHeight = lpRect.Bottom - lpRect.Top;
            if (lpWidth < 60 || lpHeight < 12)
            {
                return true;
            }

            var lpScore = HasAncestorClass(hwnd, "ComboBox") || HasAncestorClass(hwnd, "ComboBoxEx32") ? 2 : 1;
            if (lpScore > lpBestScore || (lpScore == lpBestScore && lpRect.Top > lpBestTop))
            {
                lpBest = hwnd;
                lpBestScore = lpScore;
                lpBestTop = lpRect.Top;
            }

            return true;
        }, IntPtr.Zero);

        return lpBest;
    }

    private static bool HasAncestorClass(IntPtr hwnd, string expected)
    {
        var lpParent = GetParent(hwnd);
        for (var lpDepth = 0; lpDepth < 6 && lpParent != IntPtr.Zero; lpDepth++)
        {
            if (string.Equals(ClassName(lpParent), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            lpParent = GetParent(lpParent);
        }

        return false;
    }

    private static void FocusDialog(IntPtr dialog, IntPtr edit)
    {
        var lpCurrent = GetCurrentThreadId();
        var lpForeground = GetForegroundWindow();
        var lpForegroundThread = GetWindowThreadProcessId(lpForeground, IntPtr.Zero);
        var lpDialogThread = GetWindowThreadProcessId(dialog, IntPtr.Zero);

        var lpAttachedForeground = lpCurrent != lpForegroundThread
                                   && AttachThreadInput(lpCurrent, lpForegroundThread, true);
        var lpAttachedDialog = lpCurrent != lpDialogThread
                               && AttachThreadInput(lpCurrent, lpDialogThread, true);
        try
        {
            SetForegroundWindow(dialog);
            SetFocus(edit);
            Thread.Sleep(20);
        }
        finally
        {
            if (lpAttachedDialog)
            {
                AttachThreadInput(lpCurrent, lpDialogThread, false);
            }

            if (lpAttachedForeground)
            {
                AttachThreadInput(lpCurrent, lpForegroundThread, false);
            }
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var lpBuffer = new char[256];
        return GetClassNameW(hwnd, lpBuffer, lpBuffer.Length) > 0
            ? new string(lpBuffer).TrimEnd('\0')
            : string.Empty;
    }

    private static string WindowText(IntPtr hwnd)
    {
        var lpBuffer = new char[512];
        return GetWindowTextW(hwnd, lpBuffer, lpBuffer.Length) > 0
            ? new string(lpBuffer).TrimEnd('\0')
            : string.Empty;
    }
}

