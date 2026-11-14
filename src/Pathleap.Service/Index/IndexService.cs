using System.Diagnostics;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;
using Pathleap.Shared.Ipc;

namespace Pathleap.Service.Index;

/// <summary>
/// 索引服务门面：构建（USN 主路径 / 目录遍历降级）、启动时装载快照、查询。
/// M2 只维护一个“活跃索引文件”，查询命中这一个；多卷合并留给后续里程碑。
///
/// 构建期间不释放旧快照：枚举阶段旧索引持续可搜，收到 write 阶段进度后才释放映射，
/// 因为 Windows 下被内存映射的文件无法被覆盖替换。冷启动没有旧索引时，
/// 查询会返回「构建中 + 已扫描条目」，UI 据此显示进度而不是硬失败。
/// </summary>
internal sealed class IndexService : IDisposable
{
    private readonly object _lpGate = new();
    private readonly SearchPreferences _lpPreferences = new();
    private readonly InstalledProgramIndex _lpPrograms = new();
    private readonly BrowserIndex _lpBrowser = new();
    private IndexSnapshot? _lpSnapshot;
    private SearchEngine? _lpEngine;
    private IndexStatusDto _lpStatus = new() { State = "not-built", Note = "尚未构建索引" };
    private int _lpBuildFlag;

    public bool Building => Volatile.Read(ref _lpBuildFlag) == 1;

    /// <summary>最近一次构建使用的根与模式，运行期自动刷新沿用同样的范围。</summary>
    public string LastBuildRoot { get; private set; } = string.Empty;

    public string LastBuildMode { get; private set; } = "auto";

    /// <summary>每次构建结束（成功/失败/取消）触发，供运行期监视器重挂 USN 读取起点。</summary>
    public event Action? BuildFinished;

    public IndexStatusDto Status
    {
        get
        {
            lock (_lpGate)
            {
                var lpStatus = new IndexStatusDto
                {
                    Loaded = _lpStatus.Loaded,
                    Source = _lpStatus.Source,
                    EntryCount = _lpStatus.EntryCount,
                    FileBytes = _lpStatus.FileBytes,
                    BuildMs = _lpStatus.BuildMs,
                    LoadMs = _lpStatus.LoadMs,
                    FilePath = _lpStatus.FilePath,
                    BuiltAt = _lpStatus.BuiltAt,
                    State = _lpStatus.State,
                    Note = _lpStatus.Note,
                    Phase = _lpStatus.Phase,
                    Scanned = _lpStatus.Scanned,
                    Total = _lpStatus.Total,
                    Percent = _lpStatus.Percent,
                    Warning = _lpStatus.Warning
                };

                if (Building)
                {
                    lpStatus.State = "building";
                }

                return lpStatus;
            }
        }
    }

    /// <summary>启动时装载最近一次构建的索引快照（只读映射，不反序列化）。</summary>
    public bool LoadLatest()
    {
        Directory.CreateDirectory(AppPaths.IndexRoot);
        var lpFiles = Directory.EnumerateFiles(AppPaths.IndexRoot, "*.plidx").ToList();
        if (lpFiles.Count == 0)
        {
            AppLog.Info("未找到已有索引，等待自动构建。");
            return false;
        }

        var lpNewest = lpFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
        return Load(lpNewest);
    }

