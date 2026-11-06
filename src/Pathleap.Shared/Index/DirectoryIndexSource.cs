using System.Diagnostics;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.Shared.Index;

/// <summary>
/// 降级索引源：目录遍历。用于非 NTFS 卷、无卷句柄权限（非管理员）等拿不到 USN 的场景。
/// 相比 USN 慢且要真实 stat，但不需要特权；遍历时跳过重解析点，避免符号链接成环。
/// </summary>
public static class DirectoryIndexSource
{
    public static IndexBuildResult Build(
        string rootPath,
        string outputPath,
        Action<int>? progress,
        CancellationToken cancellationToken,
        int progressStep = 50_000)
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpBuilder = new IndexBuilder();
        var lpSkipped = 0;

        var lpRootFull = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        var lpPathRoot = Path.GetPathRoot(lpRootFull) ?? lpRootFull;
        var lpVolumeLabel = lpPathRoot.TrimEnd(Path.DirectorySeparatorChar);

        // 根节点 = 卷标签；再从卷根到遍历起点补齐祖先链，否则子节点路径会丢掉中间层级。
        var lpRootIndex = lpBuilder.Add(new IndexNode(lpVolumeLabel, IndexFormat.NoParent, 0, IndexFormat.FlagDirectory, 0, 0));
        var lpParentIndex = lpRootIndex;
        var lpDepth = 0;
        var lpRelative = lpRootFull.Substring(lpPathRoot.Length);
        foreach (var lpSegment in lpRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            lpDepth++;
            lpParentIndex = lpBuilder.Add(
                new IndexNode(lpSegment, lpParentIndex, lpDepth, IndexFormat.FlagDirectory, 0, 0));
        }

        // 广度优先：队列里存 (路径, 节点下标, 深度)，父下标一次算好，落盘不用再解析。
        var lpQueue = new Queue<(string Path, int Index, int Depth)>(1024);
        lpQueue.Enqueue((lpRootFull, lpParentIndex, lpDepth));

        while (lpQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (lpPath, lpIndex, lpCurrentDepth) = lpQueue.Dequeue();

            IEnumerable<FileSystemInfo> lpChildren;
            try
            {
                lpChildren = new DirectoryInfo(lpPath).EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0,
                    ReturnSpecialDirectories = false
                });
            }
            catch (Exception ex)
            {
                lpSkipped++;
                AppLog.Debug($"目录不可枚举，跳过: {lpPath} ({ex.GetType().Name})");
                continue;
            }

            foreach (var lpChild in lpChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var lpIsDirectory = (lpChild.Attributes & FileAttributes.Directory) != 0;
                    var lpIsReparse = (lpChild.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (lpIsReparse)
                    {
                        // 重解析点不入索引，避免链接/挂载点造成环路与重复。
                        lpSkipped++;
                        continue;
                    }

                    var lpFlags = lpIsDirectory ? IndexFormat.FlagDirectory : (byte)0;
                    if ((lpChild.Attributes & FileAttributes.Hidden) != 0)
                    {
                        lpFlags |= IndexFormat.FlagHidden;
                    }

                    if ((lpChild.Attributes & FileAttributes.System) != 0)
                    {
                        lpFlags |= IndexFormat.FlagSystem;
                    }

                    var lpSize = 0L;
                    var lpModified = 0L;
                    if (lpChild is FileInfo lpFile)
                    {
                        lpSize = lpFile.Length;
                        lpModified = lpFile.LastWriteTimeUtc.ToFileTimeUtc();
                    }
                    else if (lpChild is DirectoryInfo lpDir)
                    {
                        lpModified = lpDir.LastWriteTimeUtc.ToFileTimeUtc();
                    }

                    var lpChildIndex = lpBuilder.Add(new IndexNode(lpChild.Name, lpIndex, lpCurrentDepth + 1, lpFlags, lpSize, lpModified));
                    if (lpIsDirectory)
                    {
                        lpQueue.Enqueue((lpChild.FullName, lpChildIndex, lpCurrentDepth + 1));
                    }

                    if (lpBuilder.Count % progressStep == 0)
                    {
                        progress?.Invoke(lpBuilder.Count);
                    }
                }
                catch (Exception ex)
                {
                    lpSkipped++;
                    AppLog.Debug($"条目读取失败，跳过: {lpChild.FullName} ({ex.GetType().Name})");
                }
            }
        }

        progress?.Invoke(lpBuilder.Count);
        var lpBuildMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
        var lpSource = $"walk:{lpRootFull}";
        AppLog.Info($"目录遍历完成：{lpBuilder.Count} 条，跳过 {lpSkipped}，耗时 {lpBuildMs:0}ms");

        return lpBuilder.Save(outputPath, lpSource, lpSkipped, lpBuildMs);
    }
}
