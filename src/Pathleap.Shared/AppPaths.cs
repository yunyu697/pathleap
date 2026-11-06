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

    /// <summary>服务数据根。控制台模式使用当前用户目录；M2 的 SCM 模式改用 ProgramData。</summary>
    public static string ServiceRoot { get; } =
        Environment.GetEnvironmentVariable("PATHLEAP_DATA_ROOT")
        ?? Path.Combine(UserRoot, "service");

    /// <summary>服务配置（M1 仅记录模式与日志级别，不含索引参数）。</summary>
    public static string ServiceConfigFile { get; } = Path.Combine(ServiceRoot, "service.json");

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
