using System.Diagnostics;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;
using Pathleap.Shared.Ipc;

namespace Pathleap.Service.Index;

/// <summary>
/// 索引服务门面：构建（USN 主路径 / 目录遍历降级）、启动时装载快照、查询。
/// M2 只维护一个“活跃索引文件”，查询命中这一个；多卷合并留给后续里程碑。
/// </summary>
internal sealed class IndexService : IDisposable
{
    private readonly object _lpGate = new();
    private IndexSnapshot? _lpSnapshot;
    private SearchEngine? _lpEngine;
    private IndexStatusDto _lpStatus = new() { State = "not-built", Note = "尚未构建索引" };
    private volatile bool _lpBuilding;

    public bool Building => _lpBuilding;

    public IndexStatusDto Status
    {
        get
        {
            lock (_lpGate)
            {
                return new IndexStatusDto
                {
                    Loaded = _lpStatus.Loaded,
                    Source = _lpStatus.Source,
                    EntryCount = _lpStatus.EntryCount,
                    FileBytes = _lpStatus.FileBytes,
                    BuildMs = _lpStatus.BuildMs,
                    LoadMs = _lpStatus.LoadMs,
                    FilePath = _lpStatus.FilePath,
                    BuiltAt = _lpStatus.BuiltAt,
                    State = _lpBuilding ? "building" : _lpStatus.State,
                    Note = _lpStatus.Note
                };
            }
        }
    }

    /// <summary>启动时装载最近一次构建的索引快照（只读映射，不反序列化）。</summary>
    public void LoadLatest()
    {
        Directory.CreateDirectory(AppPaths.IndexRoot);
        var lpFiles = Directory.EnumerateFiles(AppPaths.IndexRoot, "*.plidx").ToList();
        if (lpFiles.Count == 0)
        {
            AppLog.Info("未找到已有索引，等待 INDEX_BUILD。");
            return;
        }

        var lpNewest = lpFiles.OrderByDescending(File.GetLastWriteTimeUtc).First();
        Load(lpNewest);
    }

    public void Load(string filePath)
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
                    Note = $"已映射 {lpSnapshot.EntryCount} 条，装载 {lpLoadMs:0.00}ms"
                };
            }

            AppLog.Info($"索引已装载：{filePath}，{lpSnapshot.EntryCount} 条，映射耗时 {lpLoadMs:0.0}ms");
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
                    FilePath = filePath
                };
            }
        }
    }

    public IndexBuildResult Build(string root, string mode, CancellationToken cancellationToken)
    {
        _lpBuilding = true;
        try
        {
            // Windows 下被内存映射的文件无法被覆盖替换，重建前必须先释放旧映射。
            lock (_lpGate)
            {
                _lpSnapshot?.Dispose();
                _lpSnapshot = null;
                _lpEngine = null;
                _lpStatus = new IndexStatusDto { State = "building", Note = "构建中" };
            }

            var lpMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
            var lpRoot = string.IsNullOrWhiteSpace(root) ? @"C:\" : root.Trim();
            var lpVolumeLabel = IndexBuildRunner.ResolveVolumeLabel(
                lpMode == "synthetic" ? "C:\\" : lpRoot);
            var lpOutput = AppPaths.IndexFile(lpVolumeLabel);
            Directory.CreateDirectory(AppPaths.IndexRoot);

            AppLog.Info($"索引构建开始（子进程）：root={lpRoot} mode={lpMode} -> {lpOutput}");

            var lpResult = IndexBuildRunner.RunInChildProcess(lpRoot, lpMode, lpOutput, cancellationToken);
            FinishBuild(lpOutput, lpResult);
            return lpResult;
        }
        finally
        {
            _lpBuilding = false;
        }
    }

    private void FinishBuild(string output, IndexBuildResult result)
    {
        AppLog.Info(
            $"索引构建完成：{result.EntryCount} 条，文件 {result.FileBytes / 1024.0 / 1024.0:0.0} MB，" +
            $"名字前缀编码节省 {result.NamePrefixSavedBytes / 1024.0 / 1024.0:0.0} MB，耗时 {result.BuildMs:0}ms，跳过 {result.SkippedCount}");
        Load(output);
        lock (_lpGate)
        {
            _lpStatus.BuildMs = result.BuildMs;
            _lpStatus.Note = $"构建 {result.EntryCount} 条，{result.BuildMs:0}ms；{result.Source}";
        }
    }

    public SearchOutcome? Search(string query, int limit)
    {
        // 查询与快照释放互斥：查询只花几毫秒，串行化换来“重建时不会读到已释放的映射”。
        lock (_lpGate)
        {
            return _lpEngine?.Search(query, limit);
        }
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
