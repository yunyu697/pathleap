using System.Runtime.InteropServices;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>
/// 监视系统里出现的文件对话框：出现即通知宿主挂栏，移动/缩放通知重定位，
/// 对话框销毁立即通知撤栏。基于 SetWinEventHook（OUTOFCONTEXT），回调在注册线程
/// 的消息泵上派发，因此必须在 UI 线程创建。
/// </summary>
internal sealed class FileDialogWatcher : IDisposable
{
    private const int EventSystemForeground = 0x0003;
    private const int EventSystemMinimizeStart = 0x0016;
    private const int EventSystemMinimizeEnd = 0x0017;
    private const int EventObjectCreate = 0x8000;
    private const int EventObjectDestroy = 0x8001;
    private const int EventObjectShow = 0x8002;
    private const int EventObjectHide = 0x8003;
    private const int EventObjectLocationChange = 0x800B;
    private const int ObjidWindow = 0;
    private const uint WineventOutofcontext = 0x0000;

    private delegate void WinEventCallback(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint threadId,
        uint time);

    private readonly WinEventCallback _lpCallback;
    private readonly HashSet<IntPtr> _lpKnown = new();
    private readonly List<IntPtr> _lpHooks = new();
    private bool _lpDisposed;

    public FileDialogWatcher()
    {
        _lpCallback = OnWinEvent;

        // 单进程内只注册三条范围钩子，避免为每个事件建钩子。
        AddHook(EventObjectCreate, EventObjectHide);
        AddHook(EventObjectLocationChange, EventObjectLocationChange);
        AddHook(EventSystemForeground, EventSystemMinimizeEnd);

        AppLog.Info($"文件对话框监视已启动（钩子 {_lpHooks.Count} 条）。");
    }

    /// <summary>
    /// 把已经存在的对话框纳入监视集合。启动时补挂已有对话框、或从其他途径挂栏时都必须调用，
    /// 否则移动/缩放/销毁事件会被当作无关窗口过滤掉。
    /// </summary>
    public void Track(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _lpKnown.Add(handle);
    }

    /// <summary>受支持的文件对话框出现（含从隐藏恢复）。</summary>
    public event Action<FileDialogInfo>? DialogAppeared;

    /// <summary>对话框销毁；参数是对话框句柄。</summary>
    public event Action<IntPtr>? DialogDestroyed;

    /// <summary>对话框移动/缩放，需要重新贴边。</summary>
    public event Action<IntPtr>? DialogGeometryChanged;

    /// <summary>前台窗口变化（用于判断哪条栏应响应键盘）。</summary>
    public event Action<IntPtr>? ForegroundChanged;

    private bool AddHook(int eventMin, int eventMax)
    {
        var lpHandle = SetWinEventHook(
            (uint)eventMin,
            (uint)eventMax,
            IntPtr.Zero,
            _lpCallback,
            0,
            0,
            WineventOutofcontext);
        if (lpHandle == IntPtr.Zero)
        {
            AppLog.Warn($"SetWinEventHook 失败: {eventMin:X}-{eventMax:X}");
            return false;
        }

        _lpHooks.Add(lpHandle);
        return true;
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint threadId,
        uint time)
    {
        if (_lpDisposed || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType == EventSystemForeground)
        {
            ForegroundChanged?.Invoke(hwnd);
            return;
        }

        if (idObject != ObjidWindow || idChild != 0)
        {
            return;
        }

        switch (eventType)
        {
            case EventObjectCreate:
            case EventObjectShow:
            {
                if (_lpKnown.Contains(hwnd))
                {
                    if (eventType == EventObjectShow)
                    {
                        DialogGeometryChanged?.Invoke(hwnd);
                    }

                    return;
                }

                var lpInfo = FileDialogInjector.Describe(hwnd);
                if (!lpInfo.IsFileDialog)
                {
                    if (FileDialogInjector.HasStandardDialogClass(hwnd))
                    {
                        AppLog.Info($"窗口类为 #32770 但标题不在白名单内，未挂栏: hwnd=0x{hwnd.ToInt64():X} event=0x{eventType:X}");
                    }

                    return;
                }

                _lpKnown.Add(hwnd);
                AppLog.Info($"检测到文件对话框: kind={lpInfo.Kind} hwnd=0x{hwnd.ToInt64():X} title={lpInfo.Title}");
                DialogAppeared?.Invoke(lpInfo);
                return;
            }

            case EventObjectDestroy:
                if (_lpKnown.Remove(hwnd))
                {
                    DialogDestroyed?.Invoke(hwnd);
                }

                return;

            case EventObjectLocationChange:
                if (_lpKnown.Contains(hwnd))
                {
                    DialogGeometryChanged?.Invoke(hwnd);
                }

                return;

            case EventObjectHide:
                // 对话框只是临时隐藏时保留栏；真正的销毁由 EVENT_OBJECT_DESTROY 处理。
                return;

            case EventSystemMinimizeStart:
            case EventSystemMinimizeEnd:
                if (_lpKnown.Contains(hwnd))
                {
                    DialogGeometryChanged?.Invoke(hwnd);
                }

                return;
        }
    }

    public void Dispose()
    {
        _lpDisposed = true;
        foreach (var lpHook in _lpHooks)
        {
            UnhookWinEvent(lpHook);
        }

        _lpHooks.Clear();
        _lpKnown.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
}
