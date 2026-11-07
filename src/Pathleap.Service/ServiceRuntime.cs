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
    /// <summary>索引超过这个天数就视为过期，启动时自动重扫。</summary>
    private const int IndexRefreshDays = 7;

    private readonly string _lpMode;
    private readonly DateTimeOffset _lpStartedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _lpCts = new();
    private readonly IndexService _lpIndex = new();
    private Task? _lpPipeTask;
    private Task? _lpAutoBuildTask;

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

        // 不阻塞管道启动：先让 UI 能连上并读到「构建中 + 进度」，构建在后台自行推进。
        _lpAutoBuildTask = Task.Run(() => AutoBuildIfNeeded(_lpCts.Token));

        _lpPipeTask = PipeServer.RunAsync(Branding.ServicePipeName, HandleRequestAsync, _lpCts.Token);
    }

    /// <summary>
    /// 启动自检：索引缺失、上次构建失败、或快照超过 <see cref="IndexRefreshDays"/> 天时自动后台构建。
    /// 同一时刻只允许一个构建（<see cref="IndexService.Build"/> 内部有互斥），重复触发会直接跳过。
    /// </summary>
    private void AutoBuildIfNeeded(CancellationToken cancellationToken)
    {
        try
        {
            var (lpNeedsBuild, lpReason) = _lpIndex.EvaluateAutoBuild(IndexRefreshDays);
            if (!lpNeedsBuild)
            {
                AppLog.Info($"启动自检：{lpReason}，跳过自动构建");
                return;
            }

            AppLog.Info($"启动自检：{lpReason}，后台自动构建开始");
            var lpResult = _lpIndex.Build(string.Empty, "auto", cancellationToken);
            AppLog.Info(
                $"启动自动构建完成：{lpResult.EntryCount:N0} 条，{lpResult.BuildMs:0}ms，" +
                $"来源 {lpResult.Source}" +
                (string.IsNullOrEmpty(lpResult.Warning) ? string.Empty : $"，{lpResult.Warning}"));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("启动自动构建已取消（服务停止）");
        }
        catch (Exception ex)
        {
            // 失败详情已经写进索引状态（Note/Warning），这里只记日志，UI 会展示原因。
            AppLog.Error("启动自动构建失败", ex);
        }
    }

    public void Stop()
    {
        AppLog.Info("服务停止中…");
        _lpCts.Cancel();
        try
        {
            _lpAutoBuildTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 构建被取消属于正常关闭路径。
        }

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
            // 冷启动没有旧索引可搜：把「构建中 + 已扫描条目」返回给 UI 显示进度，而不是干巴巴的失败。
            var lpStatus = _lpIndex.Status;
            var lpMessage = lpStatus.State switch
            {
                "building" => "索引构建中：" + DescribeBuildProgress(lpStatus) + "，完成后自动可搜",
                "error" => "索引不可用：" + lpStatus.Note,
                _ => "索引尚未构建，稍后会自动开始"
            };
            return new IpcReply { Ok = false, Message = lpMessage, Index = lpStatus };
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

    private static string DescribeBuildProgress(IndexStatusDto status)
    {
        var lpPercent = status.Percent >= 0 ? $"{status.Percent:0.#}%，" : string.Empty;
        return status.Phase == "write"
            ? $"{lpPercent}正在写入 {status.Scanned:N0} 条"
            : $"{lpPercent}已扫描 {status.Scanned:N0} 条";
    }

    private IpcReply BuildIndex(IpcRequest request)
    {
        try
        {
            if (_lpIndex.Building)
            {
                var lpRunning = _lpIndex.Status;
                return new IpcReply
                {
                    Ok = false,
                    Message = "索引正在构建中：" + DescribeBuildProgress(lpRunning),
                    Index = lpRunning
                };
            }

            // 立刻回执，不让 IPC 请求挂几分钟；进度由 UI 轮询 INDEX_STATUS 获取。
            _ = _lpIndex.StartBuildAsync(request.Root, request.Mode, _lpCts.Token)
                .ContinueWith(static task =>
                {
                    if (task.IsFaulted)
                    {
                        AppLog.Error("索引构建失败", task.Exception.GetBaseException());
                    }
                    else if (task.IsCanceled)
                    {
                        AppLog.Info("索引构建已取消（服务停止）");
                    }
                }, TaskScheduler.Default);

            return new IpcReply
            {
                Ok = true,
                Message = "index-build-started",
                Index = _lpIndex.Status
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("索引构建启动失败", ex);
            return new IpcReply
            {
                Ok = false,
                Message = "索引构建失败: " + ex.Message,
                Index = _lpIndex.Status
            };
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
