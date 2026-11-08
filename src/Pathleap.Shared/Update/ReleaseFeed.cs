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
}
