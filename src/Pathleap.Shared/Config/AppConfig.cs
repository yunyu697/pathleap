namespace Pathleap.Shared.Config;

/// <summary>
/// 用户配置模型（JSON，camelCase）。字段只增不减；未知字段在读取时忽略，写回时保留不了（M1 简化）。
/// 默认值即产品默认值，所有字段都必须有默认值。
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;

    /// <summary>产品标识，用于检测配置是否被别的产品复用。</summary>
    public string Product { get; set; } = Branding.ProductId;

    /// <summary>
    /// 全局唤出方式。默认双击 Ctrl（连续按两次、不叠加其它键），由低层键盘钩子判定；
    /// 也可写成 Ctrl+Alt+Space 这类 RegisterHotKey 组合键（见设置页预置清单）。
    /// </summary>
    public string UiHotkey { get; set; } = "Ctrl+Ctrl";

    /// <summary>快速切换浮层的全局热键。默认 Ctrl+G；在文件对话框打开时唤出可直接注入路径。</summary>
    public string QuickSwitchHotkey { get; set; } = "Ctrl+G";

    /// <summary>UI 启动时若服务管道不可用，是否自动拉起服务控制台进程（开发期便利，M2 由 SCM 接管）。</summary>
    public bool StartServiceWithUi { get; set; } = true;

    /// <summary>窗口失焦是否自动隐藏。</summary>
    public bool HideOnDeactivate { get; set; } = true;

    public int WindowWidth { get; set; } = 660;

    public int WindowHeight { get; set; } = 436;

    /// <summary>主题：System / Light / Dark。</summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>强调色（#RRGGBB），同步到选中态与高亮片段。</summary>
    public string AccentColor { get; set; } = "#3D6EF7";

    /// <summary>窗口不透明度（0.6–1.0）；与磨砂背景搭配使用。</summary>
    public double WindowOpacity { get; set; } = 0.92;

    /// <summary>是否启用磨砂（DesktopAcrylicBackdrop）。</summary>
    public bool AcrylicEnabled { get; set; } = true;

    /// <summary>结果列表密度：Compact / Standard / Comfortable。</summary>
    public string ResultDensity { get; set; } = "Standard";

    /// <summary>搜索窗是否固定置顶（对应主窗口工具条上的图钉）。</summary>
    public bool PinWindow { get; set; } = true;

    /// <summary>
    /// 是否始终以管理员身份运行。开启后启动路径改为「计划任务（最高权限）」，
    /// 不使用 Run 注册表项；任务注册/删除各需要一次 UAC。
    /// </summary>
    public bool AlwaysRunAsAdmin { get; set; }

    /// <summary>启动时自动检查更新（只做一次 HTTP 查询，不下载）。</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>更新通道：stable / preview（preview 未接入，界面会标注）。</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>下载镜像：github（当前唯一可用）。</summary>
    public string DownloadMirror { get; set; } = "github";

    /// <summary>上次检查更新的时间（ISO 8601），供界面展示。</summary>
    public string LastUpdateCheck { get; set; } = string.Empty;

    /// <summary>自动检查的间隔小时数：24=每天，168=每周，0=仅手动。</summary>
    public int UpdateCheckHours { get; set; } = 24;

    /// <summary>Debug / Info / Warn / Error。</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>基准测试默认采样数。</summary>
    public int BenchSamples { get; set; } = 200;

    /// <summary>基准测试用于确认“输入已就绪”的探测键（单字符数字键，避开输入法组合态）。</summary>
    public string BenchProbeKey { get; set; } = "7";

    public void Normalize()
    {
        if (SchemaVersion < 2)
        {
            // v2：唤醒方式默认由 Ctrl+Alt+Space 改为双击 Ctrl；用户自定义过的其它值原样保留。
            if (string.Equals(UiHotkey, "Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase))
            {
                UiHotkey = "Ctrl+Ctrl";
            }

            SchemaVersion = 2;
        }

        if (string.IsNullOrWhiteSpace(UiHotkey))
        {
            UiHotkey = "Ctrl+Ctrl";
        }

        if (string.IsNullOrWhiteSpace(QuickSwitchHotkey))
        {
            QuickSwitchHotkey = "Ctrl+G";
        }

        WindowWidth = Math.Clamp(WindowWidth, 560, 900);
        WindowHeight = Math.Clamp(WindowHeight, 320, 700);
        BenchSamples = Math.Clamp(BenchSamples, 20, 2000);

        if (ThemeMode is not ("System" or "Light" or "Dark"))
        {
            ThemeMode = "Dark";
        }

        if (!IsHexColor(AccentColor))
        {
            AccentColor = "#3D6EF7";
        }

        WindowOpacity = Math.Clamp(WindowOpacity, 0.6, 1.0);

        if (ResultDensity is not ("Compact" or "Standard" or "Comfortable"))
        {
            ResultDensity = "Standard";
        }

        if (UpdateChannel is not ("stable" or "preview"))
        {
            UpdateChannel = "stable";
        }

        if (DownloadMirror is not "github")
        {
            DownloadMirror = "github";
        }

        UpdateCheckHours = Math.Clamp(UpdateCheckHours, 0, 168);

        if (BenchProbeKey.Length != 1 || !char.IsAsciiDigit(BenchProbeKey[0]))
        {
            BenchProbeKey = "7";
        }

        if (LogLevel is not ("Debug" or "Info" or "Warn" or "Error"))
        {
            LogLevel = "Info";
        }

        if (!string.Equals(Product, Branding.ProductId, StringComparison.Ordinal))
        {
            Product = Branding.ProductId;
        }
    }

    private static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        return value.Skip(1).All(static c => Uri.IsHexDigit(c));
    }
}
