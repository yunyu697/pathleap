using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;
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

    /// <summary>USN 变更安静这么久才重建，避免安装/编译的连续写入反复触发。</summary>
    private static readonly TimeSpan JournalQuietPeriod = TimeSpan.FromSeconds(60);

    /// <summary>两次自动重建之间至少间隔这么久，给查询留出稳定窗口。</summary>
    private static readonly TimeSpan JournalMinInterval = TimeSpan.FromMinutes(15);

    private readonly string _lpMode;
    private readonly DateTimeOffset _lpStartedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _lpCts = new();
    private readonly IndexService _lpIndex = new();
    private Task? _lpPipeTask;
    private Task? _lpAutoBuildTask;
    private UsnJournalWatcher? _lpWatcher;
    private CancellationTokenSource? _lpWatchCts;

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
        _lpIndex.LoadExtras();

        // 不阻塞管道启动：先让 UI 能连上并读到「构建中 + 进度」，构建在后台自行推进。
        _lpAutoBuildTask = Task.Run(() => AutoBuildIfNeeded(_lpCts.Token));
        _ = Task.Run(() => EnsureExtrasIfNeeded(_lpCts.Token));

        _lpIndex.BuildFinished += StartJournalWatchIfUsn;
        StartJournalWatchIfUsn();

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

    /// <summary>
    /// 附加索引自检：网络位置关闭时清掉旧文件；开启且文件缺失或早于配置改动时，后台重建一次。
    /// 不做实时监听，共享盘上的新增文件需要重新保存设置或重建索引才会出现。
    /// </summary>
    private void EnsureExtrasIfNeeded(CancellationToken cancellationToken)
    {
        try
        {
            var lpConfig = ConfigStore.Load(AppPaths.ConfigFile);
            var lpRoots = ExtrasIndexRoots.Resolve(lpConfig);
            var lpExtrasFile = _lpIndex.ExtrasFilePath;
            if (lpRoots.Count == 0)
            {
                if (File.Exists(lpExtrasFile))
                {
                    AppLog.Info("网络位置未启用，清空附加索引");
                    _lpIndex.StartExtrasBuildAsync(Array.Empty<string>(), cancellationToken).GetAwaiter().GetResult();
                }

                return;
            }

            var lpStale = !File.Exists(lpExtrasFile)
                          || (File.Exists(AppPaths.ConfigFile)
                              && File.GetLastWriteTimeUtc(lpExtrasFile) < File.GetLastWriteTimeUtc(AppPaths.ConfigFile));
            if (!lpStale)
            {
                AppLog.Info($"附加索引无需重建：{lpRoots.Count} 个根");
                return;
            }

            AppLog.Info($"附加索引启动重建：{lpRoots.Count} 个根（{string.Join("；", lpRoots)}）");
            var lpResult = _lpIndex.StartExtrasBuildAsync(lpRoots, cancellationToken).GetAwaiter().GetResult();
            if (lpResult is not null)
            {
                AppLog.Info($"附加索引构建完成：{lpResult.EntryCount:N0} 条，{lpResult.BuildMs:0}ms，{lpResult.Source}");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("附加索引构建已取消（服务停止）");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"附加索引构建失败: {ex.Message}");
        }
    }

    private void StartJournalWatchIfUsn()
    {
        try
        {
            StopJournalWatch();
            var lpStatus = _lpIndex.Status;
            if (!lpStatus.Loaded)
            {
                return;
            }

            if (!lpStatus.Source.StartsWith("usn:", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"运行期自动刷新未启用：当前索引来源为 {lpStatus.Source}，运行期变更需手动「重建索引」");
                return;
            }

            var lpRoot = string.IsNullOrEmpty(_lpIndex.LastBuildRoot) ? @"C:\" : _lpIndex.LastBuildRoot;
            var lpWatcher = new UsnJournalWatcher(lpRoot);
            if (!lpWatcher.TryStart())
            {
                AppLog.Warn($"运行期自动刷新未启用：{lpWatcher.Error}；运行期变更仍需手动「重建索引」");
                lpWatcher.Dispose();
                return;
            }

            var lpCts = CancellationTokenSource.CreateLinkedTokenSource(_lpCts.Token);
            var lpCoalescer = new ChangeCoalescer(
                JournalQuietPeriod,
                JournalMinInterval,
                lpStatus.BuiltAt ?? DateTimeOffset.Now);
            _lpWatcher = lpWatcher;
            _lpWatchCts = lpCts;
            _ = Task.Run(() => JournalLoop(lpWatcher, lpCoalescer, lpCts.Token));
            AppLog.Info(
                $"运行期自动刷新已启用：安静 {JournalQuietPeriod.TotalSeconds:0}s 后重建，最小间隔 {JournalMinInterval.TotalMinutes:0} 分钟");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"启动运行期自动刷新失败: {ex.Message}");
        }
    }

    private void JournalLoop(UsnJournalWatcher watcher, ChangeCoalescer coalescer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var lpChanges = watcher.Poll(2000);
                if (lpChanges < 0)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        AppLog.Warn($"运行期自动刷新停止：{watcher.Error}");
                    }

                    return;
                }

                var lpNow = DateTimeOffset.Now;
                if (lpChanges > 0)
                {
                    coalescer.Notify(lpChanges, lpNow);
                    AppLog.Debug($"USN 变更 +{lpChanges} 条，待刷新累计 {coalescer.Pending}");
                }

                if (_lpIndex.Building || !coalescer.ShouldRebuild(lpNow))
                {
                    continue;
                }

                var lpPending = coalescer.MarkRebuilt(lpNow);
                AppLog.Info($"USN 累计 {lpPending:N0} 条变更且已安静 {JournalQuietPeriod.TotalSeconds:0}s，自动重建索引");
                try
                {
                    _ = _lpIndex.StartBuildAsync(_lpIndex.LastBuildRoot, _lpIndex.LastBuildMode, _lpCts.Token)
                        .ContinueWith(static task =>
                        {
                            if (task.IsFaulted)
                            {
                                AppLog.Warn($"自动重建失败：{task.Exception?.GetBaseException().Message}");
                            }
                        }, TaskScheduler.Default);
                }
                catch (InvalidOperationException)
                {
                    // 已有构建在进行，交给 BuildFinished 重挂后再判断。
                }

                return;
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                AppLog.Error("USN 监视循环异常", ex);
            }
        }
    }

    private void StopJournalWatch()
    {
        var lpCts = _lpWatchCts;
        _lpWatchCts = null;
        if (lpCts is not null)
        {
            try
            {
                lpCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已在关闭流程中。
            }

            lpCts.Dispose();
        }

        _lpWatcher?.Dispose();
        _lpWatcher = null;
    }

    public void Stop()
    {
        AppLog.Info("服务停止中…");
        StopJournalWatch();
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

            case "INDEX_EXTRAS":
                return Task.Run(BuildExtras);

            case "SEARCH":
                return Task.FromResult(SearchIndex(request));

            default:
                return Task.FromResult(IpcReply.Failure($"服务不支持命令: {request.Command}"));
        }
    }

    private IpcReply SearchIndex(IpcRequest request)
    {
        var lpStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var lpDirectoriesOnly = request.Scope switch
        {
            "folder" => (bool?)true,
            "file" => (bool?)false,
            _ => null
        };
        var lpOutcome = _lpIndex.Search(request.Query, request.Limit > 0 ? request.Limit : 30, lpDirectoriesOnly);
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
                IsProgram = hit.IsProgram,
                IsBrowser = hit.IsBrowser,
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

    private IpcReply BuildExtras()
    {
        try
        {
            if (_lpIndex.BuildingExtras)
            {
                return IpcReply.Failure("附加索引正在构建中");
            }

            var lpConfig = ConfigStore.Load(AppPaths.ConfigFile);
            var lpRoots = ExtrasIndexRoots.Resolve(lpConfig);
            _ = _lpIndex.StartExtrasBuildAsync(lpRoots, _lpCts.Token)
                .ContinueWith(static task =>
                {
                    if (task.IsFaulted)
                    {
                        AppLog.Error("附加索引构建失败", task.Exception.GetBaseException());
                    }
                    else if (task.IsCanceled)
                    {
                        AppLog.Info("附加索引构建已取消（服务停止）");
                    }
                }, TaskScheduler.Default);

            return new IpcReply
            {
                Ok = true,
                Message = $"extras-started:{lpRoots.Count}",
                Index = _lpIndex.Status
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("附加索引构建启动失败", ex);
            return IpcReply.Failure("附加索引构建失败: " + ex.Message);
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
