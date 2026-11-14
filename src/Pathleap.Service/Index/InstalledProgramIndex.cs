using System.Diagnostics;
using Microsoft.Win32;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>一条已安装程序：显示名、可启动路径（.lnk 或 .exe）、匹配别名。</summary>
internal sealed class InstalledProgram
{
    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>所有 token 都要命中显示名或别名。</summary>
    public bool Matches(string[] tokens)
    {
        foreach (var lpToken in tokens)
        {
            if (Name.Contains(lpToken, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lpAliasHit = false;
            foreach (var lpAlias in Aliases)
            {
                if (lpAlias.Contains(lpToken, StringComparison.OrdinalIgnoreCase))
                {
                    lpAliasHit = true;
                    break;
                }
            }

            if (!lpAliasHit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>精确 = 显示名/别名完全相等；前缀 = 显示名或别名以查询开头；其余为子串。</summary>
    public int Rank(string query)
    {
        if (Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return SearchEngine.TierExact;
        }

        foreach (var lpAlias in Aliases)
        {
            if (lpAlias.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                return SearchEngine.TierExact;
            }
        }

        if (Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return SearchEngine.TierPrefix;
        }

        foreach (var lpAlias in Aliases)
        {
            if (lpAlias.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return SearchEngine.TierPrefix;
            }
        }

        return SearchEngine.TierSubstring;
    }
}

/// <summary>
/// 已安装程序目录：开始菜单快捷方式（用户 + 公共）+ 注册表卸载项里能定位到可执行文件的条目。
/// 只在服务进程内建一次（首次查询时懒加载），结果与文件索引合并，不写入 .plidx。
/// 只收「双击就能启动」的条目：快捷方式交给 ShellExecute；注册表项要求 DisplayIcon 指向真实 .exe。
/// </summary>
internal sealed class InstalledProgramIndex
{
    private static readonly string[] UninstallSubKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    private readonly object _lpGate = new();
    private InstalledProgram[]? _lpPrograms;

    /// <summary>在程序目录里找匹配项；pathAllowed 用于套用设置页的排除规则。</summary>
    public List<SearchHit> Match(string query, int limit, Func<string, bool>? pathAllowed)
    {
        var lpPrograms = GetPrograms();
        if (lpPrograms.Length == 0)
        {
            return new List<SearchHit>();
        }

        var lpTokens = query.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lpTokens.Length == 0)
        {
            return new List<SearchHit>();
        }

        var lpCap = Math.Clamp(limit, 1, 200) * 4;
        var lpMatched = new List<SearchHit>(Math.Min(lpCap, 32));
        foreach (var lpProgram in lpPrograms)
        {
            if (pathAllowed is not null && !pathAllowed(lpProgram.Path))
            {
                continue;
            }

            if (!lpProgram.Matches(lpTokens))
            {
                continue;
            }

            lpMatched.Add(new SearchHit
            {
                Name = lpProgram.Name,
                Path = lpProgram.Path,
                IsProgram = true,
                Tier = lpProgram.Rank(query),
                Depth = 0
            });

            if (lpMatched.Count >= lpCap)
            {
                break;
            }
        }

        return lpMatched
            .OrderByDescending(static lpHit => lpHit.Tier)
            .ThenBy(static lpHit => lpHit.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    /// <summary>首次查询时枚举一次；失败源只记日志，不阻断另一来源。</summary>
    private InstalledProgram[] GetPrograms()
    {
        lock (_lpGate)
        {
            return _lpPrograms ??= Build();
        }
    }

    private static InstalledProgram[] Build()
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpSeenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpSeenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lpPrograms = new List<InstalledProgram>(256);

        var lpShortcuts = 0;
        foreach (var lpRoot in StartMenuRoots())
        {
            foreach (var lpFile in EnumerateShortcuts(lpRoot))
            {
                var lpName = System.IO.Path.GetFileNameWithoutExtension(lpFile).Trim();
                if (lpName.Length == 0 || IsNoiseName(lpName))
                {
                    continue;
                }

                if (!lpSeenPaths.Add(lpFile) || !lpSeenNames.Add(lpName))
                {
                    continue;
                }

                lpPrograms.Add(new InstalledProgram { Name = lpName, Path = lpFile });
                lpShortcuts++;
            }
        }

        var lpRegistry = 0;
        foreach (var lpEntry in EnumerateRegistryEntries())
        {
            if (!lpSeenPaths.Add(lpEntry.Path) || !lpSeenNames.Add(lpEntry.Name))
            {
                continue;
            }

            lpPrograms.Add(lpEntry);
            lpRegistry++;
        }

        var lpElapsedMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
        AppLog.Info(
            $"已安装程序目录：{lpPrograms.Count} 条（开始菜单 {lpShortcuts}，注册表 {lpRegistry}），" +
            $"耗时 {lpElapsedMs:0.0}ms");
        return lpPrograms.ToArray();
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        // 用户自己的开始菜单优先，同名快捷方式以用户目录为准。
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            }).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"开始菜单枚举失败: {root} ({ex.Message})");
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<InstalledProgram> EnumerateRegistryEntries()
    {
        var lpHives = new[] { Registry.CurrentUser, Registry.LocalMachine };
        foreach (var lpHive in lpHives)
        {
            foreach (var lpSubKey in UninstallSubKeys)
            {
                foreach (var lpEntry in ReadUninstallKey(lpHive, lpSubKey))
                {
                    yield return lpEntry;
                }
            }
        }
    }

    private static IEnumerable<InstalledProgram> ReadUninstallKey(RegistryKey hive, string subKey)
    {
        RegistryKey? lpRoot = null;
        try
        {
            lpRoot = hive.OpenSubKey(subKey);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Debug($"注册表卸载项不可读: {hive.Name}\\{subKey} ({ex.Message})");
        }

        if (lpRoot is null)
        {
            yield break;
        }

        using (lpRoot)
        {
            string[] lpNames;
            try
            {
                lpNames = lpRoot.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                yield break;
            }

            foreach (var lpName in lpNames)
            {
                var lpEntry = ReadEntry(lpRoot, lpName);
                if (lpEntry is not null)
                {
                    yield return lpEntry;
                }
            }
        }
    }

    private static InstalledProgram? ReadEntry(RegistryKey root, string subKeyName)
    {
        try
        {
            using var lpEntry = root.OpenSubKey(subKeyName);
            if (lpEntry is null)
            {
                return null;
            }

            // 跳过系统组件、补丁与服务包：它们不是可启动的应用。
            if (lpEntry.GetValue("SystemComponent") is int lpSystemComponent && lpSystemComponent == 1)
            {
                return null;
            }

            if (lpEntry.GetValue("ReleaseType") is string lpReleaseType
                && (lpReleaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                    || lpReleaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                    || lpReleaseType.Contains("ServicePack", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (lpEntry.GetValue("ParentKeyName") is string lpParent && lpParent.Length > 0)
            {
                return null;
            }

            if (lpEntry.GetValue("DisplayName") is not string lpDisplayName
                || string.IsNullOrWhiteSpace(lpDisplayName))
            {
                return null;
            }

            lpDisplayName = lpDisplayName.Trim();
            if (IsNoiseName(lpDisplayName))
            {
                return null;
            }

            var lpExe = ResolveLaunchExe(lpEntry.GetValue("DisplayIcon") as string);
            if (lpExe is null || IsNoiseExecutable(lpExe))
            {
                return null;
            }

            return new InstalledProgram
            {
                Name = lpDisplayName,
                Path = lpExe,
                Aliases = new[] { System.IO.Path.GetFileNameWithoutExtension(lpExe) }
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// DisplayIcon 形如 <c>"C:\App\app.exe",0</c> 或 <c>C:\App\app.exe,0</c>。
    /// 只认指向真实 .exe 的值；.ico/.dll 与不存在的路径都不算「能打开」。
    /// </summary>
    private static string? ResolveLaunchExe(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var lpValue = displayIcon.Trim();
        if (lpValue.StartsWith('"'))
        {
            var lpQuoteEnd = lpValue.IndexOf('"', 1);
            if (lpQuoteEnd <= 1)
            {
                return null;
            }

            lpValue = lpValue[1..lpQuoteEnd];
        }
        else
        {
            var lpComma = lpValue.LastIndexOf(',');
            if (lpComma > 0 && int.TryParse(lpValue[(lpComma + 1)..].Trim(), out _))
            {
                lpValue = lpValue[..lpComma];
            }
        }

        lpValue = Environment.ExpandEnvironmentVariables(lpValue.Trim());
        if (!lpValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || lpValue.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return File.Exists(lpValue) ? lpValue : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsNoiseName(string name) =>
        name.StartsWith("卸载", StringComparison.Ordinal)
        || name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoiseExecutable(string exePath)
    {
        var lpName = System.IO.Path.GetFileNameWithoutExtension(exePath);
        return lpName.Equals("msiexec", StringComparison.OrdinalIgnoreCase)
               || lpName.Equals("rundll32", StringComparison.OrdinalIgnoreCase)
               || lpName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
               || lpName.StartsWith("setup", StringComparison.OrdinalIgnoreCase)
               || lpName.StartsWith("install", StringComparison.OrdinalIgnoreCase)
               || lpName.StartsWith("update", StringComparison.OrdinalIgnoreCase);
    }
}
