using Microsoft.UI.Xaml;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;

namespace Pathleap.UI;

/// <summary>命令行语义：无参数=常驻并显示一次；--tray=常驻但不显示（测试/后台用）；--bench=基准测试；--show/--status/--quit 为控制命令。</summary>
internal sealed class StartupOptions
{
    public string Command { get; set; } = string.Empty;

    public bool AutoBench { get; set; }

    /// <summary>
    /// 唤醒基准会反复抢前台，必须由调用方显式声明可以占用前台（--allow-foreground）。
    /// </summary>
    public bool AllowForeground { get; set; }

    public int BenchSamples { get; set; }

    public bool ShowOnStart { get; set; } = true;

    /// <summary>截图模式：--screen=recent|results|quick|settings-*|update*|tray|about。留空为正常启动。</summary>
    public string Screen { get; set; } = string.Empty;

    /// <summary>截图/调试用：--query=文本，配合 --screen=results 直接填入搜索框。</summary>
    public string Query { get; set; } = string.Empty;

    public static StartupOptions Parse(string[] args)
    {
        var lpOptions = new StartupOptions();
        for (var lpIndex = 0; lpIndex < args.Length; lpIndex++)
        {
            var lpArg = args[lpIndex];
            if (lpArg.StartsWith("--screen=", StringComparison.OrdinalIgnoreCase))
            {
                lpOptions.Screen = lpArg["--screen=".Length..].Trim();
                lpOptions.ShowOnStart = false;
                continue;
            }

            if (lpArg.StartsWith("--query=", StringComparison.OrdinalIgnoreCase))
            {
                lpOptions.Query = lpArg["--query=".Length..];
                continue;
            }

            switch (lpArg.ToLowerInvariant())
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
                case "--allow-foreground":
                    lpOptions.AllowForeground = true;
                    break;
                case "--tray":
                    // 常驻但不显示：托盘 + 热键照常注册，不抢前台。冒烟测试与自动化用它。
                    lpOptions.ShowOnStart = false;
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
                if (!Startup.AllowForeground)
                {
                    AppLog.Warn("拒绝转发 BENCH：未声明 --allow-foreground（该基准会反复占用前台）。");
                    return 3;
                }

                lpRequest.Samples = Startup.BenchSamples;
                lpRequest.AllowForeground = true;
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