    public bool Load(string filePath)
    {
        var lpStarted = Stopwatch.GetTimestamp();
        try
        {
            var lpSnapshot = IndexSnapshot.Open(filePath);
            var lpLoadMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
            var lpEngine = new SearchEngine(lpSnapshot);
            lock (_lpGate)
            {
                _lpSnapshot?.Dispose();
                _lpSnapshot = lpSnapshot;
                _lpEngine = lpEngine;
                _lpStatus = new IndexStatusDto
                {
                    Loaded = true,
                    Source = lpSnapshot.Source,
                    EntryCount = lpSnapshot.EntryCount,
                    FileBytes = lpSnapshot.FileBytes,
                    LoadMs = Math.Round(lpLoadMs, 2),
                    FilePath = filePath,
                    BuiltAt = lpSnapshot.BuiltAt,
                    State = "ready",
                    Phase = string.Empty,
                    Scanned = lpSnapshot.EntryCount,
                    Total = lpSnapshot.EntryCount,
                    Percent = 100,
                    Note = $"已映射 {lpSnapshot.EntryCount} 条，装载 {lpLoadMs:0.00}ms"
                };
            }

            AppLog.Info($"索引已装载：{filePath}，{lpSnapshot.EntryCount} 条，映射耗时 {lpLoadMs:0.0}ms");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"索引装载失败（{filePath}）: {ex.Message}");
            lock (_lpGate)
            {
                _lpStatus = new IndexStatusDto
                {
                    State = "error",
                    Note = "索引装载失败: " + ex.Message,
                    Warning = "索引文件不可用，需要重建",
                    FilePath = filePath
                };
            }

            return false;
        }
    }

    /// <summary>
    /// 启动自检：没有可用索引、或索引快照超过 <paramref name="maxAgeDays"/> 天，就需要自动构建。
    /// 返回原因文本，便于日志与界面把「为什么自动建」说清楚。
    /// </summary>
    public (bool NeedsBuild, string Reason) EvaluateAutoBuild(int maxAgeDays)
    {
        IndexStatusDto lpStatus;
        lock (_lpGate)
        {
            lpStatus = Status;
        }

        if (!lpStatus.Loaded)
        {
            return (true, lpStatus.State == "error" ? $"上次索引不可用（{lpStatus.Note}）" : "尚未建立索引");
        }

        if (lpStatus.BuiltAt is { } lpBuilt)
        {
            var lpAge = DateTimeOffset.Now - lpBuilt;
            if (lpAge > TimeSpan.FromDays(maxAgeDays))
            {
                return (true, $"索引已过期（构建于 {lpBuilt:yyyy-MM-dd HH:mm}，超过 {maxAgeDays} 天）");
            }

            return (false, $"索引构建于 {lpBuilt:yyyy-MM-dd HH:mm}，未过期");
        }

        return (false, "索引缺少构建时间，按可用处理");
    }

    public IndexBuildResult Build(
        string root,
        string mode,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress = null)
    {
        BeginBuild();
        try
        {
            return BuildCore(root, mode, cancellationToken, progress);
        }
        finally
        {
            Volatile.Write(ref _lpBuildFlag, 0);
            RaiseBuildFinished();
        }
    }

    /// <summary>
    /// 立即登记「构建中」并把实际构建放到后台任务，调用方可以先回执、再用 INDEX_STATUS 取进度。
    /// UI 点「重建索引」走这条路径，不必让 IPC 请求挂几分钟。
    /// </summary>
    public Task<IndexBuildResult> StartBuildAsync(
        string root,
        string mode,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress = null)
    {
        BeginBuild();
        return Task.Run(() =>
        {
            try
            {
                return BuildCore(root, mode, cancellationToken, progress);
            }
            finally
            {
                Volatile.Write(ref _lpBuildFlag, 0);
                RaiseBuildFinished();
            }
        });
    }

    private void RaiseBuildFinished()
    {
        try
        {
            BuildFinished?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"构建完成回调失败: {ex.Message}");
        }
    }

    private void BeginBuild()
    {
        if (Interlocked.CompareExchange(ref _lpBuildFlag, 1, 0) != 0)
        {
            throw new InvalidOperationException("索引正在构建中，请等待当前构建完成");
        }

        lock (_lpGate)
        {
            _lpStatus.State = "building";
            _lpStatus.Phase = "scan";
            _lpStatus.Scanned = 0;
            _lpStatus.Total = 0;
            _lpStatus.Percent = -1;
            _lpStatus.Warning = string.Empty;
            _lpStatus.Note = _lpStatus.Loaded
                ? $"正在重建索引；期间仍可搜索上一次索引（{_lpStatus.EntryCount:N0} 条）"
                : "首次构建，完成后自动可搜索";
        }
    }

    private IndexBuildResult BuildCore(
        string root,
        string mode,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress)
    {
        var lpOutput = string.Empty;
        try
        {
            var lpMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
            var lpRoot = string.IsNullOrWhiteSpace(root) ? @"C:\" : root.Trim();
            LastBuildRoot = lpRoot;
            LastBuildMode = lpMode;
            var lpVolumeLabel = IndexBuildRunner.ResolveVolumeLabel(
                lpMode == "synthetic" ? "C:\\" : lpRoot);
            lpOutput = AppPaths.IndexFile(lpVolumeLabel);
            Directory.CreateDirectory(AppPaths.IndexRoot);

            AppLog.Info($"索引构建开始（子进程）：root={lpRoot} mode={lpMode} -> {lpOutput}");

            void OnProgress(IndexBuildProgress lpProgress)
            {
                lock (_lpGate)
                {
                    _lpStatus.State = "building";
                    _lpStatus.Phase = lpProgress.Phase;
                    _lpStatus.Scanned = lpProgress.Scanned;
                    _lpStatus.Total = lpProgress.Total;
                    _lpStatus.Percent = lpProgress.Percent;
                    if (!string.IsNullOrEmpty(lpProgress.Warning))
                    {
                        // 降级说明一旦上报就保留，后续扫描进度不会再覆盖它。
                        _lpStatus.Warning = lpProgress.Warning;
                    }

                    _lpStatus.Note = lpProgress.Phase == "write"
                        ? $"正在写入索引（{lpProgress.Scanned:N0} 条），此阶段暂不可搜索"
                        : $"已扫描 {lpProgress.Scanned:N0} 条";
                }

                if (lpProgress.Phase == "write")
                {
                    // 落盘前必须释放旧映射，否则子进程无法覆盖同路径索引文件。
                    ReleaseSnapshot();
                }

                progress?.Invoke(lpProgress);
            }

            var lpResult = IndexBuildRunner.RunInChildProcess(
                lpRoot,
                lpMode,
                lpOutput,
                OnProgress,
                cancellationToken);
            FinishBuild(lpOutput, lpResult);
            return lpResult;
        }
        catch (Exception ex)
        {
            RecoverAfterFailure(lpOutput, ex);
            throw;
        }
    }

    private void ReleaseSnapshot()
    {
        lock (_lpGate)
        {
            _lpSnapshot?.Dispose();
            _lpSnapshot = null;
            _lpEngine = null;
        }
    }

    private void FinishBuild(string output, IndexBuildResult result)
    {
        AppLog.Info(
            $"索引构建完成：{result.EntryCount} 条，文件 {result.FileBytes / 1024.0 / 1024.0:0.0} MB，" +
            $"名字前缀编码节省 {result.NamePrefixSavedBytes / 1024.0 / 1024.0:0.0} MB，耗时 {result.BuildMs:0}ms，跳过 {result.SkippedCount}" +
            (string.IsNullOrEmpty(result.Warning) ? string.Empty : $"；{result.Warning}"));

        Load(output);
        lock (_lpGate)
        {
            if (!_lpStatus.Loaded)
            {
                // 文件写成功但装载失败：保留 Load 给出的错误说明，并补一条告警，避免误报「就绪」。
                _lpStatus.Warning = "索引已写入但装载失败，需要重建";
                return;
            }

            _lpStatus.BuildMs = result.BuildMs;
            _lpStatus.Warning = result.Warning;
            _lpStatus.Phase = string.Empty;
            _lpStatus.Scanned = result.EntryCount;
            _lpStatus.Total = result.EntryCount;
            _lpStatus.Percent = 100;
            _lpStatus.Note = string.IsNullOrEmpty(result.Warning)
                ? $"构建 {result.EntryCount:N0} 条，{result.BuildMs:0}ms；{result.Source}"
                : $"构建 {result.EntryCount:N0} 条，{result.BuildMs:0}ms；{result.Source}；{result.Warning}";
        }
    }

    /// <summary>
    /// 构建失败后的兜底：能重新装载上一次索引就继续可搜（状态 ready + 告警），
    /// 否则标记 error 并把原因写进 Note，界面据此给出明确提示，而不是静默失败。
    /// </summary>
    private void RecoverAfterFailure(string output, Exception exception)
    {
        var lpRestored = !string.IsNullOrEmpty(output) && File.Exists(output) && Load(output);
        lock (_lpGate)
        {
            _lpStatus.BuildMs = 0;
            _lpStatus.Phase = string.Empty;
            _lpStatus.State = lpRestored ? "ready" : "error";
            _lpStatus.Warning = "构建失败：" + exception.Message;
            _lpStatus.Note = lpRestored
                ? $"上次索引仍可用（{_lpStatus.EntryCount:N0} 条）；本次构建失败：{exception.Message}"
                : "索引构建失败：" + exception.Message +
                  "；可点击「重建索引」重试；若提示权限不足，请以管理员身份运行或安装为服务";
        }
    }

    public SearchOutcome? Search(string query, int limit, bool? directoriesOnly = null)
    {
        // 偏好按查询取一次快照（配置文件改了才重读），过滤在引擎取结果阶段完成。
        var lpPreferences = _lpPreferences.Get();
        Func<string, bool>? lpPathAllowed = lpPreferences.ExcludedPrefixes.Length == 0
            ? null
            : lpPath => !SearchPreferences.IsExcluded(lpPath, lpPreferences.ExcludedPrefixes);

        SearchOutcome? lpOutcome;
        // 查询与快照释放互斥：查询只花几毫秒，串行化换来“重建时不会读到已释放的映射”。
        lock (_lpGate)
        {
            lpOutcome = _lpEngine?.Search(query, limit, directoriesOnly, lpPathAllowed);
        }

        if (lpOutcome is null)
        {
            return null;
        }

        // 已安装程序、浏览器书签/历史与文件索引合并：这两类在启动器场景里更相关，
        // 同 tier 时排在文件命中之前。
        var lpSupplemental = new List<SearchHit>();
        if (directoriesOnly != true && !string.IsNullOrWhiteSpace(query))
        {
            if (lpPreferences.IncludeInstalledPrograms)
            {
                lpSupplemental.AddRange(_lpPrograms.Match(query, limit, lpPathAllowed));
            }

            if (lpPreferences.IncludeBrowserHistory)
            {
                lpSupplemental.AddRange(_lpBrowser.Match(query, limit));
            }
        }

        if (lpSupplemental.Count > 0)
        {
            MergeSupplementalHits(lpOutcome, lpSupplemental, limit);
        }

        return lpOutcome;
    }

    /// <summary>
    /// 补充来源（程序 / 浏览器）按路径去重后并进结果，再按 tier 稳定排序（补充条目先插入，同 tier 在前）。
    /// 同一应用的重复快捷方式（用户/公共开始菜单各一份）只保留一条：文件命中里的 .lnk
    /// 若与程序条目同名，也一并丢掉，避免结果里出现“draw.io”和“draw.io.lnk”两条。
    /// </summary>
    private static void MergeSupplementalHits(SearchOutcome outcome, List<SearchHit> supplementalHits, int limit)
    {
        var lpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpProgramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpMerged = new List<SearchHit>(supplementalHits.Count + outcome.Hits.Count);
        foreach (var lpHit in supplementalHits)
        {
            if (lpHit.IsProgram)
            {
                lpProgramNames.Add(lpHit.Name);
            }

            if (lpSeen.Add(lpHit.Path))
            {
                lpMerged.Add(lpHit);
            }
        }

        foreach (var lpHit in outcome.Hits)
        {
            if (!lpHit.IsDirectory
                && lpHit.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && lpProgramNames.Contains(System.IO.Path.GetFileNameWithoutExtension(lpHit.Name)))
            {
                continue;
            }

            if (lpSeen.Add(lpHit.Path))
            {
                lpMerged.Add(lpHit);
            }
        }

        var lpTake = Math.Clamp(limit, 1, 500);
        var lpOrdered = lpMerged
            .OrderByDescending(static lpHit => lpHit.Tier)
            .Take(lpTake)
            .ToList();

        outcome.Hits.Clear();
        outcome.Hits.AddRange(lpOrdered);
    }

    public void Dispose()
    {
        lock (_lpGate)
        {
            _lpSnapshot?.Dispose();
            _lpSnapshot = null;
            _lpEngine = null;
        }
    }
}
