using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Service.Index;

namespace Pathleap.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        AppLog.Initialize("service");

        // 构建子进程入口：--build-once <mode> <root> <output>，跑完即退出，不走单实例互斥。
        var lpBuildOnce = Array.FindIndex(args, static a => string.Equals(a, "--build-once", StringComparison.OrdinalIgnoreCase));
        if (lpBuildOnce >= 0)
        {
            if (args.Length < lpBuildOnce + 4)
            {
                AppLog.Error("--build-once 参数不足，期望：--build-once <mode> <root> <output>");
                return 64;
            }

            return IndexBuildCli.Run(args[lpBuildOnce + 1], args[lpBuildOnce + 2], args[lpBuildOnce + 3]);
        }

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
