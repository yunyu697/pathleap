using System.Text;
using Pathleap.Shared.Ipc;

namespace Pathleap.Shared.Update;

/// <summary>
/// 公开发布仓库的 <c>latest.json</c> 清单。签名覆盖 <see cref="BuildSignedContent"/> 的字节，
/// 客户端只用签名文本覆盖字段，避免「清单里有些字段没签」的缺口。
/// </summary>
public sealed class LatestManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string Version { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public string ReleasedAt { get; set; } = string.Empty;

    public string Channel { get; set; } = "stable";

    /// <summary>平台/架构标识；当前只发布 win-x64。</summary>
    public string Platform { get; set; } = "win-x64";

    public MinimumSystem MinimumSystem { get; set; } = new();

    public string Notes { get; set; } = string.Empty;

    /// <summary>资产键：<c>installer</c>、<c>portable</c>，顺序固定。</summary>
    public Dictionary<string, ManifestAsset> Assets { get; set; } = new();

    public string Signature { get; set; } = string.Empty;

    public ManifestAsset? PreferredAsset =>
        Assets.TryGetValue(ReleaseFeed.AssetInstaller, out var lpInstaller) && lpInstaller.IsUsable
            ? lpInstaller
            : Assets.TryGetValue(ReleaseFeed.AssetPortable, out var lpPortable) && lpPortable.IsUsable
                ? lpPortable
                : null;

    public bool HasPortableAsset =>
        Assets.TryGetValue(ReleaseFeed.AssetPortable, out var lpPortable) && lpPortable.IsUsable;

    /// <summary>
    /// 构造参与签名的固定文本。字段顺序、分隔符与换行都固定，签名端（发布工具）与验签端（客户端）
    /// 共用这一份实现，不依赖 JSON 序列化细节。
    /// </summary>
    public string BuildSignedContent()
    {
        var lpBuilder = new StringBuilder();
        lpBuilder.Append("pathleap-manifest-v1\n");
        lpBuilder.Append("version=").Append(Version).Append('\n');
        lpBuilder.Append("tag=").Append(Tag).Append('\n');
        lpBuilder.Append("releasedAt=").Append(ReleasedAt).Append('\n');
        lpBuilder.Append("channel=").Append(Channel).Append('\n');
        lpBuilder.Append("platform=").Append(Platform).Append('\n');
        lpBuilder.Append("minimumSystem.windows=").Append(MinimumSystem.Windows).Append('\n');
        lpBuilder.Append("minimumSystem.dotnetDesktop=").Append(MinimumSystem.DotnetDesktop).Append('\n');
        lpBuilder.Append("minimumSystem.windowsAppRuntime=").Append(MinimumSystem.WindowsAppRuntime).Append('\n');

        foreach (var lpKey in ReleaseFeed.AssetOrder)
        {
            if (!Assets.TryGetValue(lpKey, out var lpAsset) || !lpAsset.IsUsable)
            {
                continue;
            }

            lpBuilder.Append("asset.").Append(lpKey).Append(".name=").Append(lpAsset.Name).Append('\n');
            lpBuilder.Append("asset.").Append(lpKey).Append(".size=").Append(lpAsset.Size).Append('\n');
            lpBuilder.Append("asset.").Append(lpKey).Append(".sha256=").Append(lpAsset.Sha256.ToLowerInvariant()).Append('\n');
            lpBuilder.Append("asset.").Append(lpKey).Append(".url=").Append(lpAsset.Url).Append('\n');
        }

        // 更新说明放在最后：多行文本不会影响前面「一行一个字段」的结构。
        lpBuilder.Append("notes=").Append((Notes ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'));
        return lpBuilder.ToString();
    }

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, JsonDefaults.Options);

    public static LatestManifest Parse(string json)
    {
        var lpManifest = System.Text.Json.JsonSerializer.Deserialize<LatestManifest>(json, JsonDefaults.Options)
                         ?? throw new FormatException("清单为空或不是合法 JSON");
        return lpManifest;
    }

    /// <summary>结构校验：只保留能安全用于下载与展示的部分。</summary>
    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new FormatException($"不支持的清单版本：schemaVersion={SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(Version))
        {
            throw new FormatException("清单缺少 version");
        }

        if (Platform != "win-x64")
        {
            throw new FormatException($"清单平台不匹配：{Platform}（当前只支持 win-x64）");
        }

        if (string.IsNullOrWhiteSpace(Signature))
        {
            throw new FormatException("清单缺少 signature");
        }

        foreach (var lpEntry in Assets)
        {
            var lpAsset = lpEntry.Value;
            if (!lpAsset.IsUsable)
            {
                throw new FormatException($"资产 {lpEntry.Key} 信息不完整（name/sha256/url 必填）");
            }

            if (lpAsset.Sha256.Length != 64 || !lpAsset.Sha256.All(char.IsAsciiHexDigit))
            {
                throw new FormatException($"资产 {lpEntry.Key} 的 sha256 不是 64 位十六进制");
            }

            if (!Uri.TryCreate(lpAsset.Url, UriKind.Absolute, out var lpUri) || lpUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new FormatException($"资产 {lpEntry.Key} 的 url 必须是 https");
            }
        }
    }
}

public sealed class MinimumSystem
{
    public string Windows { get; set; } = "10.0.17763.0";

    public string DotnetDesktop { get; set; } = "8.0";

    public string WindowsAppRuntime { get; set; } = "2.6.5";
}

public sealed class ManifestAsset
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(Sha256)
        && !string.IsNullOrWhiteSpace(Url);
}
