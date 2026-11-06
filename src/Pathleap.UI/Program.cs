using Microsoft.UI.Xaml;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;

namespace Pathleap.UI;

/// <summary>命令行语义：无参数=常驻并显示一次；--bench=基准测试；--show/--status/--quit 为控制命令。</summary>
internal sealed class StartupOptions
{
    public string Command { get; set; } = string.Empty;

    public bool AutoBench { get; set; }

    public int BenchSamples { get; set; }

    public bool ShowOnStart { get; set; } = true;

    public static StartupOptions Parse(string[] args)
    {
        var lpOptions = new StartupOptions();
        for (var lpIndex = 0; lpIndex < args.Length; lpIndex++)
        {
            switch (args[lpIndex].ToLowerInvariant())
            {
                case "--bench":
                    lpOptions.AutoBench = true;
                    lpOptions.Command = "BENCH";
                    lpOptions.ShowOnStart = false;
                    if (lpIndex + 1 < args.Length && int.TryParse(args[lpIndex + 1], out var lpSamples))
                    {
                        lpOptions.BenchSamples = lpSamples;
                        lpIndex++;
                    }

                    break;
                case "--show":
                    lpOptions.Command = "SHOW";
                    lpOptions.ShowOnStart = true;
                    break;
                case "--status":
                    lpOptions.Command = "STATUS";
                    lpOptions.ShowOnStart = false;
                    break;
                case "--quit":
                    lpOptions.Command = "QUIT";
                    lpOptions.ShowOnStart = false;
                    break;
            }
        }

        return lpOptions;
    }
}

public static class Program
{
    internal static StartupOptions Startup = new();

    [STAThread]
    public static int Main(string[] args)
    {
        AppLog.Initialize("ui");
        Startup = StartupOptions.Parse(args);
        AppLog.Info($"UI {Branding.Version} 启动，PID={Environment.ProcessId}，args=[{string.Join(' ', args)}]");
        AppLog.Info($"配置: {AppPaths.ConfigFile}");
        AppLog.Info($"日志目录: {AppLog.LogDirectory}");

        using var lpMutex = new Mutex(initiallyOwned: true, Branding.UiMutexName, out var lpCreatedNew);
        if (!lpCreatedNew)
        {
            AppLog.Info("已有 UI 实例，转发命令后退出。");
            return ForwardToPrimary();
        }

        AppLog.Info($"单实例互斥体已获取: {Branding.UiMutexName}");

        if (Startup.Command == "QUIT")
        {
            return 0;
        }

        if (Startup.Command == "STATUS")
        {
            AppLog.Info("没有运行中的实例。");
            return 3;
        }

        try
        {
            Application.Start(_ => new App());
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("WinUI 应用启动失败", ex);
            return 1;
        }
    }

    private static int ForwardToPrimary()
    {
        try
        {
            var lpRequest = new IpcRequest { Command = Startup.Command };
            if (Startup.Command == "BENCH")
            {
                lpRequest.Samples = Startup.BenchSamples;
            }

            var lpTimeout = Startup.Command == "BENCH" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(5);
            var lpReply = PipeClient.SendAsync(Branding.UiPipeName, lpRequest, lpTimeout)
                .GetAwaiter()
                .GetResult();

            AppLog.Info($"转发结果: ok={lpReply.Ok}, message={lpReply.Message}, summary={lpReply.SummaryPath}");
            if (lpReply.P50Ms >= 0)
            {
                AppLog.Info($"转发基准数据: P50={lpReply.P50Ms}ms, P95={lpReply.P95Ms}ms");
            }

            return lpReply.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"无法连接主实例管道: {ex.Message}");
            return 2;
        }
    }
}
