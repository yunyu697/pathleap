using System.Diagnostics;
using System.Runtime.InteropServices;
using Pathleap.Shared.Diagnostics;
using Pathleap.UI.Platform;

namespace Pathleap.UI.Platform;

/// <summary>
/// 独立的 Win32 消息线程（STA + 消息泵）：
///  - 持有全局热键（WM_HOTKEY 到达时立即取 QPC 时间戳，再抛回 UI 线程）；
///  - 持有托盘图标与右键菜单；
///  - 不依赖 WinUI 窗口，因此窗口隐藏时热键仍然有效。
/// </summary>
internal sealed class NativeMessageHost : IDisposable
{
    private const int MenuShow = 1001;
    private const int MenuBench = 1002;
    private const int MenuOpenConfig = 1003;
    private const int MenuOpenLogs = 1004;
    private const int MenuExit = 1099;
    private const uint WmQuit = 0x0012;
    private const int TrayIconId = 1;

    private readonly ManualResetEventSlim _lpReady = new(false);
    private readonly ManualResetEventSlim _lpStopped = new(false);
    private readonly object _lpTrayGate = new();
    private Thread? _lpThread;
    private uint _lpThreadId;
    private IntPtr _lpHwnd;
    private NativeMethods.WindowProcedure? _lpWindowProcedure;
    private uint _lpTaskbarCreatedMessage;
    private NotifyIconHandle? _lpTray;

    public event Action<long>? HotkeyPressed;

    public event Action? ShowRequested;

    public event Action? BenchRequested;

    public event Action? OpenConfigRequested;

    public event Action? OpenLogRequested;

    public event Action? ExitRequested;

    public HotkeySpec Hotkey { get; private set; } = HotkeySpec.Default;

    public bool HotkeyRegistered { get; private set; }

    public int HotkeyErrorCode { get; private set; }

    public bool TrayAvailable => _lpTray?.Added ?? false;

    public bool Start(HotkeySpec hotkey, string trayTooltip)
    {
        Hotkey = hotkey;
        _lpThread = new Thread(() => ThreadMain(trayTooltip))
        {
            IsBackground = true,
            Name = $"{Shared.Branding.ProductId}.NativeMessageHost"
        };
        _lpThread.SetApartmentState(ApartmentState.STA);
        _lpThread.Start();
        return _lpReady.Wait(TimeSpan.FromSeconds(3));
    }

    private void ThreadMain(string trayTooltip)
    {
        try
        {
            _lpThreadId = NativeMethods.GetCurrentThreadId();
            _lpWindowProcedure = WindowProcedure;

            var lpClassName = $"{Shared.Branding.ProductId}.NativeHost";
            var lpInstance = NativeMethods.GetModuleHandleW(null);
            var lpClass = new NativeMethods.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_lpWindowProcedure),
                Instance = lpInstance,
                ClassName = lpClassName
            };
            NativeMethods.RegisterClassExW(ref lpClass);

            _lpHwnd = NativeMethods.CreateWindowExW(
                0,
                lpClassName,
                Shared.Branding.DisplayName,
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HwndMessage,
                IntPtr.Zero,
                lpInstance,
                IntPtr.Zero);

            if (_lpHwnd == IntPtr.Zero)
            {
                AppLog.Error($"创建消息窗口失败（Win32={Marshal.GetLastWin32Error()}），热键与托盘不可用。");
                _lpReady.Set();
                return;
            }

            _lpTaskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
            HotkeyRegistered = NativeMethods.RegisterHotKey(
                _lpHwnd,
                NativeMethods.HotkeyId,
                Hotkey.Modifiers | NativeMethods.ModNoRepeat,
                Hotkey.VirtualKey);
            if (!HotkeyRegistered)
            {
                HotkeyErrorCode = Marshal.GetLastWin32Error();
                AppLog.Error(
                    $"全局热键注册失败: {Hotkey.Display}（Win32={HotkeyErrorCode}" +
                    (HotkeyErrorCode == NativeMethods.ErrorHotkeyAlreadyRegistered ? " 已被其它程序占用" : string.Empty) + "）");
            }
            else
            {
                AppLog.Info($"全局热键注册成功: {Hotkey.Display}");
            }

            _lpTray = new NotifyIconHandle(_lpHwnd, trayTooltip);
            _lpTray.Add(showWarningIfFailed: false);
            if (_lpTray.Added)
            {
                AppLog.Info($"托盘图标已添加: {trayTooltip}");
            }
            else
            {
                AppLog.Warn($"托盘图标添加失败（Win32={Marshal.GetLastWin32Error()}），等待 TaskbarCreated 消息时重试。");
            }

