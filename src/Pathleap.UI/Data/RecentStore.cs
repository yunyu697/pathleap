using System.Text.Json;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Data;

/// <summary>
/// 最近访问记录：只记录用户真正打开过的条目（主窗口选出后打开时写入），
/// 用于空输入时的「最近访问」首页与快速切换的文件夹列表。没有历史时不做任何伪造数据。
/// </summary>
public static class RecentStore
{
    private const int MaxEntries = 24;
    private static readonly object Gate = new();
    private static List<RecentEntry>? _lpCache;

    public static event Action? Updated;

    public static IReadOnlyList<RecentEntry> Load()
    {
        lock (Gate)
        {
            if (_lpCache is not null)
            {
                return _lpCache.ToList();
            }

            try
            {
                if (File.Exists(AppPaths.RecentFile))
                {
                    var lpJson = File.ReadAllText(AppPaths.RecentFile);
                    _lpCache = JsonSerializer.Deserialize<List<RecentEntry>>(lpJson, JsonDefaults.Options) ?? new List<RecentEntry>();
                }
                else
                {
                    _lpCache = new List<RecentEntry>();
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"最近访问记录读取失败，按空列表处理: {ex.Message}");
                _lpCache = new List<RecentEntry>();
            }

            return _lpCache.ToList();
        }
    }

    public static void Record(string path, string name, bool isDirectory)
    {
        lock (Gate)
        {
            var lpEntries = _lpCache is null ? Load().ToList() : _lpCache.ToList();
            lpEntries.RemoveAll(lpEntry => string.Equals(lpEntry.Path, path, StringComparison.OrdinalIgnoreCase));
            lpEntries.Insert(0, new RecentEntry
            {
                Name = name,
                Path = path,
                IsDirectory = isDirectory,
                AccessedAt = DateTimeOffset.Now
            });

            if (lpEntries.Count > MaxEntries)
            {
                lpEntries.RemoveRange(MaxEntries, lpEntries.Count - MaxEntries);
            }

            _lpCache = lpEntries;
            Save(lpEntries);
        }

        Updated?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _lpCache = new List<RecentEntry>();
            try
            {
                if (File.Exists(AppPaths.RecentFile))
                {
                    File.Delete(AppPaths.RecentFile);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"清空最近访问失败: {ex.Message}");
            }
        }

        Updated?.Invoke();
    }

    /// <summary>最近打开的文件夹（目录取自身，文件取所在目录），按最近时间倒序且去重。</summary>
    public static IReadOnlyList<RecentEntry> RecentFolders(int limit)
    {
        var lpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpResult = new List<RecentEntry>();
        foreach (var lpEntry in Load())
        {
            var lpFolder = lpEntry.IsDirectory ? lpEntry.Path : Path.GetDirectoryName(lpEntry.Path);
            if (string.IsNullOrEmpty(lpFolder) || !lpSeen.Add(lpFolder))
            {
                continue;
            }

            lpResult.Add(new RecentEntry
            {
                Name = Path.GetFileName(lpFolder.TrimEnd('\\')) is { Length: > 0 } lpLeaf ? lpLeaf : lpFolder,
                Path = lpFolder,
                IsDirectory = true,
                AccessedAt = lpEntry.AccessedAt
            });

            if (lpResult.Count >= limit)
            {
                break;
            }
        }

        return lpResult;
    }

    private static void Save(List<RecentEntry> entries)
    {
        try
        {
            var lpParent = Path.GetDirectoryName(AppPaths.RecentFile);
            if (!string.IsNullOrEmpty(lpParent))
            {
                AppPaths.EnsureDirectory(lpParent);
            }

            var lpTemp = AppPaths.RecentFile + ".tmp";
            File.WriteAllText(lpTemp, JsonSerializer.Serialize(entries, JsonDefaults.Options));
            File.Move(lpTemp, AppPaths.RecentFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"最近访问记录写入失败: {ex.Message}");
        }
    }
}

public sealed class RecentEntry
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public DateTimeOffset AccessedAt { get; set; }
}
