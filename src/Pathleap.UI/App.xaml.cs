using System.Diagnostics;
using Microsoft.UI.Xaml;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Perf;
using Pathleap.UI.Platform;
using Pathleap.UI.Theme;
using Pathleap.UI.Update;

namespace Pathleap.UI;

public partial class App : Application
{
    private AppConfig _lpConfig = new();
    private MainWindow? _lpWindow;
    private NativeMessageHost? _lpNativeHost;
    private ServiceLauncher? _lpService;
    private SettingsWindow? _lpSettingsWindow;
    private UpdateWindow? _lpUpdateWindow;
    private readonly Dictionary<nint, QuickSwitchWindow> _lpQuickBars = new();
    private QuickSwitchWindow? _lpDetachedBar;
    private FileDialogWatcher? _lpDialogWatcher;
    private QuickSwitchWindow? _lpKeyboardBar;
    private TrayMenuWindow? _lpTrayMenu;
    private AboutWindow? _lpAboutWindow;
    private HotkeySpec _lpHotkey = HotkeySpec.Default;
    private HotkeySpec _lpQuickHotkey = HotkeySpec.DefaultQuick;
    private readonly CancellationTokenSource _lpCts = new();
    private readonly DateTimeOffset _lpStartedAt = DateTimeOffset.Now;
    private bool _lpBenchRunning;
    private bool _lpShuttingDown;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WinUI 3 不会自动安装 SynchronizationContext；若不安装，await 之后的续体会落到线程池线程，
        // 再调用 Window.Activate() 会抛 RPC_E_WRONG_THREAD (0x8001010E)。
        InstallUiSynchronizationContext();
        _ = InitializeAsync();
    }

    private static void InstallUiSynchronizationContext()
    {
        if (SynchronizationContext.Current is not null)
        {
            return;
        }

        var lpQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (lpQueue is null)
        {
            AppLog.Warn("未能取得 UI 线程的 DispatcherQueue，异步续体将可能不在 UI 线程。");
            return;
        }

        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(lpQueue));
    }

    private async Task InitializeAsync()
    {
        try
        {
            _lpConfig = ConfigStore.Load(AppPaths.ConfigFile);
            AppLog.SetMinimumLevel(ParseLevel(_lpConfig.LogLevel));
            AppPaths.EnsureDirectory(AppPaths.LogsRoot);
            AppPaths.EnsureDirectory(AppPaths.PerfRoot);

            AppLog.Info($"配置加载完成: hotkey={_lpConfig.UiHotkey}, startService={_lpConfig.StartServiceWithUi}, " +
                        $"hideOnDeactivate={_lpConfig.HideOnDeactivate}, benchSamples={_lpConfig.BenchSamples}");

            ThemeService.Instance.Load(_lpConfig, AppPaths.ConfigFile);

            _lpWindow = new MainWindow(_lpConfig);
            AppIcons.Apply(_lpWindow);
            _lpWindow.SettingsRequested += OpenSettings;
            _lpWindow.QuickSwitchRequested += OpenQuickSwitch;
            _lpWindow.UpdateRequested += OpenUpdate;
            _lpWindow.AboutRequested += OpenAbout;

            _lpNativeHost = new NativeMessageHost();
            WireNativeEvents();

            if (!HotkeySpec.TryParse(_lpConfig.UiHotkey, out var lpParsedHotkey, out var lpHotkeyError))
            {
                AppLog.Warn($"配置热键无效（{lpHotkeyError}），回退默认 {HotkeySpec.Default.Display}");
            }
            else
            {
                _lpHotkey = lpParsedHotkey;
            }

            if (!HotkeySpec.TryParse(_lpConfig.QuickSwitchHotkey, out var lpParsedQuick, out var lpQuickError))
            {
                AppLog.Warn($"快速切换热键无效（{lpQuickError}），回退默认 {HotkeySpec.DefaultQuick.Display}");
            }
            else
            {
                _lpQuickHotkey = lpParsedQuick;
            }

            // 两个唤醒热键不能撞车：Chord 完全相同时直接回退默认，避免其中一个静默失效。
            if (_lpHotkey.SameAs(_lpQuickHotkey))
            {
                AppLog.Warn($"唤醒方式与快速切换热键冲突（同为 {_lpHotkey.Display}），唤醒方式回退默认 {HotkeySpec.Default.Display}");
                _lpHotkey = HotkeySpec.Default;
            }

            var lpTooltip = $"{Branding.DisplayName}（{Branding.ChineseName}） · {_lpHotkey.Display}";
            var lpStarted = await Task.Run(() => _lpNativeHost.Start(_lpHotkey, _lpQuickHotkey, lpTooltip));
            if (!lpStarted)
            {
                AppLog.Error("原生消息线程启动超时，托盘与热键可能不可用。");
            }

            StartIpc();
            _ = EnsureServiceAsync();

            var lpScreenMode = !string.IsNullOrEmpty(Program.Startup.Screen);
            if (!_lpNativeHost.HotkeyRegistered)
            {
                var lpMessage = _lpHotkey.IsDoubleTap
                    ? $"双击唤醒 {_lpHotkey.Display} 不可用（Win32={_lpNativeHost.HotkeyErrorCode}）：低层键盘钩子未能安装，" +
                      "常见于安全软件拦截或权限不足。请从托盘菜单打开搜索，或在设置「快捷键」里改用组合键。"
                    : $"热键 {_lpHotkey.Display} 注册失败（Win32={_lpNativeHost.HotkeyErrorCode}）。" +
                      "请从托盘菜单打开搜索，或在设置「快捷键」里换用其它预置方式。";
                AppLog.Warn(lpMessage);
                _lpNativeHost.ShowTrayWarning($"{Branding.DisplayName} 唤醒方式不可用", lpMessage);
                if (!Program.Startup.AutoBench && !lpScreenMode)
                {
                    _lpWindow.ShowFromCommand();
                }
            }
            else if (Program.Startup.ShowOnStart && !lpScreenMode)
            {
                _lpWindow.ShowFromCommand();
            }

            if (!_lpNativeHost.QuickHotkeyRegistered)
            {
                var lpQuickMessage = $"快速切换热键 {_lpQuickHotkey.Display} 注册失败（Win32={_lpNativeHost.QuickHotkeyErrorCode}），" +
                                     "可从主窗口打开快速切换，或修改配置中的 quickSwitchHotkey。";
                AppLog.Warn(lpQuickMessage);
                _lpNativeHost.ShowTrayWarning($"{Branding.DisplayName} 快速切换热键不可用", lpQuickMessage);
            }

            StartDialogWatcher();
            _lpNativeHost.KeyInterceptor = InterceptQuickBarKey;

            if (lpScreenMode)
            {
                await ShowScreenAsync(Program.Startup.Screen);
                return;
            }

            if (Program.Startup.AutoBench)
            {
                if (!Program.Startup.AllowForeground)
                {
                    // 唤醒基准会反复把窗口切到前台，未获显式许可时不启动。
                    AppLog.Warn("拒绝自动基准：未声明 --allow-foreground（该基准会反复占用前台）。");
                    Shutdown(3);
                    return;
                }

                // 给服务/托盘一点初始化时间，再跑基准；结果落盘后退出。
                await Task.Delay(800);
                var lpSamples = Program.Startup.BenchSamples > 0
                    ? Program.Startup.BenchSamples
                    : _lpConfig.BenchSamples;
                var lpReply = await RunBenchmarkAsync(lpSamples);
                Shutdown(lpReply.Ok ? 0 : 5);
            }

            _ = RunAutoUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("UI 初始化失败", ex);
            Shutdown(1);
        }
    }

    private void WireNativeEvents()
    {
        if (_lpNativeHost is null)
        {
            return;
        }

        _lpNativeHost.HotkeyPressed += tick =>
            _lpWindow?.UiDispatcher.TryEnqueue(() => _lpWindow.ShowFromHotkey(tick));

        _lpNativeHost.QuickHotkeyPressed += () => Dispatch(OpenQuickSwitch);

        _lpNativeHost.ShowRequested += () => Dispatch(() => _lpWindow?.ShowFromCommand());

        _lpNativeHost.TrayMenuRequested += (x, y) => Dispatch(() => OpenTrayMenu(x, y));
    }

    private async Task RunBenchmarkAndNotifyAsync()
    {
        var lpReply = await RunBenchmarkAsync(_lpConfig.BenchSamples);
        if (lpReply.Ok)
        {
            _lpNativeHost?.ShowTrayInfo("唤醒基准完成", lpReply.Message);
        }
        else
        {
            _lpNativeHost?.ShowTrayWarning("唤醒基准失败", lpReply.Message);
        }
    }

    private async Task EnsureServiceAsync()
    {
        if (_lpService is null)
        {
            return;
        }

        await _lpService.EnsureRunningAsync(_lpConfig.StartServiceWithUi, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Dispatch(() => _lpWindow?.SetServiceStatus(_lpService.StatusText, _lpService.Connected));
    }

    private void StartIpc()
    {
        _lpService = new ServiceLauncher();
        _ = Task.Run(() => PipeServer.RunAsync(Branding.UiPipeName, HandleIpcAsync, _lpCts.Token));
        AppLog.Info($"UI 控制管道已启动: {Branding.UiPipeName}");
    }

    private Task<IpcReply> HandleIpcAsync(IpcRequest request)
    {
        switch (request.Command.ToUpperInvariant())
        {
            case "PING":
                return Task.FromResult(IpcReply.Success("pong"));

            case "SHOW":
                Dispatch(() => _lpWindow?.ShowFromCommand());
                return Task.FromResult(IpcReply.Success("showing"));

            case "STATUS":
                return Task.FromResult(IpcReply.Success(
                    $"{Branding.DisplayName} {Branding.Version} PID={Environment.ProcessId} " +
                    $"uptime={(long)(DateTimeOffset.Now - _lpStartedAt).TotalMilliseconds}ms " +
                    $"hotkey={_lpHotkey.Display} registered={_lpNativeHost?.HotkeyRegistered} " +
                    $"service={_lpService?.StatusText} benchRunning={_lpBenchRunning}"));

            case "BENCH":
                if (!request.AllowForeground)
                {
                    return Task.FromResult<IpcReply>(IpcReply.Failure("拒绝 BENCH：调用方未声明占用前台（需 allowForeground=true）。"));
                }

                return DispatchToUiAsync(() => RunBenchmarkAsync(request.Samples));

            // 自动化入口：切换唤醒方式（与设置页点击走同一条 ApplyHotkeyAsync）。
            case "HOTKEY":
            {
                if (!HotkeySpec.TryParse(request.Query, out var lpSpec, out var lpSpecError))
                {
                    return Task.FromResult<IpcReply>(IpcReply.Failure($"唤醒方式无效: {lpSpecError}"));
                }

                return DispatchToUiAsync(async () =>
                {
                    await ApplyHotkeyAsync(lpSpec);
                    var lpOk = _lpNativeHost?.HotkeyRegistered == true;
                    return lpOk
                        ? IpcReply.Success($"已切换到 {lpSpec.Display}")
                        : IpcReply.Failure($"切换后未生效: {lpSpec.Display}");
                });
            }

            // 后台验证入口：等价于在搜索框输入文本并等结果回来，不注入真实键鼠。
            case "QUERY":
            {
                var lpWindow = _lpWindow;
                if (lpWindow is null)
                {
                    return Task.FromResult<IpcReply>(IpcReply.Failure("搜索窗尚未就绪。"));
                }

                return DispatchToUiAsync(() => lpWindow.QueryForTestAsync(request.Query));
            }

            // 后台验证入口：等价于按 ↑/↓ 移动结果选择。
            case "NAV":
            {
                var lpWindow = _lpWindow;
                if (lpWindow is null)
                {
                    return Task.FromResult<IpcReply>(IpcReply.Failure("搜索窗尚未就绪。"));
                }

                return DispatchToUiAsync(() => Task.FromResult(lpWindow.SelectForTest(request.Samples)));
            }

            case "QUIT":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    _lpWindow?.UiDispatcher.TryEnqueue(() => Shutdown(0));
                });
                return Task.FromResult(IpcReply.Success("quitting"));

            default:
                return Task.FromResult(IpcReply.Failure($"未知命令: {request.Command}"));
        }
    }

    private async Task<IpcReply> RunBenchmarkAsync(int samples)
    {
        if (_lpBenchRunning)
        {
            return IpcReply.Failure("基准测试正在运行，请稍后。");
        }

        if (_lpWindow is null || _lpNativeHost is null || !_lpNativeHost.HotkeyRegistered)
        {
            return IpcReply.Failure("全局热键未注册，无法进行唤醒基准。");
        }

        if (samples <= 0)
        {
            samples = _lpConfig.BenchSamples;
        }

        _lpBenchRunning = true;
        try
        {
            var (lpSamples, lpStartedAt, lpFinishedAt) = await WakeBenchmark.RunAsync(
                _lpWindow,
                _lpHotkey,
                _lpConfig.BenchProbeKey,
                samples,
                _lpCts.Token);

            var lpSummary = PerfReport.Build(
                lpSamples,
                _lpHotkey.Display,
                _lpConfig.BenchProbeKey,
                lpStartedAt,
                lpFinishedAt);
            PerfReport.Write(lpSummary);

            AppLog.Info($"基准摘要: {lpSummary.Verdict}");
            AppLog.Info($"CSV: {lpSummary.CsvPath}");
            AppLog.Info($"JSON: {lpSummary.JsonPath}");

            return new IpcReply
            {
                Ok = true,
                Message = lpSummary.Verdict,
                SummaryPath = lpSummary.JsonPath,
                P50Ms = lpSummary.InputReady.P50,
                P95Ms = lpSummary.InputReady.P95
            };
        }
        catch (OperationCanceledException)
        {
            return IpcReply.Failure("基准测试被取消。");
        }
        catch (Exception ex)
        {
            AppLog.Error("基准测试执行失败", ex);
            return IpcReply.Failure($"基准测试失败: {ex.Message}");
        }
        finally
        {
            _lpBenchRunning = false;
        }
    }

    private Task<IpcReply> DispatchToUiAsync(Func<Task<IpcReply>> action)
    {
        var lpCompletion = new TaskCompletionSource<IpcReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lpQueued = _lpWindow?.UiDispatcher.TryEnqueue(async () =>
        {
            try
            {
                lpCompletion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                AppLog.Error("UI 任务失败", ex);
                lpCompletion.TrySetResult(IpcReply.Failure(ex.Message));
            }
        }) ?? false;

        if (!lpQueued)
        {
            lpCompletion.TrySetResult(IpcReply.Failure("UI 线程不可用"));
        }

        return lpCompletion.Task;
    }

    private void Dispatch(Action action)
    {
        if (_lpShuttingDown)
        {
            return;
        }

        var lpWindow = _lpWindow;
        if (lpWindow is null)
        {
            return;
        }

        lpWindow.UiDispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error("UI 调度任务异常", ex);
            }
        });
    }

    private void OpenSettings() => OpenSettings("appearance");

    private void OpenSettings(string page)
    {
        if (_lpSettingsWindow is not null)
        {
            _lpSettingsWindow.ShowPageForTest(page);
            _lpSettingsWindow.Activate();
            return;
        }

        var lpWindow = new SettingsWindow(_lpConfig);
        AppIcons.Apply(lpWindow);
        lpWindow.UpdateRequested += OpenUpdate;
        lpWindow.HotkeyRequested += spec => _ = ApplyHotkeyAsync(spec);
        lpWindow.Closed += (_, _) =>
        {
            lpWindow.PrepareShutdown();
            _lpSettingsWindow = null;
        };

        _lpSettingsWindow = lpWindow;
        lpWindow.ShowPageForTest(page);
        lpWindow.Activate();
        AppLog.Info($"设置窗已打开: page={page}");
    }

    /// <summary>
    /// 设置页选中预置唤醒方式后立即生效：先校验与快速切换热键冲突，再在消息线程重注册；
    /// 失败时运行态已回滚，界面与日志同时给出原因，不做静默失效。
    /// </summary>
    private async Task ApplyHotkeyAsync(HotkeySpec spec)
    {
        var lpHost = _lpNativeHost;
        if (lpHost is null)
        {
            _lpSettingsWindow?.ReportHotkeyResult(spec, false, "原生消息线程尚未启动");
            return;
        }

        if (spec.SameAs(_lpQuickHotkey))
        {
            var lpConflict = $"{spec.Display} 与快速切换热键 {_lpQuickHotkey.Display} 冲突，请选择其它方式";
            AppLog.Warn(lpConflict);
            _lpSettingsWindow?.ReportHotkeyResult(spec, false, lpConflict);
            return;
        }

        var lpResult = await lpHost.ChangeHotkeyAsync(spec);
        if (lpResult.Ok)
        {
            _lpHotkey = spec;
            _lpConfig.UiHotkey = spec.Text;
            ThemeService.Instance.SaveConfig();
            lpHost.UpdateTrayTooltip($"{Branding.DisplayName}（{Branding.ChineseName}） · {spec.Display}");
            AppLog.Info($"唤醒方式已保存: {spec.Text}");
        }
        else
        {
            AppLog.Error($"唤醒方式未生效: {spec.Display} · {lpResult.Message}");
            lpHost.ShowTrayWarning($"{Branding.DisplayName} 唤醒方式切换失败", lpResult.Message);
        }

        _lpSettingsWindow?.ReportHotkeyResult(spec, lpResult.Ok, lpResult.Message);
    }

    private void OpenUpdate() => OpenUpdate(string.Empty);

    private void OpenUpdate(string previewState)
    {
        if (_lpUpdateWindow is not null)
        {
            _lpUpdateWindow.Activate();
            if (!string.IsNullOrEmpty(previewState))
            {
                _lpUpdateWindow.ShowPreview(previewState);
            }

            return;
        }

        var lpWindow = new UpdateWindow(_lpConfig);
        AppIcons.Apply(lpWindow);
        lpWindow.Closed += (_, _) =>
        {
            lpWindow.PrepareShutdown();
            _lpUpdateWindow = null;
        };

        _lpUpdateWindow = lpWindow;
        if (!string.IsNullOrEmpty(previewState))
        {
            lpWindow.ShowPreview(previewState);
        }

        lpWindow.Activate();
        AppLog.Info($"更新窗已打开: state={(string.IsNullOrEmpty(previewState) ? "idle" : previewState)}");
    }

    private void OpenQuickSwitch()
    {
        // 必须赶在栏拿走前台之前记录对话框；Ctrl+G 触发时前台仍是用户的打开/保存对话框。
        var lpForeground = FileDialogInjector.CaptureActive();
        var lpDialogs = FileDialogInjector.EnumerateFileDialogs();

        if (lpDialogs.Count == 0)
        {
            ShowDetachedQuickBar();
            AppLog.Info("快速切换栏已以预览形态打开（未检测到文件对话框）。");
            RunQuickPick();
            return;
        }

        // 每个对话框各自一条栏，互不叠加；Ctrl+G 只激活当前前台对话框对应的那条。
        QuickSwitchWindow? lpTarget = null;
        foreach (var lpDialog in lpDialogs)
        {
            var lpBar = EnsureQuickBar(lpDialog);
            if (lpForeground.IsFileDialog && lpDialog.Handle == lpForeground.Handle)
            {
                lpTarget = lpBar;
            }
        }

        lpTarget ??= _lpQuickBars.TryGetValue(lpDialogs[0].Handle, out var lpFirst) ? lpFirst : null;
        if (lpTarget is not null)
        {
            ActivateQuickBar(lpTarget);
        }

        AppLog.Info($"快速切换栏已打开（对话框 {lpDialogs.Count} 个，当前={(lpForeground.IsFileDialog ? lpForeground.Kind : "未知")}）。");
        RunQuickPick();
    }

    private void RunQuickPick()
    {
        if (Program.Startup.QuickPick <= 0)
        {
            return;
        }

        var lpIndex = Program.Startup.QuickPick - 1;
        var lpBar = Volatile.Read(ref _lpKeyboardBar) ?? _lpDetachedBar;
        lpBar?.DispatcherQueue.TryEnqueue(() => lpBar.PickByIndex(lpIndex));
    }

    private QuickSwitchWindow EnsureQuickBar(FileDialogInfo dialog)
    {
        _lpDialogWatcher?.Track(dialog.Handle);

        if (_lpQuickBars.TryGetValue(dialog.Handle, out var lpExisting))
        {
            lpExisting.Attach(dialog);
            return lpExisting;
        }

        var lpWindow = new QuickSwitchWindow(_lpConfig);
        AppIcons.Apply(lpWindow);
        _lpQuickBars[dialog.Handle] = lpWindow;
        lpWindow.KeyboardEnded += () =>
            Dispatch(() =>
            {
                if (Volatile.Read(ref _lpKeyboardBar) == lpWindow)
                {
                    Volatile.Write(ref _lpKeyboardBar, null);
                }
            });
        lpWindow.BarClosed += () =>
            Dispatch(() => RemoveQuickBar(dialog.Handle, lpWindow));
        lpWindow.Attach(dialog);
        AppLog.Info($"快速切换栏已挂到对话框: kind={dialog.Kind} handle=0x{dialog.Handle.ToInt64():X}");
        return lpWindow;
    }

    private void RemoveQuickBar(IntPtr handle, QuickSwitchWindow bar)
    {
        if (_lpQuickBars.TryGetValue(handle, out var lpCurrent) && lpCurrent == bar)
        {
            _lpQuickBars.Remove(handle);
        }

        if (Volatile.Read(ref _lpKeyboardBar) == bar)
        {
            Volatile.Write(ref _lpKeyboardBar, null);
        }

        bar.CloseSafely();
    }

    private void ShowDetachedQuickBar()
    {
        if (_lpDetachedBar is null)
        {
            _lpDetachedBar = new QuickSwitchWindow(_lpConfig);
            _lpDetachedBar.BarClosed += () => Dispatch(() => _lpDetachedBar = null);
        }

        var lpHwnd = _lpWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_lpWindow);
        _lpDetachedBar.ShowDetached(lpHwnd);
    }

    private void StartDialogWatcher()
    {
        if (_lpDialogWatcher is not null)
        {
            return;
        }

        _lpDialogWatcher = new FileDialogWatcher();
        _lpDialogWatcher.DialogAppeared += info =>
        {
            var lpBar = EnsureQuickBar(info);
            lpBar.SetForegroundActive(NativeMethods.GetForegroundWindow() == info.Handle);
        };
        _lpDialogWatcher.DialogDestroyed += handle =>
        {
            if (_lpQuickBars.TryGetValue(handle, out var lpBar))
            {
                RemoveQuickBar(handle, lpBar);
            }
        };
        _lpDialogWatcher.DialogGeometryChanged += handle =>
        {
            if (_lpQuickBars.TryGetValue(handle, out var lpBar))
            {
                lpBar.Reposition();
            }
        };
        _lpDialogWatcher.ForegroundChanged += handle =>
        {
            PruneDeadBars();
            SyncBarVisibility(handle);
            UpdateKeyboardBarForForeground(handle);
            // 宿主回到前台时系统会把 owned 栏抬到宿主之上，这里立刻压回宿主之下，
            // 保证栏始终与对话框同层，不会越过盖住对话框的其它窗口。
            foreach (var lpBar in _lpQuickBars.Values)
            {
                if (lpBar.OwnerHandle == handle)
                {
                    lpBar.ReorderAboveOwner();
                    lpBar.ReorderAboveOwnerDelayed();
                    break;
                }
            }
        };

        // 启动前已经打开的文件对话框也要补上栏。
        foreach (var lpDialog in FileDialogInjector.EnumerateFileDialogs())
        {
            _lpDialogWatcher.Track(lpDialog.Handle);
            EnsureQuickBar(lpDialog);
        }

        SyncBarVisibility(NativeMethods.GetForegroundWindow());
    }

    /// <summary>只让前台对话框那条栏可见：互叠时后台栏不会压在其它对话框上。</summary>
    private void SyncBarVisibility(IntPtr foreground)
    {
        if (_lpQuickBars.Count == 0)
        {
            return;
        }

        foreach (var lpPair in _lpQuickBars)
        {
            // 前台已经落在栏自身（用户点进搜索框）时，它同样算活跃；否则会被误判为后台而隐藏。
            var lpActive = lpPair.Key == foreground
                           || (foreground != IntPtr.Zero && lpPair.Value.Handle == foreground);
            lpPair.Value.SetForegroundActive(lpActive);
        }
    }

    /// <summary>兜底清理：对话框进程异常退出、没收到销毁事件时，前台变化时顺手把死栏撤掉。</summary>
    private void PruneDeadBars()
    {
        if (_lpQuickBars.Count == 0)
        {
            return;
        }

        var lpDead = new List<nint>();
        foreach (var lpPair in _lpQuickBars)
        {
            if (!QuickSwitchNative.IsWindow(lpPair.Key))
            {
                lpDead.Add(lpPair.Key);
            }
        }

        foreach (var lpHandle in lpDead)
        {
            AppLog.Info($"快速切换栏兜底清理: dialog=0x{lpHandle.ToInt64():X} 已不存在");
            RemoveQuickBar(lpHandle, _lpQuickBars[lpHandle]);
        }
    }

    /// <summary>只有当前对话框对应的那条栏响应键盘；前台切到非对话框窗口时立即停止拦截。</summary>
    private void UpdateKeyboardBarForForeground(IntPtr foreground)
    {
        var lpBar = Volatile.Read(ref _lpKeyboardBar);
        if (lpBar is null)
        {
            return;
        }

        if (foreground == lpBar.DialogHandle || foreground == lpBar.Handle)
        {
            return;
        }

        if (foreground != IntPtr.Zero && _lpQuickBars.TryGetValue(foreground, out var lpNext))
        {
            ActivateQuickBar(lpNext);
            return;
        }

        Volatile.Write(ref _lpKeyboardBar, null);
        lpBar.SetKeyboardActive(false);
    }

    private void ActivateQuickBar(QuickSwitchWindow bar)
    {
        var lpPrevious = Volatile.Read(ref _lpKeyboardBar);
        if (lpPrevious != bar)
        {
            lpPrevious?.SetKeyboardActive(false);
        }

        Volatile.Write(ref _lpKeyboardBar, bar);
        bar.SetKeyboardActive(true);
    }

    /// <summary>
    /// 低层键盘钩子回调（原生消息线程）：仅当栏处于键盘模式且对应对话框仍在前台时，
    /// 吞掉 1–6 / Enter / Esc / ↑↓，并把动作投回 UI 线程。回调里不做任何耗时工作。
    /// 这里不排除注入按键：远程桌面、输入法/辅助工具常常以注入方式送键，
    /// 而该路径已被「显式激活 + 对话框在前台」两重条件限定，误吞风险可忽略。
    /// </summary>
    private bool InterceptQuickBarKey(int virtualKey, bool isDown, bool isInjected)
    {
        if (!isDown)
        {
            return false;
        }

        var lpBar = Volatile.Read(ref _lpKeyboardBar);
        var lpWindow = _lpWindow;
        if (lpBar is null || lpWindow is null)
        {
            return false;
        }

        var lpDialog = lpBar.DialogHandle;
        if (lpDialog == IntPtr.Zero || NativeMethods.GetForegroundWindow() != lpDialog)
        {
            return false;
        }

        var lpIsDigit = virtualKey is >= 0x31 and <= 0x36 || virtualKey is >= 0x61 and <= 0x66;
        if (!lpIsDigit && virtualKey is not (0x1B or 0x0D or 0x26 or 0x28))
        {
            return false;
        }

        lpWindow.UiDispatcher.TryEnqueue(() => lpBar.HandleKey(virtualKey));
        return true;
    }

    private void OpenAbout()
    {
        if (_lpAboutWindow is not null)
        {
            _lpAboutWindow.Activate();
            return;
        }

        var lpWindow = new AboutWindow();
        AppIcons.Apply(lpWindow);
        lpWindow.Closed += (_, _) => _lpAboutWindow = null;
        _lpAboutWindow = lpWindow;
        lpWindow.Activate();
        AppLog.Info("关于窗已打开");
    }

    private void OpenTrayMenu(int screenX, int screenY)
    {
        if (_lpTrayMenu is null)
        {
            var lpMenu = new TrayMenuWindow(_lpHotkey.Display);
            lpMenu.ShowSearchRequested += () => _lpWindow?.UiDispatcher.TryEnqueue(() => _lpWindow.ShowFromCommand());
            lpMenu.SettingsRequested += () => Dispatch(OpenSettings);
            lpMenu.UpdateRequested += () => Dispatch(OpenUpdate);
            lpMenu.AboutRequested += () => Dispatch(OpenAbout);
            lpMenu.ExitRequested += () => Dispatch(() => Shutdown(0));
            lpMenu.Closed += (_, _) =>
            {
                lpMenu.PrepareShutdown();
                _lpTrayMenu = null;
            };
            _lpTrayMenu = lpMenu;
        }

        _lpTrayMenu.ShowAt(screenX, screenY);
    }

    private async Task RunAutoUpdateCheckAsync()
    {
        try
        {
            if (!_lpConfig.AutoCheckUpdates || _lpConfig.UpdateCheckHours <= 0)
            {
                AppLog.Info("自动检查更新已关闭（autoCheckUpdates=false 或仅手动）。");
                return;
            }

            var lpLast = DateTimeOffset.MinValue;
            if (!string.IsNullOrWhiteSpace(_lpConfig.LastUpdateCheck))
            {
                try
                {
                    lpLast = DateTimeOffset.Parse(_lpConfig.LastUpdateCheck);
                }
                catch
                {
                    lpLast = DateTimeOffset.MinValue;
                }
            }

            if (lpLast != DateTimeOffset.MinValue
                && (DateTimeOffset.Now - lpLast).TotalHours < _lpConfig.UpdateCheckHours)
            {
                AppLog.Info($"距上次检查更新未满 {_lpConfig.UpdateCheckHours} 小时，跳过。");
                return;
            }

            // 让托盘与服务先稳定，不必和启动流程抢时间。
            await Task.Delay(TimeSpan.FromSeconds(6), _lpCts.Token);
            var lpResult = await UpdateService.CheckAsync(_lpCts.Token);
            _lpConfig.LastUpdateCheck = DateTimeOffset.Now.ToString("O");
            ThemeService.Instance.SaveConfig();

            if (!lpResult.Ok)
            {
                // 自动检查失败不静默：日志 + 托盘提示；不谎报有更新。
                AppLog.Warn($"自动检查更新未完成: {lpResult.Message}");
                _lpNativeHost?.ShowTrayInfo("更新检查未完成", lpResult.Message);
                return;
            }

            if (!lpResult.HasUpdate)
            {
                AppLog.Info($"自动检查更新: {lpResult.Message}");
                return;
            }

            AppLog.Info($"自动检查更新发现新版本 {lpResult.RemoteVersion}");
            _lpNativeHost?.ShowTrayInfo(
                $"{Branding.DisplayName} {lpResult.RemoteVersion} 可用",
                "右键托盘图标，选择「检查更新」查看版本说明与下载。");
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，无需记录。
        }
        catch (Exception ex)
        {
            AppLog.Warn($"自动检查更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 截图模式：按 --screen 打开对应界面并保持，供外部抓图。绝不改动真实数据。
    /// </summary>
    private async Task ShowScreenAsync(string screen)
    {
        AppLog.Info($"截图模式: {screen}, query={Program.Startup.Query}");
        switch (screen.ToLowerInvariant())
        {
            case "recent":
                _lpWindow?.ShowFromCommand();
                break;

            case "results":
                _lpWindow?.ShowFromCommand();
                if (_lpWindow is not null)
                {
                    var lpQuery = string.IsNullOrWhiteSpace(Program.Startup.Query)
                        ? "pathleap"
                        : Program.Startup.Query;
                    await _lpWindow.QueryForTestAsync(lpQuery);
                }

                break;

            case "quick":
                OpenQuickSwitch();
                if (!string.IsNullOrEmpty(Program.Startup.QuickType) || Program.Startup.QuickFocus || Program.Startup.QuickSkipGuide)
                {
                    await Task.Delay(700);
                    var lpQuickBar = Volatile.Read(ref _lpKeyboardBar) ?? _lpDetachedBar;
                    if (Program.Startup.QuickSkipGuide)
                    {
                        lpQuickBar?.DismissGuideForTest();
                        await Task.Delay(400);
                    }
                    else if (!string.IsNullOrEmpty(Program.Startup.QuickType))
                    {
                        lpQuickBar?.TypeForTest(Program.Startup.QuickType);
                    }
                    else
                    {
                        lpQuickBar?.FocusForTest();
                    }
                }

                break;

            case "settings":
            case "settings-appearance":
                OpenSettings("appearance");
                break;

            case "settings-shortcuts":
                OpenSettings("shortcuts");
                break;

            case "settings-scope":
                OpenSettings("scope");
                break;

            case "settings-update":
                OpenSettings("update");
                break;

            case "settings-help":
                OpenSettings("help");
                break;

            case "update":
                OpenUpdate(string.Empty);
                break;

            case "update-found":
                OpenUpdate("found");
                break;

            case "update-downloading":
                OpenUpdate("downloading");
                break;

            case "update-downloaded":
                OpenUpdate("downloaded");
                break;

            case "about":
                OpenAbout();
                break;

            case "tray":
            {
                var lpWork = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                OpenTrayMenu(lpWork.X + lpWork.Width - 24, lpWork.Y + lpWork.Height - 24);
                break;
            }

            default:
                AppLog.Warn($"未知截图界面: {screen}");
                break;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            AppPaths.EnsureDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开目录失败 {path}: {ex.Message}");
        }
    }

    private void Shutdown(int exitCode)
    {
        if (_lpShuttingDown)
        {
            return;
        }

        _lpShuttingDown = true;
        AppLog.Info($"准备退出，exitCode={exitCode}");
        try
        {
            _lpCts.Cancel();
        }
        catch (Exception)
        {
            // 取消阶段的异常不影响退出。
        }

        try
        {
            _lpNativeHost?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"释放托盘/热键失败: {ex.Message}");
        }

        try
        {
            _lpService?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止服务失败: {ex.Message}");
        }

        try
        {
            _lpDialogWatcher?.Dispose();
            _lpDialogWatcher = null;
            foreach (var lpBar in _lpQuickBars.Values)
            {
                lpBar.CloseSafely();
            }

            _lpQuickBars.Clear();
            Volatile.Write(ref _lpKeyboardBar, null);
            _lpDetachedBar?.CloseSafely();
            _lpDetachedBar = null;
            _lpTrayMenu?.PrepareShutdown();
            _lpTrayMenu?.Close();
            _lpSettingsWindow?.PrepareShutdown();
            _lpSettingsWindow?.Close();
            _lpUpdateWindow?.PrepareShutdown();
            _lpUpdateWindow?.Close();
            _lpAboutWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"关闭辅助窗口失败: {ex.Message}");
        }

        try
        {
            _lpWindow?.PrepareShutdown();
            _lpWindow?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"关闭窗口失败: {ex.Message}");
        }

        AppLog.Info("清理完成");
        AppLog.Shutdown();
        Environment.Exit(exitCode);
    }

    private static LogLevel ParseLevel(string level) => level switch
    {
        "Debug" => LogLevel.Debug,
        "Warn" => LogLevel.Warn,
        "Error" => LogLevel.Error,
        _ => LogLevel.Info
    };
}

/// <summary>
/// 把 async 续体投递回 WinUI 的 DispatcherQueue。
/// Windows App SDK 2.6.5 的 Microsoft.WinUI.dll 中不包含 DispatcherQueueSynchronizationContext，
/// 因此需要自行实现一个最小版本。
/// </summary>
internal sealed class UiSynchronizationContext : SynchronizationContext
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _lpQueue;

    public UiSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        _lpQueue = queue;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (!_lpQueue.TryEnqueue(() => d(state)))
        {
            AppLog.Warn("DispatcherQueue 已关闭，异步续体丢弃。");
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_lpQueue.HasThreadAccess)
        {
            d(state);
            return;
        }

        using var lpDone = new ManualResetEventSlim(false);
        Exception? lpError = null;
        _lpQueue.TryEnqueue(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                lpError = ex;
            }
            finally
            {
                lpDone.Set();
            }
        });
        lpDone.Wait(TimeSpan.FromSeconds(10));
        if (lpError is not null)
        {
            throw lpError;
        }
    }

    public override SynchronizationContext CreateCopy() => this;
}
