namespace Pathleap.Shared.Update;

/// <summary>
/// 公开发布仓库的唯一来源。客户端只读这里，且不携带任何令牌。
/// 源码仓库保持私有；公开发布仓库只放安装包、便携包与 latest.json。
/// </summary>
public static class ReleaseFeed
{
    public const string Owner = "yunyu697";

    public const string RepoName = "pathleap-releases";

    public const string Repository = Owner + "/" + RepoName;

    public const string HomeUrl = "https://github.com/" + Repository;

    /// <summary>最新正式版 Release 的固定下载地址（GitHub 302 到资源 CDN，无需认证）。</summary>
    public const string ManifestUrl =
        "https://github.com/" + Repository + "/releases/latest/download/latest.json";

    public const string AssetInstaller = "installer";

    public const string AssetPortable = "portable";

    public static readonly string[] AssetOrder = [AssetInstaller, AssetPortable];
}
