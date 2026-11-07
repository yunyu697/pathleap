using System.Net.Http;
using System.Security.Cryptography;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Update;

namespace Pathleap.UI.Update;

/// <summary>
/// 更新检查与下载：只读公开发布仓库的 <c>latest.json</c>，不携带任何令牌。
/// 清单必须 Ed25519 验签通过；安装包必须 SHA256 逐字节校验一致，否则拒绝安装。
/// 网络或校验失败时把原因原样交给界面，不做静默降级。
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static string HomepageUrl => ReleaseFeed.HomeUrl;

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ReleaseSigning.HasEmbeddedPublicKey)
            {
                return UpdateCheckResult.Failure(
                    "客户端未内置发布公钥（ReleaseSigning.PublicKeyBase64 仍为占位值），已中止更新检查；不降级为无签名安装。");
            }

            using var lpRequest = new HttpRequestMessage(HttpMethod.Get, ReleaseFeed.ManifestUrl);
            lpRequest.Headers.UserAgent.ParseAdd($"Pathleap/{Branding.Version}");
            lpRequest.Headers.Accept.ParseAdd("application/json, text/plain");

            using var lpResponse = await Http.SendAsync(lpRequest, cancellationToken).ConfigureAwait(false);
            if (!lpResponse.IsSuccessStatusCode)
            {
                var lpStatus = (int)lpResponse.StatusCode;
                var lpMessage = lpStatus == 404
                    ? $"公开发布仓库暂无最新清单（HTTP 404）：{ReleaseFeed.ManifestUrl}"
                    : $"发布仓库返回 HTTP {lpStatus}";
                AppLog.Warn($"更新检查失败: {lpMessage}");
                return UpdateCheckResult.Failure(lpMessage);
            }

            var lpJson = await lpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lpManifest = LatestManifest.Parse(lpJson);
            lpManifest.Validate();

            if (!ReleaseSigning.Verify(lpManifest))
            {
                AppLog.Warn($"更新清单验签失败: version={lpManifest.Version}");
                return UpdateCheckResult.Failure("更新清单验签失败：内容可能被篡改或公钥不匹配，已中止（不降级）。");
            }

            var lpAsset = lpManifest.PreferredAsset;
            if (lpAsset is null)
            {
                return UpdateCheckResult.Failure("清单未包含可用的安装包或便携包资产。");
            }

            var lpHasUpdate = SemanticVersion.IsNewer(lpManifest.Version, Branding.Version);
            AppLog.Info(
                $"更新检查完成: remote={lpManifest.Version} local={Branding.Version} update={lpHasUpdate} "
                + $"asset={lpAsset.Name} signature=ok");

            return new UpdateCheckResult
            {
                Ok = true,
                SignatureVerified = true,
                RemoteTag = lpManifest.Tag,
                RemoteVersion = lpManifest.Version,
                Notes = lpManifest.Notes,
                AssetName = lpAsset.Name,
                AssetSize = lpAsset.Size,
                AssetUrl = lpAsset.Url,
                AssetSha256 = lpAsset.Sha256,
                MinimumSystemText =
                    $"Windows {lpManifest.MinimumSystem.Windows} · .NET {lpManifest.MinimumSystem.DotnetDesktop} · "
                    + $"Windows App Runtime {lpManifest.MinimumSystem.WindowsAppRuntime}",
                HasUpdate = lpHasUpdate,
                Message = $"清单已验签 · 最低 Windows {lpManifest.MinimumSystem.Windows}"
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新检查失败: {ex.Message}");
            return UpdateCheckResult.Failure(ex.Message);
        }
    }

    /// <summary>下载并逐字节校验 SHA256；不一致或大小不符时删除临时文件并抛出，绝不启动安装。</summary>
    public static async Task DownloadAsync(
        UpdateCheckResult info,
        string destinationPath,
        Action<long, long> progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(info.AssetSha256))
        {
            throw new InvalidOperationException("清单未提供 SHA256，拒绝下载。");
        }

        var lpExpectedHash = info.AssetSha256.ToLowerInvariant();
        var lpPartial = destinationPath + ".part";

        using var lpRequest = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl);
        lpRequest.Headers.UserAgent.ParseAdd($"Pathleap/{Branding.Version}");

        using var lpResponse = await Http.SendAsync(lpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        lpResponse.EnsureSuccessStatusCode();

        var lpTotal = info.AssetSize > 0 ? info.AssetSize : lpResponse.Content.Headers.ContentLength ?? -1;
        var lpFolder = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(lpFolder))
        {
            Directory.CreateDirectory(lpFolder);
        }

        await using var lpSource = await lpResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var lpHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var lpBuffer = new byte[81920];
        var lpReceived = 0L;
        var lpLastReport = 0L;

        // 先把字节写入 .part，校验通过后再改名，避免半成品进入下载目录。
        await using (var lpTarget = new FileStream(lpPartial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            while (true)
            {
                var lpRead = await lpSource.ReadAsync(lpBuffer, cancellationToken).ConfigureAwait(false);
                if (lpRead <= 0)
                {
                    break;
                }

                lpHash.AppendData(lpBuffer, 0, lpRead);
                await lpTarget.WriteAsync(lpBuffer.AsMemory(0, lpRead), cancellationToken).ConfigureAwait(false);
                lpReceived += lpRead;
                if (lpReceived - lpLastReport > 256 * 1024)
                {
                    lpLastReport = lpReceived;
                    progress(lpReceived, lpTotal);
                }
            }

            progress(lpReceived, lpTotal);
        }

        var lpActualHash = Convert.ToHexString(lpHash.GetHashAndReset()).ToLowerInvariant();
        if (lpReceived != info.AssetSize || lpActualHash != lpExpectedHash)
        {
            TryDelete(lpPartial);
            throw new InvalidOperationException(
                $"安装包校验失败（大小 {lpReceived}/{info.AssetSize}，SHA256 {lpActualHash}，期望 {lpExpectedHash}），已删除下载文件并拒绝安装。");
        }

        File.Move(lpPartial, destinationPath, overwrite: true);
        AppLog.Info($"已下载并校验安装包: {destinationPath} sha256={lpActualHash}");
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
        catch (Exception ex)
        {
            AppLog.Warn($"删除未通过校验的下载文件失败: {ex.Message}");
        }
    }
}

public sealed class UpdateCheckResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool SignatureVerified { get; init; }

    public string RemoteTag { get; init; } = string.Empty;

    public string RemoteVersion { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public long AssetSize { get; init; }

    public string AssetUrl { get; init; } = string.Empty;

    public string AssetSha256 { get; init; } = string.Empty;

    public string MinimumSystemText { get; init; } = string.Empty;

    public bool HasUpdate { get; init; }

    public static UpdateCheckResult Failure(string message) => new() { Ok = false, Message = message };
}
