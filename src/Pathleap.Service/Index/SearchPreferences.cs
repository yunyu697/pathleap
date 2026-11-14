using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.Service.Index;

/// <summary>查询侧的用户偏好快照：排除前缀 + 是否包含已安装程序。</summary>
internal sealed record SearchPreferencesSnapshot(string[] ExcludedPrefixes, bool IncludeInstalledPrograms);

/// <summary>
/// 用户配置（config.json）里的查询偏好。按文件修改时间懒加载，查询时取一次快照：
/// 设置页改动后无需重启服务或重建索引，下一次查询即生效。
/// </summary>
internal sealed class SearchPreferences
{
    private readonly string _lpConfigPath = AppPaths.ConfigFile;
    private readonly object _lpGate = new();
    private SearchPreferencesSnapshot _lpSnapshot = new(Array.Empty<string>(), true);
    private DateTime _lpStamp = DateTime.MinValue;
    private bool _lpLoaded;

    public SearchPreferencesSnapshot Get()
    {
        lock (_lpGate)
        {
            try
            {
                var lpStamp = File.Exists(_lpConfigPath)
                    ? File.GetLastWriteTimeUtc(_lpConfigPath)
                    : DateTime.MinValue;
                if (!_lpLoaded || lpStamp != _lpStamp)
                {
                    var lpConfig = ConfigStore.Load(_lpConfigPath);
                    _lpSnapshot = new SearchPreferencesSnapshot(
                        Normalize(lpConfig.ExcludedPaths),
                        lpConfig.IncludeInstalledPrograms);
                    _lpStamp = lpStamp;
                    _lpLoaded = true;
                    AppLog.Info(
                        $"搜索偏好已加载：排除 {_lpSnapshot.ExcludedPrefixes.Length} 条，" +
                        $"已安装程序 {(_lpSnapshot.IncludeInstalledPrograms ? "开" : "关")}" +
                        (_lpSnapshot.ExcludedPrefixes.Length == 0
                            ? string.Empty
                            : "（" + string.Join("；", _lpSnapshot.ExcludedPrefixes) + "）"));
                }
            }
            catch (Exception ex)
            {
                // 配置读取失败不应打断搜索：退回「无排除、含程序」，并记日志。
                AppLog.Warn($"搜索偏好读取失败，按默认值处理: {ex.Message}");
            }

            return _lpSnapshot;
        }
    }

    /// <summary>路径是否落在任一排除前缀下（目录本身也算命中）。</summary>
    public static bool IsExcluded(string path, string[] prefixes)
    {
        foreach (var lpPrefix in prefixes)
        {
            if (path.Length < lpPrefix.Length
                || !path.StartsWith(lpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (path.Length == lpPrefix.Length || path[lpPrefix.Length] is '\\' or '/')
            {
                return true;
            }
        }

        return false;
    }

    private static string[] Normalize(IEnumerable<string>? paths)
    {
        var lpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpResult = new List<string>();
        foreach (var lpPath in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(lpPath))
            {
                continue;
            }

            var lpTrimmed = lpPath.Trim().TrimEnd('\\', '/');
            if (lpTrimmed.Length == 0)
            {
                continue;
            }

            if (lpSeen.Add(lpTrimmed))
            {
                lpResult.Add(lpTrimmed);
            }
        }

        return lpResult.ToArray();
    }
}
