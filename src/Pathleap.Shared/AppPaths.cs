namespace Pathleap.Shared;

/// <summary>
/// 所有落盘位置的唯一来源。任何脚本 / 文档都应引用这里定义的布局，不要在别处硬编码。
/// </summary>
public static class AppPaths
{
    /// <summary>当前用户数据根：%LOCALAPPDATA%\Pathleap。</summary>
    public static string UserRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Branding.ProductId);

    /// <summary>用户配置（JSON，原子写入）。</summary>
    public static string ConfigFile { get; } = Path.Combine(UserRoot, "config.json");

    /// <summary>日志目录（UI / Service 各自一个每日文件）。</summary>
    public static string LogsRoot { get; } = Path.Combine(UserRoot, "logs");

    /// <summary>性能实测数据目录（CSV + JSON）。</summary>
    public static string PerfRoot { get; } = Path.Combine(UserRoot, "perf");

    /// <summary>最近访问记录（用户实际打开过的条目，按时间倒序，最多 24 条）。</summary>
    public static string RecentFile { get; } = Path.Combine(UserRoot, "recent.json");

    /// <summary>更新包下载目录（只由用户显式点击「更新 / 仅下载」时写入）。</summary>
    public static string UpdatesRoot { get; } = Path.Combine(UserRoot, "updates");

    /// <summary>服务数据根。控制台模式使用当前用户目录；M2 的 SCM 模式改用 ProgramData。</summary>
    public static string ServiceRoot { get; } =
        Environment.GetEnvironmentVariable("PATHLEAP_DATA_ROOT")
        ?? Path.Combine(UserRoot, "service");

    /// <summary>服务配置（M1 仅记录模式与日志级别，不含索引参数）。</summary>
    public static string ServiceConfigFile { get; } = Path.Combine(ServiceRoot, "service.json");

    /// <summary>索引快照目录（每卷一个 .plidx 文件）。</summary>
    public static string IndexRoot { get; } = Path.Combine(ServiceRoot, "index");

    /// <summary>某个卷/根的索引文件名，如 <c>index\C.plidx</c>。</summary>
    public static string IndexFile(string volumeLabel)
    {
        var lpSafe = new string(volumeLabel.Where(static c => char.IsLetterOrDigit(c)).ToArray());
        return Path.Combine(IndexRoot, (lpSafe.Length == 0 ? "default" : lpSafe) + ".plidx");
    }

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
