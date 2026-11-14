using Pathleap.Shared;
using Pathleap.Shared.Config;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.Service.Index;

/// <summary>
/// 用户配置（config.json）里的排除路径。按文件修改时间懒加载，查询时取一次快照：
/// 命中「等于该目录」或「以该目录 + 分隔符开头」的结果被过滤。
/// 这样设置页新增/删除规则后无需重建索引，下一次查询即生效。
/// </summary>
internal sealed class ExclusionRules
{
    private readonly string _lpConfigPath = AppPaths.ConfigFile;
    private readonly object _lpGate = new();
    private string[] _lpPrefixes = Array.Empty<string>();
    private DateTime _lpStamp = DateTime.MinValue;
    private bool _lpLoaded;

    /// <summary>规范化后的排除前缀（去掉末尾分隔符，不区分大小写去重）。</summary>
    public string[] GetPrefixes()
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
                    var lpBefore = _lpPrefixes.Length;
                    _lpPrefixes = Normalize(lpConfig.ExcludedPaths);
                    _lpStamp = lpStamp;
                    _lpLoaded = true;
                    if (_lpPrefixes.Length != lpBefore || lpBefore == 0)
                    {
                        AppLog.Info($"排除规则已加载：{_lpPrefixes.Length} 条" +
                                    (_lpPrefixes.Length == 0
                                        ? string.Empty
                                        : "（" + string.Join("；", _lpPrefixes) + "）"));
                    }
                }
            }
            catch (Exception ex)
            {
                // 配置读取失败不应打断搜索：退回「无排除」，并记日志。
                AppLog.Warn($"排除规则读取失败，按未配置排除处理: {ex.Message}");
            }

            return _lpPrefixes;
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
