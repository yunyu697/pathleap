namespace Pathleap.Shared.Config;

/// <summary>
/// 用户配置模型（JSON，camelCase）。字段只增不减；未知字段在读取时忽略，写回时保留不了（M1 简化）。
/// 默认值即产品默认值，所有字段都必须有默认值。
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>产品标识，用于检测配置是否被别的产品复用。</summary>
    public string Product { get; set; } = Branding.ProductId;

    /// <summary>全局唤出热键。M1 临时使用 Ctrl+Alt+Space（双击 Ctrl 需要 LL 钩子，属后续里程碑）。</summary>
    public string UiHotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>UI 启动时若服务管道不可用，是否自动拉起服务控制台进程（开发期便利，M2 由 SCM 接管）。</summary>
    public bool StartServiceWithUi { get; set; } = true;

    /// <summary>窗口失焦是否自动隐藏。</summary>
    public bool HideOnDeactivate { get; set; } = true;

    public int WindowWidth { get; set; } = 660;

    public int WindowHeight { get; set; } = 436;

    /// <summary>Debug / Info / Warn / Error。</summary>
    public string LogLevel { get; set; } = "Info";

    /// <summary>基准测试默认采样数。</summary>
    public int BenchSamples { get; set; } = 200;

    /// <summary>基准测试用于确认“输入已就绪”的探测键（单字符数字键，避开输入法组合态）。</summary>
    public string BenchProbeKey { get; set; } = "7";

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(UiHotkey))
        {
            UiHotkey = "Ctrl+Alt+Space";
        }

        WindowWidth = Math.Clamp(WindowWidth, 560, 900);
        WindowHeight = Math.Clamp(WindowHeight, 320, 700);
        BenchSamples = Math.Clamp(BenchSamples, 20, 2000);

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
}
