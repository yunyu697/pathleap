using System.Diagnostics;
using Pathleap.Shared.Diagnostics;
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
    public static IndexBuildResult Run(
        string root,
        string mode,
        string output,
        Action<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lpMode = string.IsNullOrWhiteSpace(mode) ? "auto" : mode.Trim().ToLowerInvariant();
        var lpRoot = string.IsNullOrWhiteSpace(root) ? @"C:\" : root.Trim();

        if (lpMode == "synthetic")
        {
            // 压测入口：root 解析成条目数，默认 500 万。
            var lpCount = int.TryParse(lpRoot, out var lpParsedCount) ? lpParsedCount : 5_000_000;
            return SyntheticIndexSource.Build(lpCount, output, progress, cancellationToken);
        }

        if (lpMode is "usn" or "auto")
        {
            try
            {
                var lpVolumeLabel = ResolveVolumeLabel(lpRoot);
                using var lpSource = UsnVolumeSource.Open(lpVolumeLabel, lpVolumeLabel);
                return lpSource.BuildFull(output, progress, cancellationToken);
            }
            catch (Exception ex) when (lpMode == "auto")
            {
                // auto 模式：非管理员/服务身份拿不到卷句柄，降级目录遍历，并把原因带回给上层展示。
                var lpReason = ex is UsnAccessException
                    ? "权限不足（需要管理员或服务身份）"
                    : ex.Message;
                var lpWarning = $"USN 主路径不可用：{lpReason}；已降级为目录遍历";
                AppLog.Warn(lpWarning);
                // 先上报一次降级说明，UI 在扫描过程中就能看到「已降级」，不必等构建结束。
                progress?.Invoke(new IndexBuildProgress { Phase = "scan", Warning = lpWarning });
                var lpResult = DirectoryIndexSource.Build(lpRoot, output, progress, cancellationToken);
                lpResult.Warning = lpWarning;
                return lpResult;
            }
        }

        return DirectoryIndexSource.Build(lpRoot, output, progress, cancellationToken);
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

    /// <summary>
    /// 在服务进程外跑一次构建，返回子进程落盘的构建摘要。
    /// 进度由子进程写进 <c>&lt;output&gt;.progress.json</c>，这里轮询后回调给服务门面。
    /// </summary>
    public static IndexBuildResult RunInChildProcess(
        string root,
        string mode,
        string output,
        Action<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lpExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(lpExe))
        {
            throw new InvalidOperationException("无法定位服务可执行文件，不能启动索引构建子进程");
        }

        var lpResultFile = output + ".result.json";
        var lpProgressFile = output + ".progress.json";
        var lpFailFile = output + ".fail.txt";
        TryDelete(lpResultFile);
        TryDelete(lpProgressFile);
        TryDelete(lpFailFile);

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

        var lpLastProgressWrite = DateTime.MinValue;
        while (!lpProcess.WaitForExit(200))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                TryKill(lpProcess);
                throw new OperationCanceledException(cancellationToken);
            }

            ForwardProgress(lpProgressFile, progress, ref lpLastProgressWrite);
        }

        if (lpProcess.ExitCode != 0)
        {
            var lpReason = TryReadText(lpFailFile);
            throw new InvalidOperationException(
                lpReason.Length > 0
                    ? $"索引构建子进程失败：{lpReason}"
                    : $"索引构建子进程退出码 {lpProcess.ExitCode}");
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
        TryDelete(lpProgressFile);
        TryDelete(lpFailFile);
        return lpResult;
    }

    /// <summary>读取子进程写出的进度文件；内容没更新就跳过，避免每 200ms 重复解析。</summary>
    private static void ForwardProgress(
        string progressFile,
        Action<IndexBuildProgress>? progress,
        ref DateTime lastWrite)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(progressFile))
            {
                return;
            }

            var lpWriteTime = File.GetLastWriteTimeUtc(progressFile);
            if (lpWriteTime <= lastWrite)
            {
                return;
            }

            var lpJson = File.ReadAllText(progressFile);
            var lpParsed = System.Text.Json.JsonSerializer.Deserialize<IndexBuildProgress>(lpJson);
            if (lpParsed is null)
            {
                return;
            }

            lastWrite = lpWriteTime;
            progress(lpParsed);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            // 子进程正在写进度文件时可能读到半截 JSON，下一轮再读即可。
        }
    }

    private static string TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
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
