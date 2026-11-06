using System.Diagnostics;
using Microsoft.UI.Xaml;
using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.UI.Perf;
using Pathleap.UI.Platform;

namespace Pathleap.UI;

public partial class App : Application
{
    private AppConfig _lpConfig = new();
    private MainWindow? _lpWindow;
    private NativeMessageHost? _lpNativeHost;
    private ServiceLauncher? _lpService;
    private HotkeySpec _lpHotkey = HotkeySpec.Default;
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

            _lpWindow = new MainWindow(_lpConfig);

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

            var lpTooltip = $"{Branding.DisplayName}（{Branding.ChineseName}） · {_lpHotkey.Display} · M1";
            var lpStarted = await Task.Run(() => _lpNativeHost.Start(_lpHotkey, lpTooltip));
            if (!lpStarted)
            {
                AppLog.Error("原生消息线程启动超时，托盘与热键可能不可用。");
            }

            StartIpc();
            _ = EnsureServiceAsync();

            if (!_lpNativeHost.HotkeyRegistered)
            {
                var lpMessage = $"热键 {_lpHotkey.Display} 注册失败（Win32={_lpNativeHost.HotkeyErrorCode}）。" +
                                "请从托盘菜单打开搜索，或修改配置中的 uiHotkey。";
                AppLog.Warn(lpMessage);
                _lpNativeHost.ShowTrayWarning($"{Branding.DisplayName} 热键不可用", lpMessage);
                if (!Program.Startup.AutoBench)
                {
                    _lpWindow.ShowFromCommand();
                }
            }
            else if (Program.Startup.ShowOnStart)
            {
                _lpWindow.ShowFromCommand();
            }

            if (Program.Startup.AutoBench)
            {
                // 给服务/托盘一点初始化时间，再跑基准；结果落盘后退出。
                await Task.Delay(800);
                var lpSamples = Program.Startup.BenchSamples > 0
                    ? Program.Startup.BenchSamples
                    : _lpConfig.BenchSamples;
                var lpReply = await RunBenchmarkAsync(lpSamples);
                Shutdown(lpReply.Ok ? 0 : 5);
            }
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

        _lpNativeHost.ShowRequested += () => Dispatch(() => _lpWindow?.ShowFromCommand());

        _lpNativeHost.BenchRequested += () => Dispatch(() =>
        {
            _ = RunBenchmarkAndNotifyAsync();
        });

        _lpNativeHost.OpenConfigRequested += () => OpenFolder(AppPaths.UserRoot);

        _lpNativeHost.OpenLogRequested += () => OpenFolder(AppPaths.LogsRoot);

        _lpNativeHost.ExitRequested += () => Dispatch(() => Shutdown(0));
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
                return DispatchToUiAsync(() => RunBenchmarkAsync(request.Samples));

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
