namespace Pathleap.Shared.Update;

/// <summary>
/// 更新源的唯一来源。仓库已公开，客户端直接读主仓库的最新 Release，零令牌。
/// Release 资产由 tag 流水线构建并上传，包含安装包、便携包、latest.json 与 latest.json.sig。
/// </summary>
public static class ReleaseFeed
{
    public const string Owner = "yunyu697";

    public const string RepoName = "pathleap";

    public const string Repository = Owner + "/" + RepoName;

    public const string HomeUrl = "https://github.com/" + Repository;

    /// <summary>最新正式版 Release 的固定下载地址（GitHub 302 到资源 CDN，无需认证）。</summary>
    public const string ManifestUrl =
        "https://github.com/" + Repository + "/releases/latest/download/latest.json";

    /// <summary>分离签名文件名；清单内的 signature 字段供客户端直接验签。</summary>
    public const string SignatureFile = "latest.json.sig";

    public const string AssetInstaller = "installer";

    public const string AssetPortable = "portable";

    public static readonly string[] AssetOrder = [AssetInstaller, AssetPortable];

    /// <summary>
    /// GitHub Release 加速镜像前缀，按顺序尝试；全部失败后回退官方地址。
    /// 只影响公开资产的下载，不换取任何令牌，也不改动签名校验。
    /// </summary>
    private static readonly string[] DefaultMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/"
    ];

    /// <summary>环境变量：设为 <c>off</c> 关闭镜像；设为其它前缀值则只试该前缀后回退官方（便于诊断）。</summary>
    public const string MirrorEnvironmentVariable = "PATHLEAP_UPDATE_MIRROR";

    /// <summary>把同一个资源地址按「镜像 → 官方」展开，供调用方逐个尝试。</summary>
    public static IEnumerable<string> ExpandSources(string url)
    {
        var lpOverride = Environment.GetEnvironmentVariable(MirrorEnvironmentVariable)?.Trim();
        if (string.Equals(lpOverride, "off", StringComparison.OrdinalIgnoreCase) || lpOverride == "0")
        {
            yield return url;
            yield break;
        }

        IEnumerable<string> lpPrefixes = string.IsNullOrEmpty(lpOverride)
            ? DefaultMirrorPrefixes
            : [lpOverride.EndsWith('/') ? lpOverride : lpOverride + "/"];

        foreach (var lpPrefix in lpPrefixes)
        {
            if (Uri.TryCreate(lpPrefix + url, UriKind.Absolute, out var lpMirror) && lpMirror.Host.Length > 0)
            {
                yield return lpMirror.ToString();
            }
        }

        yield return url;
    }

    /// <summary>判断一个源是否是官方地址（用于日志与界面说明）。</summary>
    public static bool IsOfficialSource(string url) => url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);
}
