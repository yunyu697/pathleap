using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        AppLog.Initialize("service");
        var lpIsService = Array.Exists(args, static a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
        var lpMode = lpIsService ? "service" : "console";
        AppLog.Info($"{Branding.DisplayName} Service {Branding.Version} 启动，mode={lpMode}");
        AppLog.Info($"日志目录: {AppLog.LogDirectory}");

        if (lpIsService)
        {
            return WindowsServiceHost.Run(Branding.ServiceName);
        }

        return RunConsole();
    }

    private static int RunConsole()
    {
        using var lpMutex = new Mutex(initiallyOwned: true, Branding.ServiceMutexName, out var lpCreatedNew);
        if (!lpCreatedNew)
        {
            AppLog.Warn("已有服务实例在运行，本次退出。");
            return 3;
        }

        using var lpRuntime = new ServiceRuntime("console");
        var lpStopRequested = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, lpEvent) =>
        {
            lpEvent.Cancel = true;
            lpStopRequested.Set();
        };

        lpRuntime.Start();
        Console.WriteLine($"{Branding.DisplayName} Service 已启动（console 模式）。Ctrl+C 停止。");
        lpStopRequested.Wait();
        lpRuntime.Stop();
        AppLog.Shutdown();
        return 0;
    }
}
