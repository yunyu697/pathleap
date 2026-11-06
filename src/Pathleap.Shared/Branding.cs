namespace Pathleap.Shared;

using System.Reflection;

/// <summary>
/// 产品标识的唯一集中定义点。
/// 改名时只需要修改 <see cref="ProductId"/>，其余互斥体名、管道名、配置目录、日志前缀全部自动跟随。
/// 注意：程序集名 / 命名空间仍保留 Pathleap，属于工程代号，改名不强制。
/// </summary>
public static class Branding
{
    /// <summary>英文产品名，也是目录、互斥体、管道、日志文件名的来源。</summary>
    public const string ProductId = "Pathleap";

    /// <summary>中文名，仅用于界面展示与文档。</summary>
    public const string ChineseName = "跃径";

    /// <summary>展示名（当前与英文名一致）。</summary>
    public const string DisplayName = ProductId;

    /// <summary>
    /// 程序内版本号。唯一来源是仓库根目录的 Directory.Build.props（<c>PathleapVersion</c>），
    /// 经 AssemblyInformationalVersion 进入程序集；界面、日志、IPC 状态、基准报告都读这里，
    /// 不允许在代码里另写版本字符串。
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var lpAssembly = Assembly.GetEntryAssembly() ?? typeof(Branding).Assembly;
        var lpInformational = lpAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(lpInformational))
        {
            return lpInformational;
        }

        return lpAssembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>品牌标记字符（品牌点里显示的字母，由英文名首字母派生）。</summary>
    public static string Mark => ProductId.Length > 0 ? ProductId[..1].ToUpperInvariant() : "?";

    public const string MutexPrefix = @"Local\";

    /// <summary>UI 单实例互斥体（按会话隔离，允许多用户同时登录）。</summary>
    public static string UiMutexName => $"{MutexPrefix}{ProductId}.UI";

    /// <summary>UI 单实例控制管道（SHOW / BENCH / STATUS / QUIT）。</summary>
    public static string UiPipeName => $"{ProductId}.UI";

    /// <summary>索引服务互斥体（控制台模式下防止重复启动）。</summary>
    public static string ServiceMutexName => $"{MutexPrefix}{ProductId}.Service";

    /// <summary>索引服务查询管道。</summary>
    public static string ServicePipeName => $"{ProductId}.Service";

    /// <summary>服务名（M2 安装 SCM 服务时使用）。</summary>
    public static string ServiceName => $"{ProductId}Service";
}
