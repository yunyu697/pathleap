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
    private const uint WmQuit = 0x0012;
    private const uint WmTimer = 0x0113;
    private const int TrayIconId = 1;
    private const int HookWatchdogTimerId = 3;

    /// <summary>UI 线程请求切换唤醒方式（消息线程负责注销/重注册）。</summary>
    private const uint WmApplyHotkey = NativeMethods.WmApp + 2;

    /// <summary>双击判定：两次「按下并松开」之间的最大间隔。</summary>
    private const long TapWindowMs = 380;

    /// <summary>单次按住超过该时长即视为长按，不计为一次 tap。</summary>
    private const long TapMaxHoldMs = 400;

    private readonly ManualResetEventSlim _lpReady = new(false);
    private readonly ManualResetEventSlim _lpStopped = new(false);
    private readonly object _lpTrayGate = new();
    private readonly object _lpHotkeyGate = new();
    private Thread? _lpThread;
    private uint _lpThreadId;
    private IntPtr _lpHwnd;
    private NativeMethods.WindowProcedure? _lpWindowProcedure;
    private uint _lpTaskbarCreatedMessage;
    private NotifyIconHandle? _lpTray;
    private KeyboardHook? _lpKeyboardHook;
    private Func<int, bool, bool, bool>? _lpKeyInterceptor;

    /// <summary>待处理的唤醒方式切换（spec + 完成源），只在消息线程消费。</summary>
    private (HotkeySpec Spec, TaskCompletionSource<HotkeyApplyResult> Completion)? _lpPendingHotkey;

    // 双击状态机：全部字段只在消息线程读写。
    private long _lpTapDownTick;
    private long _lpTapFirstUpTick;
    private bool _lpTapDown;
    private bool _lpTapArmed;
    private bool _lpTapContaminated;

    public event Action<long>? HotkeyPressed;

    /// <summary>快速切换热键（默认 Ctrl+G）按下；该热键在文件对话框打开时用于注入路径。</summary>
    public event Action? QuickHotkeyPressed;

    public event Action? ShowRequested;

    /// <summary>托盘右键：把菜单交给 UI 线程绘制（设计稿的 5 项菜单），参数为屏幕坐标。</summary>
    public event Action<int, int>? TrayMenuRequested;

    public HotkeySpec Hotkey { get; private set; } = HotkeySpec.Default;

    public bool HotkeyRegistered { get; private set; }

    public int HotkeyErrorCode { get; private set; }

    public HotkeySpec QuickHotkey { get; private set; } = HotkeySpec.DefaultQuick;

    public bool QuickHotkeyRegistered { get; private set; }

    public int QuickHotkeyErrorCode { get; private set; }

    public bool TrayAvailable => _lpTray?.Added ?? false;

    /// <summary>键盘钩子是否安装成功；失败时双击类唤醒方式不可用。</summary>
    public bool KeyboardHookRegistered { get; private set; }

    public int KeyboardHookErrorCode { get; private set; }

    /// <summary>
    /// 全局按键拦截器（在原生消息线程回调，必须极短）：参数为虚拟键、是否按下、是否注入；
    /// 返回 true 表示吞掉该键。用于快速切换栏的 1–6/Enter/Esc 与双击唤醒判定。
    /// </summary>
    public Func<int, bool, bool, bool>? KeyInterceptor
    {
        get => _lpKeyInterceptor;
        set => _lpKeyInterceptor = value;
    }

    public bool Start(HotkeySpec hotkey, HotkeySpec quickHotkey, string trayTooltip)
    {
        Hotkey = hotkey;
        QuickHotkey = quickHotkey;
        _lpThread = new Thread(() => ThreadMain(trayTooltip))
        {
            IsBackground = true,
            Name = $"{Shared.Branding.ProductId}.NativeMessageHost"
        };
        _lpThread.SetApartmentState(ApartmentState.STA);
        _lpThread.Start();
        return _lpReady.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 运行期切换唤醒方式：注销旧注册、按新 spec 重新注册（Chord）或确认钩子可用（DoubleTap）。
    /// 必须在消息线程执行，因此投递到线程后再用完成源把结果带回调用方。
    /// </summary>
    public Task<HotkeyApplyResult> ChangeHotkeyAsync(HotkeySpec spec)
    {
        var lpCompletion = new TaskCompletionSource<HotkeyApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_lpThreadId == 0)
        {
            lpCompletion.TrySetResult(HotkeyApplyResult.Fail(-1, "原生消息线程尚未就绪"));
            return lpCompletion.Task;
        }

        lock (_lpHotkeyGate)
        {
            _lpPendingHotkey = (spec, lpCompletion);
        }

        if (!NativeMethods.PostThreadMessageW(_lpThreadId, WmApplyHotkey, IntPtr.Zero, IntPtr.Zero))
        {
            lock (_lpHotkeyGate)
            {
                _lpPendingHotkey = null;
            }

            lpCompletion.TrySetResult(HotkeyApplyResult.Fail(Marshal.GetLastWin32Error(), "无法投递唤醒方式变更"));
        }

        return lpCompletion.Task;
    }

    /// <summary>切换后同步托盘悬浮提示里的唤出方式。</summary>
    public void UpdateTrayTooltip(string tooltip) => _lpTray?.UpdateTip(tooltip);

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
            if (Hotkey.Kind == HotkeyKind.Chord)
            {
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
            }

            QuickHotkeyRegistered = NativeMethods.RegisterHotKey(
                _lpHwnd,
                NativeMethods.QuickHotkeyId,
                QuickHotkey.Modifiers | NativeMethods.ModNoRepeat,
                QuickHotkey.VirtualKey);
            if (!QuickHotkeyRegistered)
            {
                QuickHotkeyErrorCode = Marshal.GetLastWin32Error();
                AppLog.Warn(
                    $"快速切换热键注册失败: {QuickHotkey.Display}（Win32={QuickHotkeyErrorCode}" +
                    (QuickHotkeyErrorCode == NativeMethods.ErrorHotkeyAlreadyRegistered ? " 已被其它程序占用" : string.Empty) + "）");
            }
            else
            {
                AppLog.Info($"快速切换热键注册成功: {QuickHotkey.Display}");
            }

            // 全局键盘钩子：快速切换栏按键与双击唤醒都依赖它；定期自检重装防止被系统静默摘掉。
            _lpKeyboardHook = new KeyboardHook(InvokeKeyInterceptor);
            KeyboardHookRegistered = _lpKeyboardHook.Install();
            KeyboardHookErrorCode = _lpKeyboardHook.LastError;
            if (!KeyboardHookRegistered)
            {
                AppLog.Error($"低层键盘钩子安装失败（Win32={KeyboardHookErrorCode}）。双击类唤醒方式不可用。");
            }
            else
            {
                NativeMethods.SetTimer(_lpHwnd, (IntPtr)HookWatchdogTimerId, 15_000, IntPtr.Zero);
                AppLog.Info("低层键盘钩子已安装（每 15 秒自检重装）。");
            }

            // 双击类唤醒完全依赖低层钩子：钩子不可用就等于热键未注册，必须如实反映到界面与日志。
            if (Hotkey.IsDoubleTap)
            {
                HotkeyRegistered = KeyboardHookRegistered;
                HotkeyErrorCode = KeyboardHookRegistered ? 0 : KeyboardHookErrorCode;
                if (HotkeyRegistered)
                {
                    AppLog.Info($"双击唤醒已启用: {Hotkey.Display}（窗口 {TapWindowMs} ms，最长按住 {TapMaxHoldMs} ms）");
                }
                else
                {
                    AppLog.Error($"双击唤醒不可用: {Hotkey.Display}，低层键盘钩子未安装（Win32={HotkeyErrorCode}）。");
                }
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
            _lpKeyboardHook?.Remove();
            _lpKeyboardHook = null;
            if (_lpHwnd != IntPtr.Zero)
            {
                NativeMethods.KillTimer(_lpHwnd, (IntPtr)HookWatchdogTimerId);
                NativeMethods.UnregisterHotKey(_lpHwnd, NativeMethods.HotkeyId);
                NativeMethods.UnregisterHotKey(_lpHwnd, NativeMethods.QuickHotkeyId);
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

            // PostThreadMessageW 投递的是线程消息（hwnd=NULL），DispatchMessage 不会处理，
            // 必须在消息循环里显式消费，否则运行期切换唤醒方式会永远等不到完成。
            if (lpMessage.Msg == WmApplyHotkey)
            {
                ApplyPendingHotkey();
                continue;
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

                if ((int)wParam == NativeMethods.QuickHotkeyId)
                {
                    AppLog.Debug($"快速切换热键到达: {QuickHotkey.Display}");
                    QuickHotkeyPressed?.Invoke();
                    return IntPtr.Zero;
                }

                break;

            case WmTimer:
                if ((int)(long)wParam == HookWatchdogTimerId)
                {
                    // 系统会在回调超时时静默移除低层钩子，这里周期性重装以保证唤醒方式始终有效。
                    if (_lpKeyboardHook?.Rearm() == false)
                    {
                        KeyboardHookRegistered = false;
                        KeyboardHookErrorCode = _lpKeyboardHook?.LastError ?? 0;
                        AppLog.Error($"低层键盘钩子已被系统移除且重装失败（Win32={KeyboardHookErrorCode}）。");
                    }

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

    private bool InvokeKeyInterceptor(int virtualKey, bool isDown, bool isInjected)
    {
        try
        {
            // 双击判定永远不吃键（只观察），因此不会影响 Ctrl+C/V、Ctrl+拖动、Ctrl+点击多选等正常操作。
            DetectDoubleTap(virtualKey, isDown);
        }
        catch
        {
            // 钩子回调里的异常绝不能外泄，否则会被系统摘钩。
        }

        var lpInterceptor = _lpKeyInterceptor;
        return lpInterceptor is not null && lpInterceptor(virtualKey, isDown, isInjected);
    }

    /// <summary>
    /// 双击修饰键判定（消息线程，必须极短）：
    ///  - 只认「按下后 400 ms 内松开」为一次 tap；按住不放/长按松开都不算；
    ///  - 两次 tap 间隔 ≤380 ms 才触发；
    ///  - 期间出现任何其它按键（Ctrl+C、Ctrl+V、Ctrl+拖动前的点击等）立即作废当前 tap，绝不误吞组合键。
    /// </summary>
    private void DetectDoubleTap(int virtualKey, bool isDown)
    {
        var lpSpec = Hotkey;
        if (lpSpec.Kind != HotkeyKind.DoubleTap || _lpTapVirtualKey == 0)
        {
            return;
        }

        var lpTapKey = (int)lpSpec.TapVirtualKey;
        var lpKey = NormalizeTapKey(virtualKey);
        if (lpKey != lpTapKey)
        {
            if (isDown && (_lpTapDown || _lpTapArmed))
            {
                // 同一个物理按键序列里出现了别的键：整段作废，绝不在 Ctrl+C 之后误判双击。
                if (_lpTapDown)
                {
                    _lpTapContaminated = true;
                }

                _lpTapArmed = false;
                _lpTapFirstUpTick = 0;
            }

            return;
        }

        var lpNow = Environment.TickCount64;
        if (isDown)
        {
            if (_lpTapDown)
            {
                // 自动重复：按住不放，不累计新的 tap。
                return;
            }

            _lpTapDown = true;
            _lpTapDownTick = lpNow;
            return;
        }

        _lpTapDown = false;
        var lpHoldMs = lpNow - _lpTapDownTick;
        if (_lpTapContaminated)
        {
            _lpTapContaminated = false;
            _lpTapArmed = false;
            _lpTapFirstUpTick = 0;
            return;
        }

        if (lpHoldMs > TapMaxHoldMs)
        {
            // 长按再松开不算一次 tap。
            _lpTapArmed = false;
            _lpTapFirstUpTick = 0;
            return;
        }

        if (_lpTapArmed && lpNow - _lpTapFirstUpTick <= TapWindowMs)
        {
            _lpTapArmed = false;
            _lpTapFirstUpTick = 0;
            var lpTick = Stopwatch.GetTimestamp();
            AppLog.Info($"双击唤醒触发: {lpSpec.Display}");
            HotkeyPressed?.Invoke(lpTick);
            return;
        }

        // 第一次 tap 或间隔已超时：重新开始计时，不作废按键本身。
        _lpTapArmed = true;
        _lpTapFirstUpTick = lpNow;
    }

    private uint _lpTapVirtualKey => Hotkey.Kind == HotkeyKind.DoubleTap ? Hotkey.TapVirtualKey : 0;

    /// <summary>
    /// 低层键盘钩子收到的是左右区分的虚拟键（VK_LCONTROL=0xA2、VK_RCONTROL=0xA3 等），
    /// 而预置项用的是通用键（0x11/0x12/0x10）。这里统一归一到通用键，否则永远匹配不上、双击静默失效。
    /// </summary>
    private static int NormalizeTapKey(int virtualKey)
    {
        return virtualKey switch
        {
            0xA2 or 0xA3 => 0x11, // 左/右 Ctrl
            0xA4 or 0xA5 => 0x12, // 左/右 Alt
            0xA0 or 0xA1 => 0x10, // 左/右 Shift
            _ => virtualKey
        };
    }

    private void ResetTapState()
    {
        _lpTapDown = false;
        _lpTapArmed = false;
        _lpTapContaminated = false;
        _lpTapFirstUpTick = 0;
        _lpTapDownTick = 0;
    }

    /// <summary>在消息线程应用待处理的唤醒方式；无论成败都用完成源回传原因，界面不静默失效。</summary>
    private void ApplyPendingHotkey()
    {
        (HotkeySpec Spec, TaskCompletionSource<HotkeyApplyResult> Completion)? lpPending;
        lock (_lpHotkeyGate)
        {
            lpPending = _lpPendingHotkey;
            _lpPendingHotkey = null;
        }

        if (lpPending is null)
        {
            return;
        }

        var lpSpec = lpPending.Value.Spec;
        var lpCompletion = lpPending.Value.Completion;
        var lpPrevious = Hotkey;
        try
        {
            if (lpPrevious.Kind == HotkeyKind.Chord && HotkeyRegistered)
            {
                NativeMethods.UnregisterHotKey(_lpHwnd, NativeMethods.HotkeyId);
            }

            HotkeyRegistered = false;
            HotkeyErrorCode = 0;
            ResetTapState();
            Hotkey = lpSpec;

            HotkeyApplyResult lpResult;
            if (lpSpec.Kind == HotkeyKind.Chord)
            {
                var lpOk = NativeMethods.RegisterHotKey(
                    _lpHwnd,
                    NativeMethods.HotkeyId,
                    lpSpec.Modifiers | NativeMethods.ModNoRepeat,
                    lpSpec.VirtualKey);
                if (!lpOk)
                {
                    HotkeyErrorCode = Marshal.GetLastWin32Error();
                }

                HotkeyRegistered = lpOk;
                lpResult = lpOk
                    ? HotkeyApplyResult.Success()
                    : HotkeyApplyResult.Fail(
                        HotkeyErrorCode,
                        HotkeyErrorCode == NativeMethods.ErrorHotkeyAlreadyRegistered
                            ? $"{lpSpec.Display} 已被其它程序占用"
                            : $"注册 {lpSpec.Display} 失败（Win32={HotkeyErrorCode}）");
            }
            else
            {
                if (_lpKeyboardHook?.Installed != true)
                {
                    _lpKeyboardHook?.Install();
                }

                KeyboardHookRegistered = _lpKeyboardHook?.Installed == true;
                KeyboardHookErrorCode = _lpKeyboardHook?.LastError ?? 0;
                HotkeyRegistered = KeyboardHookRegistered;
                HotkeyErrorCode = HotkeyRegistered ? 0 : KeyboardHookErrorCode;
                lpResult = HotkeyRegistered
                    ? HotkeyApplyResult.Success()
                    : HotkeyApplyResult.Fail(
                        HotkeyErrorCode,
                        $"低层键盘钩子不可用（Win32={HotkeyErrorCode}），双击类唤醒无法工作，请换用组合键");
            }

            if (!lpResult.Ok)
            {
                // 新方式没生效时必须回滚，否则用户会直接失去唤醒能力。
                Hotkey = lpPrevious;
                ResetTapState();
                if (lpPrevious.Kind == HotkeyKind.Chord)
                {
                    HotkeyRegistered = NativeMethods.RegisterHotKey(
                        _lpHwnd,
                        NativeMethods.HotkeyId,
                        lpPrevious.Modifiers | NativeMethods.ModNoRepeat,
                        lpPrevious.VirtualKey);
                    if (!HotkeyRegistered)
                    {
                        HotkeyErrorCode = Marshal.GetLastWin32Error();
                    }
                }
                else
                {
                    HotkeyRegistered = _lpKeyboardHook?.Installed == true;
                }

                lpResult = new HotkeyApplyResult(
                    false,
                    lpResult.ErrorCode,
                    lpResult.Message + (HotkeyRegistered
                        ? $"；已回滚到 {lpPrevious.Display}"
                        : "；且上一方式也未能恢复，请从托盘菜单打开搜索"));
                AppLog.Error($"唤醒方式切换失败: {lpResult.Message}");
            }

            if (lpResult.Ok)
            {
                AppLog.Info(lpSpec.Kind == HotkeyKind.Chord
                    ? $"唤醒方式已切换: {lpSpec.Display}（RegisterHotKey）"
                    : $"唤醒方式已切换: {lpSpec.Display}（双击判定，窗口 {TapWindowMs} ms）");
            }

            lpCompletion.TrySetResult(lpResult);
        }
        catch (Exception ex)
        {
            AppLog.Error("应用唤醒方式时异常", ex);
            lpCompletion.TrySetResult(HotkeyApplyResult.Fail(-1, ex.Message));
        }
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

        try
        {
            TrayMenuRequested?.Invoke(lpPoint.X, lpPoint.Y);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"托盘菜单请求异常: {ex.Message}");
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
        private IntPtr _lpIcon;

        public NotifyIconHandle(IntPtr hwnd, string tip)
        {
            _lpHwnd = hwnd;
            _lpTip = tip.Length > 120 ? tip[..120] : tip;
            _lpIcon = AppIcons.LoadTrayIcon();
            _lpData = new NativeMethods.NotifyIconData
            {
                Size = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
                Hwnd = hwnd,
                Id = TrayIconId,
                Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
                CallbackMessage = NativeMethods.WmTrayCallback,
                Icon = _lpIcon,
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

        /// <summary>更新悬浮提示文字（切换唤醒方式后同步，避免托盘提示与界面不一致）。</summary>
        public void UpdateTip(string tip)
        {
            lock (_lpGate)
            {
                if (!Added)
                {
                    return;
                }

                var lpData = _lpData;
                lpData.Flags = NativeMethods.NifTip;
                lpData.Tip = tip.Length > 120 ? tip[..120] : tip;
                NativeMethods.Shell_NotifyIconW(NativeMethods.NimModify, ref lpData);
            }
        }

        public void Dispose()
        {
            lock (_lpGate)
            {
                if (Added)
                {
                    var lpData = _lpData;
                    NativeMethods.Shell_NotifyIconW(NativeMethods.NimDelete, ref lpData);
                    Added = false;
                }

                AppIcons.ReleaseIcon(_lpIcon);
                _lpIcon = IntPtr.Zero;
            }
        }
    }
}

/// <summary>唤醒方式切换结果：失败必须带回原因，供界面与日志同时提示。</summary>
internal sealed record HotkeyApplyResult(bool Ok, int ErrorCode, string Message)
{
    public static HotkeyApplyResult Success() => new(true, 0, "已生效");

    public static HotkeyApplyResult Fail(int errorCode, string message) => new(false, errorCode, message);
}
