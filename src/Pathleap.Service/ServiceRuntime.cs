using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;

namespace Pathleap.Service;

/// <summary>
/// M1 索引服务空壳：
///  - 与 UI 完全分离的第二个进程；
///  - 只提供 PING / STATUS 控制面；
///  - 真实 MFT / USN 索引在 M2 接入，本阶段 index = not-implemented。
/// </summary>
public sealed class ServiceRuntime : IDisposable
{
    private readonly string _lpMode;
    private readonly DateTimeOffset _lpStartedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _lpCts = new();
    private Task? _lpPipeTask;

    public ServiceRuntime(string mode)
    {
        _lpMode = mode;
    }

    public void Start()
    {
        var lpOptions = EnsureServiceConfig(_lpMode);
        var lpLevel = Enum.TryParse<LogLevel>(lpOptions.LogLevel, ignoreCase: true, out var lpParsed)
            ? lpParsed
            : LogLevel.Info;
        AppLog.SetMinimumLevel(lpLevel);

        AppLog.Info($"{Branding.DisplayName} 服务启动，模式={_lpMode}，PID={Environment.ProcessId}");
        AppLog.Info($"服务配置: {AppPaths.ServiceConfigFile}");
        AppLog.Info("索引状态: not-implemented（M1 空壳，M2 接入 FSCTL_ENUM_USN_DATA + USN）");

        _lpPipeTask = PipeServer.RunAsync(Branding.ServicePipeName, HandleRequestAsync, _lpCts.Token);
    }

    public void Stop()
    {
        AppLog.Info("服务停止中…");
        _lpCts.Cancel();
        try
        {
            _lpPipeTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 关闭阶段的超时/取消不视为错误。
        }

        AppLog.Info("服务已停止");
    }

    private Task<IpcReply> HandleRequestAsync(IpcRequest request)
    {
        switch (request.Command.ToUpperInvariant())
        {
            case "PING":
                return Task.FromResult(IpcReply.Success("pong"));

            case "STATUS":
                return Task.FromResult(new IpcReply
                {
                    Ok = true,
                    Message = "status",
                    Status = new ServiceStatusDto
                    {
                        Product = Branding.ProductId,
                        Mode = _lpMode,
                        Pid = Environment.ProcessId,
                        UptimeMs = (long)(DateTimeOffset.Now - _lpStartedAt).TotalMilliseconds,
                        Index = "not-implemented",
                        Version = Branding.Version,
                        StartedAt = _lpStartedAt
                    }
                });

            default:
                return Task.FromResult(IpcReply.Failure($"服务不支持命令: {request.Command}"));
        }
    }

    private static ServiceOptions EnsureServiceConfig(string mode)
    {
        try
        {
            if (File.Exists(AppPaths.ServiceConfigFile))
            {
                var lpJson = File.ReadAllText(AppPaths.ServiceConfigFile);
                return System.Text.Json.JsonSerializer.Deserialize<ServiceOptions>(lpJson, JsonDefaults.Options)
                       ?? new ServiceOptions();
            }

            AppPaths.EnsureDirectory(AppPaths.ServiceRoot);
            var lpFresh = new ServiceOptions { Mode = mode };
            File.WriteAllText(
                AppPaths.ServiceConfigFile,
                System.Text.Json.JsonSerializer.Serialize(lpFresh, JsonDefaults.Options));
            return lpFresh;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"服务配置初始化失败，使用默认值: {ex.Message}");
            return new ServiceOptions();
        }
    }

    public void Dispose()
    {
        _lpCts.Dispose();
    }

    private sealed class ServiceOptions
    {
        public int SchemaVersion { get; set; } = 1;

        public string Mode { get; set; } = "console";

        public string LogLevel { get; set; } = "Info";

        /// <summary>M1 固定 not-implemented。</summary>
        public string IndexMode { get; set; } = "not-implemented";

        public string Note { get; set; } = "M1 skeleton: no index implementation";
    }
}
