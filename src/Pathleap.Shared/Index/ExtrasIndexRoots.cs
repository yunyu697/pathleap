using Pathleap.Shared.Config;

namespace Pathleap.Shared.Index;

/// <summary>
/// 主索引（整卷 USN / 遍历）之外的附加索引根：目前来自「网络位置」。
/// 只返回当前存在的目录；已断开的映射盘、离线共享会被跳过并留给调用方记日志。
/// </summary>
public static class ExtrasIndexRoots
{
    /// <summary>按配置解析附加索引根，去重并保持顺序：映射盘在前，手动添加的共享次之，附加目录在后。</summary>
    public static IReadOnlyList<string> Resolve(AppConfig config)
    {
        var lpRoots = new List<string>();
        var lpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.IncludeNetworkLocations)
        {
            foreach (var lpDriveRoot in MappedNetworkDriveRoots())
            {
                AddIfUsable(lpRoots, lpSeen, lpDriveRoot);
            }

            foreach (var lpLocation in config.NetworkLocations)
            {
                AddIfUsable(lpRoots, lpSeen, lpLocation);
            }
        }

        foreach (var lpDirectory in config.IndexDirectories)
        {
            AddIfUsable(lpRoots, lpSeen, lpDirectory);
        }

        return PruneNestedRoots(lpRoots);
    }

    /// <summary>去掉被其他根包含的根：同时添加 C:\Work 与 C:\Work\Sub 时只扫外层，避免重复结果。</summary>
    private static IReadOnlyList<string> PruneNestedRoots(List<string> roots)
    {
        if (roots.Count <= 1)
        {
            return roots;
        }

        var lpKept = new List<string>(roots.Count);
        foreach (var lpRoot in roots.OrderBy(static lpPath => lpPath.Length))
        {
            var lpCovered = lpKept.Any(lpKeptRoot =>
                lpRoot.Equals(lpKeptRoot, StringComparison.OrdinalIgnoreCase)
                || lpRoot.StartsWith(lpKeptRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            if (!lpCovered)
            {
                lpKept.Add(lpRoot);
            }
        }

        return lpKept;
    }

    private static IEnumerable<string> MappedNetworkDriveRoots()
    {
        DriveInfo[] lpDrives;
        try
        {
            lpDrives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var lpDrive in lpDrives)
        {
            if (lpDrive.DriveType != DriveType.Network)
            {
                continue;
            }

            var lpRoot = lpDrive.RootDirectory.FullName;
            bool lpReady;
            try
            {
                lpReady = lpDrive.IsReady && Directory.Exists(lpRoot);
            }
            catch (IOException)
            {
                lpReady = false;
            }

            if (lpReady)
            {
                yield return lpRoot;
            }
        }
    }

    private static void AddIfUsable(List<string> roots, HashSet<string> seen, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        string lpFullPath;
        try
        {
            lpFullPath = Path.GetFullPath(candidate.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        lpFullPath = lpFullPath.TrimEnd('\\');
        if (lpFullPath.Length == 2 && lpFullPath[1] == ':')
        {
            lpFullPath += "\\";
        }

        if (lpFullPath.Length == 0 || !Directory.Exists(lpFullPath))
        {
            return;
        }

        if (seen.Add(lpFullPath))
        {
            roots.Add(lpFullPath);
        }
    }
}
