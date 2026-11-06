using System.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>
/// 索引构建的唯一实现：服务进程（IPC 路径）通过短生命周期子进程调用它，
/// 子进程用 <c>--build-once</c> 直接跑同一条流程。
///
/// 之所以放到子进程：500 万级构建会产生 1.5 GB 级临时堆（构建期节点 + 名字副本），
/// 这些内存由 GC 持有、进程退出前不会归还给系统。服务是长驻进程，
/// 在进程内构建会把峰值内存永久留在服务身上；让构建进程退出，内存随进程一并回收。
/// </summary>
internal static class IndexBuildRunner
{
    public static IndexBuildResult Run(string root, string mode, string output, CancellationToken cancellationToken)
    {
        var lpMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        var lpRoot = string.IsNullOrWhiteSpace(root) ? @"C:\" : root.Trim();

        if (lpMode == "synthetic")
        {
            // 压测入口：root 解析成条目数，默认 500 万。
            var lpCount = int.TryParse(lpRoot, out var lpParsedCount) ? lpParsedCount : 5_000_000;
            return SyntheticIndexSource.Build(lpCount, output, cancellationToken);
        }

        if (lpMode is "usn" or "auto")
        {
            try
            {
                var lpVolumeLabel = ResolveVolumeLabel(lpRoot);
                using var lpSource = UsnVolumeSource.Open(lpVolumeLabel, lpVolumeLabel);
                return lpSource.BuildFull(output, static _ => { }, cancellationToken);
            }
            catch (UsnAccessException) when (lpMode == "auto")
            {
                // 非管理员/服务身份拿不到卷句柄：自动降级到目录遍历。
            }
            catch (Exception) when (lpMode == "auto")
            {
                // auto 模式任何 USN 失败都降级，保证索引可用。
            }
        }

        return DirectoryIndexSource.Build(lpRoot, output, static _ => { }, cancellationToken);
    }

    /// <summary>C: / C:\ / C 都归一成 C；目录路径取所在卷。</summary>
    public static string ResolveVolumeLabel(string root)
    {
        var lpRoot = root.Trim();
        if (lpRoot.Length <= 3 && lpRoot.Contains(':'))
        {
            return char.ToUpperInvariant(lpRoot[0]).ToString();
        }

        var lpPathRoot = Path.GetPathRoot(Path.GetFullPath(lpRoot));
        return string.IsNullOrEmpty(lpPathRoot) ? "C" : char.ToUpperInvariant(lpPathRoot[0]).ToString();
    }

    /// <summary>在服务进程外跑一次构建，返回子进程落盘的构建摘要。</summary>
    public static IndexBuildResult RunInChildProcess(
        string root,
        string mode,
        string output,
        CancellationToken cancellationToken)
    {
        var lpExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(lpExe))
        {
            throw new InvalidOperationException("无法定位服务可执行文件，不能启动索引构建子进程");
        }

        var lpResultFile = output + ".result.json";
        TryDelete(lpResultFile);

        var lpStartInfo = new ProcessStartInfo(lpExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        lpStartInfo.ArgumentList.Add("--build-once");
        lpStartInfo.ArgumentList.Add(mode);
        lpStartInfo.ArgumentList.Add(root);
        lpStartInfo.ArgumentList.Add(output);

        using var lpProcess = new Process { StartInfo = lpStartInfo };
        lpProcess.Start();

        while (!lpProcess.WaitForExit(200))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(lpProcess);
                throw new OperationCanceledException(cancellationToken);
            }
        }

        if (lpProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"索引构建子进程退出码 {lpProcess.ExitCode}");
        }

        if (!File.Exists(lpResultFile))
        {
            throw new InvalidOperationException("索引构建子进程未产出结果摘要");
        }

        var lpJson = File.ReadAllText(lpResultFile);
        var lpResult = System.Text.Json.JsonSerializer.Deserialize<IndexBuildResult>(lpJson);
        if (lpResult is null)
        {
            throw new InvalidOperationException("索引构建结果摘要无法解析");
        }

        // 子进程已把自身构建耗时写进摘要（含节点生成与落盘），进程启动开销可以忽略，直接沿用。
        TryDelete(lpResultFile);
        return lpResult;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 摘要文件清理失败不影响构建结果。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 子进程已退出。
        }
    }
}
