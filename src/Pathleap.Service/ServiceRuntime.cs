using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;
using Pathleap.Service.Index;

namespace Pathleap.Service;

/// <summary>
/// 索引服务：与 UI 完全分离的第二个进程。
/// 控制面 PING / STATUS / SEARCH / INDEX_STATUS / INDEX_BUILD；
/// 索引来源优先 USN/MFT（需管理员或服务身份），否则降级目录遍历。
/// </summary>
public sealed class ServiceRuntime : IDisposable
{
    private readonly string _lpMode;
    private readonly DateTimeOffset _lpStartedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _lpCts = new();
    private readonly IndexService _lpIndex = new();
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
        AppLog.Info($"索引目录: {AppPaths.IndexRoot}");
        _lpIndex.LoadLatest();

        _lpPipeTask = PipeServer.RunAsync(Branding.ServicePipeName, HandleRequestAsync, _lpCts.Token);
    }

    public void Stop()
    {
        AppLog.Info("服务停止中…");
        _lpCts.Cancel();
        _lpIndex.Dispose();
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
                        Index = _lpIndex.Status.State,
                        Version = Branding.Version,
                        StartedAt = _lpStartedAt
                    },
                    Index = _lpIndex.Status
                });

            case "INDEX_STATUS":
                return Task.FromResult(new IpcReply
                {
                    Ok = true,
                    Message = "index-status",
                    Index = _lpIndex.Status
                });

            case "INDEX_BUILD":
                return Task.Run(() => BuildIndex(request));

            case "SEARCH":
                return Task.FromResult(SearchIndex(request));

            default:
                return Task.FromResult(IpcReply.Failure($"服务不支持命令: {request.Command}"));
        }
    }

    private IpcReply SearchIndex(IpcRequest request)
    {
        var lpStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var lpOutcome = _lpIndex.Search(request.Query, request.Limit > 0 ? request.Limit : 30);
        if (lpOutcome is null)
        {
            return IpcReply.Failure("索引尚未构建，请先执行 INDEX_BUILD");
        }

        var lpTotalMs = Math.Round(
            (System.Diagnostics.Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            3);

        return new IpcReply
        {
            Ok = true,
            Message = $"hits={lpOutcome.Hits.Count}",
            QueryMs = lpOutcome.ElapsedMs,
            CandidateCount = lpOutcome.CandidateCount,
            Hits = lpOutcome.Hits.Select(static hit => new SearchHitDto
            {
                Name = hit.Name,
                Path = hit.Path,
                IsDirectory = hit.IsDirectory,
                Size = hit.Size,
                ModifiedAt = hit.ModifiedAt?.ToString("O"),
                Tier = hit.Tier,
                Score = hit.Score
            }).ToList(),
            Index = _lpIndex.Status,
            P50Ms = lpTotalMs
        };
    }

    private IpcReply BuildIndex(IpcRequest request)
    {
        try
        {
            var lpResult = _lpIndex.Build(request.Root, request.Mode, _lpCts.Token);
            return new IpcReply
            {
                Ok = true,
                Message = $"index-built entries={lpResult.EntryCount}",
                Index = _lpIndex.Status,
                P50Ms = lpResult.BuildMs,
                QueryMs = lpResult.BuildMs
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("索引构建失败", ex);
            return IpcReply.Failure("索引构建失败: " + ex.Message);
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
