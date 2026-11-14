using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>一条浏览器条目：书签或历史记录。匹配只在标题 / URL 上做，不联网。</summary>
internal sealed class BrowserEntry
{
    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public bool IsBookmark { get; init; }

    public long LastVisitUnixMs { get; init; }

    /// <summary>所有 token 都要命中标题或 URL。</summary>
    public bool Matches(string[] tokens)
    {
        foreach (var lpToken in tokens)
        {
            if (!Title.Contains(lpToken, StringComparison.OrdinalIgnoreCase)
                && !Url.Contains(lpToken, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>标题精确 / 标题前缀 / 其余为子串。书签在同 tier 内排历史之前。</summary>
    public int Rank(string query)
    {
        if (Title.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return SearchEngine.TierExact;
        }

        if (Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return SearchEngine.TierPrefix;
        }

        return SearchEngine.TierSubstring;
    }
}

/// <summary>
/// 浏览器书签与历史目录：Chrome / Edge 的用户配置目录，书签读 Bookmarks(JSON)，
/// 历史读 History(SQLite，经 winsqlite3.dll 只读打开一份临时副本，避开浏览器文件锁)。
/// 只在服务进程内懒加载一次；任一来源失败只记日志，不影响另一来源与文件搜索。
/// </summary>
internal sealed class BrowserIndex
{
    private const int HistoryLimit = 4000;

    /// <summary>浏览器时间戳起点：1601-01-01 UTC，单位微秒。</summary>
    private static readonly DateTimeOffset WebKitEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _lpGate = new();
    private BrowserEntry[]? _lpEntries;

    /// <summary>在浏览器目录里找匹配项；结果按 tier 排序后取前 limit 条。</summary>
    public List<SearchHit> Match(string query, int limit)
    {
        var lpEntries = GetEntries();
        if (lpEntries.Length == 0)
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

        var lpTake = Math.Clamp(limit, 1, 200);
        var lpHits = new List<SearchHit>(Math.Min(lpTake * 2, 32));
        foreach (var lpEntry in lpEntries)
        {
            if (!lpEntry.Matches(lpTokens))
            {
                continue;
            }

            lpHits.Add(new SearchHit
            {
                Name = lpEntry.Title,
                Path = lpEntry.Url,
                IsBrowser = true,
                Tier = lpEntry.Rank(query),
                Score = lpEntry.IsBookmark ? 1 : 0,
                Depth = 0
            });
        }

        return lpHits
            .OrderByDescending(static lpHit => lpHit.Tier)
            .ThenByDescending(static lpHit => lpHit.Score)
            .Take(lpTake)
            .ToList();
    }

    private BrowserEntry[] GetEntries()
    {
        lock (_lpGate)
        {
            return _lpEntries ??= Build();
        }
    }

    private static BrowserEntry[] Build()
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpByUrl = new Dictionary<string, BrowserEntry>(StringComparer.OrdinalIgnoreCase);
        var lpBookmarks = 0;
        var lpHistory = 0;

        foreach (var lpRoot in ProfileRoots())
        {
            foreach (var lpEntry in ReadBookmarks(lpRoot))
            {
                // 同一 URL 既是书签又有历史时保留书签（名字更可能是用户起的）。
                lpByUrl[lpEntry.Url] = lpEntry;
                lpBookmarks++;
            }

            foreach (var lpEntry in ReadHistory(lpRoot))
            {
                if (!lpByUrl.ContainsKey(lpEntry.Url))
                {
                    lpByUrl[lpEntry.Url] = lpEntry;
                    lpHistory++;
                }
            }
        }

        var lpElapsedMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
        AppLog.Info(
            $"浏览器目录：{lpByUrl.Count} 条（书签 {lpBookmarks}，历史 {lpHistory}），耗时 {lpElapsedMs:0.0}ms");
        return lpByUrl.Values.ToArray();
    }

    private static IEnumerable<string> ProfileRoots()
    {
        var lpLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var lpBrowser in new[] { "Google\\Chrome\\User Data", "Microsoft\\Edge\\User Data" })
        {
            var lpUserData = System.IO.Path.Combine(lpLocal, lpBrowser);
            if (!Directory.Exists(lpUserData))
            {
                continue;
            }

            // Default + Profile 1..N：每个配置目录都有独立的 Bookmarks / History。
            IEnumerable<string> lpProfiles;
            try
            {
                lpProfiles = Directory.EnumerateDirectories(lpUserData)
                    .Where(static lpDir =>
                    {
                        var lpName = System.IO.Path.GetFileName(lpDir);
                        return lpName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                               || lpName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Debug($"浏览器配置目录枚举失败: {lpUserData} ({ex.Message})");
                continue;
            }

            foreach (var lpProfile in lpProfiles)
            {
                yield return lpProfile;
            }
        }
    }

    private static IEnumerable<BrowserEntry> ReadBookmarks(string profileRoot)
    {
        var lpFile = System.IO.Path.Combine(profileRoot, "Bookmarks");
        if (!File.Exists(lpFile))
        {
            return Array.Empty<BrowserEntry>();
        }

        var lpResult = new List<BrowserEntry>();
        try
        {
            using var lpDocument = JsonDocument.Parse(File.ReadAllText(lpFile));
            if (lpDocument.RootElement.TryGetProperty("roots", out var lpRoots))
            {
                foreach (var lpRoot in lpRoots.EnumerateObject())
                {
                    WalkBookmarkNode(lpRoot.Value, lpResult);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            AppLog.Debug($"书签读取失败: {lpFile} ({ex.Message})");
            return Array.Empty<BrowserEntry>();
        }

        return lpResult;
    }

    private static void WalkBookmarkNode(JsonElement node, List<BrowserEntry> target)
    {
        if (!node.TryGetProperty("type", out var lpType))
        {
            return;
        }

        if (string.Equals(lpType.GetString(), "url", StringComparison.OrdinalIgnoreCase))
        {
            var lpUrl = node.TryGetProperty("url", out var lpUrlElement) ? lpUrlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(lpUrl)
                || !(lpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || lpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var lpName = node.TryGetProperty("name", out var lpNameElement) ? lpNameElement.GetString() : null;
            target.Add(new BrowserEntry
            {
                Title = string.IsNullOrWhiteSpace(lpName) ? lpUrl : lpName.Trim(),
                Url = lpUrl,
                IsBookmark = true
            });
            return;
        }

        if (node.TryGetProperty("children", out var lpChildren) && lpChildren.ValueKind == JsonValueKind.Array)
        {
            foreach (var lpChild in lpChildren.EnumerateArray())
            {
                WalkBookmarkNode(lpChild, target);
            }
        }
    }

    private static IEnumerable<BrowserEntry> ReadHistory(string profileRoot)
    {
        var lpHistory = System.IO.Path.Combine(profileRoot, "History");
        if (!File.Exists(lpHistory))
        {
            return Array.Empty<BrowserEntry>();
        }

        var lpCopy = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pathleap-history-{Environment.ProcessId}-{Guid.NewGuid():N}.db");
        try
        {
            CopyShared(lpHistory, lpCopy);
            return QueryHistory(lpCopy);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            AppLog.Warn($"历史记录读取失败: {lpHistory} ({ex.Message})");
            return Array.Empty<BrowserEntry>();
        }
        finally
        {
            TryDelete(lpCopy);
        }
    }

    /// <summary>浏览器以只读共享打开数据库时也能拷出一份；直接 File.Copy 在独占锁下会失败。</summary>
    private static void CopyShared(string source, string destination)
    {
        using var lpInput = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var lpOutput = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        lpInput.CopyTo(lpOutput, 1 << 16);
    }

    private static IEnumerable<BrowserEntry> QueryHistory(string databasePath)
    {
        var lpResult = new List<BrowserEntry>(256);
        if (NativeSqlite.OpenReadOnly(databasePath) is not { } lpDatabase)
        {
            return lpResult;
        }

        using (lpDatabase)
        {
            var lpSql =
                "SELECT title, url, last_visit_time FROM urls " +
                "WHERE title IS NOT NULL AND title <> '' " +
                "ORDER BY last_visit_time DESC LIMIT " + HistoryLimit;
            foreach (var (lpTitle, lpUrl, lpLastVisit) in lpDatabase.Query(lpSql))
            {
                if (string.IsNullOrWhiteSpace(lpTitle) || string.IsNullOrWhiteSpace(lpUrl))
                {
                    continue;
                }

                var lpVisited = 0L;
                try
                {
                    if (lpLastVisit > 0)
                    {
                        lpVisited = WebKitEpoch.AddTicks(lpLastVisit * 10).ToUnixTimeMilliseconds();
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    lpVisited = 0;
                }

                lpResult.Add(new BrowserEntry
                {
                    Title = lpTitle.Trim(),
                    Url = lpUrl,
                    IsBookmark = false,
                    LastVisitUnixMs = lpVisited
                });
            }
        }

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
            // 临时库留到系统清理即可。
        }
    }

    /// <summary>
    /// winsqlite3.dll（Windows 自带）的最小只读封装：只跑一条 SELECT，逐行取前三列文本。
    /// 用系统 SQLite 是为了不引入 NuGet 依赖，避免离线构建失败。
    /// </summary>
    private sealed class NativeSqlite : IDisposable
    {
        private const int Ok = 0;
        private const int Row = 100;
        private const int OpenReadOnlyFlag = 0x1;

        private IntPtr _lpDatabase;

        private NativeSqlite(IntPtr database)
        {
            _lpDatabase = database;
        }

        public static NativeSqlite? OpenReadOnly(string path)
        {
            var lpResult = sqlite3_open_v2(path, out var lpDatabase, OpenReadOnlyFlag, IntPtr.Zero);
            if (lpResult != Ok || lpDatabase == IntPtr.Zero)
            {
                if (lpDatabase != IntPtr.Zero)
                {
                    sqlite3_close(lpDatabase);
                }

                AppLog.Warn($"无法只读打开浏览器数据库: {path}（sqlite={lpResult}）");
                return null;
            }

            return new NativeSqlite(lpDatabase);
        }

        public IEnumerable<(string Title, string Url, long LastVisit)> Query(string sql)
        {
            if (sqlite3_prepare_v2(_lpDatabase, sql, -1, out var lpStatement, IntPtr.Zero) != Ok
                || lpStatement == IntPtr.Zero)
            {
                AppLog.Warn($"浏览器数据库查询准备失败（sqlite={sqlite3_errcode(_lpDatabase)}）");
                yield break;
            }

            try
            {
                while (sqlite3_step(lpStatement) == Row)
                {
                    yield return (
                        ColumnText(lpStatement, 0),
                        ColumnText(lpStatement, 1),
                        sqlite3_column_int64(lpStatement, 2));
                }
            }
            finally
            {
                sqlite3_finalize(lpStatement);
            }
        }

        private static string ColumnText(IntPtr statement, int column)
        {
            var lpText = sqlite3_column_text(statement, column);
            if (lpText == IntPtr.Zero)
            {
                return string.Empty;
            }

            var lpLength = sqlite3_column_bytes(statement, column);
            return lpLength <= 0 ? string.Empty : Marshal.PtrToStringUTF8(lpText, lpLength) ?? string.Empty;
        }

        public void Dispose()
        {
            if (_lpDatabase != IntPtr.Zero)
            {
                sqlite3_close(_lpDatabase);
                _lpDatabase = IntPtr.Zero;
            }
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database,
            int flags,
            IntPtr vfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(
            IntPtr database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int bytes,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_errcode(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);
    }
}