            _lpReady.Set();
            RunMessageLoop();
        }
        catch (Exception ex)
        {
            AppLog.Error("原生消息线程异常", ex);
            _lpReady.Set();
        }
        finally
        {
            _lpTray?.Dispose();
            if (_lpHwnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_lpHwnd, NativeMethods.HotkeyId);
                NativeMethods.DestroyWindow(_lpHwnd);
                _lpHwnd = IntPtr.Zero;
            }

            NativeMethods.UnregisterClassW($"{Shared.Branding.ProductId}.NativeHost", NativeMethods.GetModuleHandleW(null));
            _lpStopped.Set();
        }
    }

    private void RunMessageLoop()
    {
        while (true)
        {
            var lpResult = NativeMethods.GetMessageW(out var lpMessage, IntPtr.Zero, 0, 0);
            if (lpResult == 0 || lpResult == -1)
            {
                break;
            }

            NativeMethods.TranslateMessage(ref lpMessage);
            NativeMethods.DispatchMessageW(ref lpMessage);
        }
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WmHotkey:
                if ((int)wParam == NativeMethods.HotkeyId)
                {
                    // 第一现场打点：从 WM_HOTKEY 投递到消息泵处理，不含 UI 线程排队时间。
                    var lpTick = Stopwatch.GetTimestamp();
                    AppLog.Debug($"WM_HOTKEY 到达 t={lpTick}f");
                    HotkeyPressed?.Invoke(lpTick);
                    return IntPtr.Zero;
                }

                break;

            case NativeMethods.WmTrayCallback:
                HandleTrayCallback(NativeMethods.GetLowWord(lParam));
                return IntPtr.Zero;

            case NativeMethods.WmDestroy:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        if (_lpTaskbarCreatedMessage != 0 && message == _lpTaskbarCreatedMessage)
        {
            // Explorer 重启后重新添加托盘图标。
            _lpTray?.ReAdd();
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void HandleTrayCallback(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case NativeMethods.WmLButtonUp:
            case NativeMethods.WmLButtonDoubleClick:
                ShowRequested?.Invoke();
                break;

            case NativeMethods.WmRButtonUp:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var lpPoint))
        {
            return;
        }

        var lpMenu = NativeMethods.CreatePopupMenu();
        if (lpMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfString, new IntPtr(MenuShow), $"显示搜索\t{Hotkey.Display}");
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfString, new IntPtr(MenuBench), "运行唤醒基准测试");
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfSeparator, IntPtr.Zero, null);
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfString, new IntPtr(MenuOpenConfig), "打开配置目录");
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfString, new IntPtr(MenuOpenLogs), "打开日志目录");
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfSeparator, IntPtr.Zero, null);
            NativeMethods.AppendMenuW(lpMenu, NativeMethods.MfString, new IntPtr(MenuExit), "退出");

            // 托盘菜单需要在菜单前取得前台权限，否则点击菜单外部不会自动消失。
            NativeMethods.SetForegroundWindow(_lpHwnd);
            var lpCommand = NativeMethods.TrackPopupMenu(
                lpMenu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCommand,
                lpPoint.X,
                lpPoint.Y,
                0,
                _lpHwnd,
                IntPtr.Zero);
            NativeMethods.PostMessageW(_lpHwnd, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (lpCommand)
            {
                case MenuShow:
                    ShowRequested?.Invoke();
                    break;
                case MenuBench:
                    BenchRequested?.Invoke();
                    break;
                case MenuOpenConfig:
                    OpenConfigRequested?.Invoke();
                    break;
                case MenuOpenLogs:
                    OpenLogRequested?.Invoke();
                    break;
                case MenuExit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"托盘菜单异常: {ex.Message}");
        }
        finally
        {
            NativeMethods.DestroyMenu(lpMenu);
        }
    }

    public void ShowTrayWarning(string title, string message)
    {
        _lpTray?.ShowBalloon(title, message, warning: true);
    }

    public void ShowTrayInfo(string title, string message)
    {
        _lpTray?.ShowBalloon(title, message, warning: false);
    }

    public void Dispose()
    {
        if (_lpThread is null)
        {
            return;
        }

        try
        {
            if (_lpThreadId != 0)
            {
                NativeMethods.PostThreadMessageW(_lpThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            }

            _lpStopped.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"原生消息线程停止超时: {ex.Message}");
        }
    }

    /// <summary>托盘图标生命周期封装，允许从任意线程发 balloon 警告。</summary>
    private sealed class NotifyIconHandle : IDisposable
    {
        private readonly IntPtr _lpHwnd;
        private readonly string _lpTip;
        private readonly object _lpGate = new();
        private NativeMethods.NotifyIconData _lpData;

        public NotifyIconHandle(IntPtr hwnd, string tip)
        {
            _lpHwnd = hwnd;
            _lpTip = tip.Length > 120 ? tip[..120] : tip;
            _lpData = new NativeMethods.NotifyIconData
            {
                Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
                Hwnd = hwnd,
                Id = TrayIconId,
                Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
                CallbackMessage = NativeMethods.WmTrayCallback,
                Icon = NativeMethods.LoadIconW(IntPtr.Zero, new IntPtr(NativeMethods.IdiApplication)),
                Tip = _lpTip,
                Info = string.Empty,
                InfoTitle = string.Empty
            };
        }

        public bool Added { get; private set; }

        public bool Add(bool showWarningIfFailed)
        {
            var lpData = _lpData;
            Added = NativeMethods.Shell_NotifyIconW(NativeMethods.NimAdd, ref lpData);
            if (!Added && showWarningIfFailed)
            {
                AppLog.Warn($"Shell_NotifyIcon(NIM_ADD) 失败（Win32={Marshal.GetLastWin32Error()}）");
            }

            return Added;
        }

        public void ReAdd()
        {
            Added = false;
            Add(showWarningIfFailed: true);
        }

        public void ShowBalloon(string title, string message, bool warning)
        {
            lock (_lpGate)
            {
                if (!Added)
                {
                    return;
                }

                var lpData = _lpData;
                lpData.Flags = NativeMethods.NifInfo;
                lpData.InfoTitle = title.Length > 60 ? title[..60] : title;
                lpData.Info = message.Length > 250 ? message[..250] : message;
                lpData.InfoFlags = warning ? NativeMethods.NiifWarning : NativeMethods.NiifInfo;
                NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref lpData);
            }
        }

        public void Dispose()
        {
            lock (_lpGate)
            {
                if (!Added)
                {
                    return;
                }

                var lpData = _lpData;
                NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref lpData);
                Added = false;
            }
        }
    }
}
