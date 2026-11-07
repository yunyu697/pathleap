using System.Net.Http;
using System.Text.Json;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Update;

/// <summary>
/// 更新检查与下载：直接查询 GitHub Releases（公开 API，无需凭据）。
/// 只使用真实返回的数据；网络失败时把错误原样交给界面，不做静默伪造。
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/yunyu697/pathleap/releases/latest";
    private const string HomeUrl = "https://github.com/yunyu697/pathleap";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static string HomepageUrl => HomeUrl;

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var lpRequest = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            lpRequest.Headers.UserAgent.ParseAdd($"Pathleap/{Branding.Version}");
            lpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var lpResponse = await Http.SendAsync(lpRequest, cancellationToken).ConfigureAwait(false);
            if (!lpResponse.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failure($"GitHub 返回 HTTP {(int)lpResponse.StatusCode}");
            }

            var lpJson = await lpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var lpDocument = JsonDocument.Parse(lpJson);
            var lpRoot = lpDocument.RootElement;

            var lpTag = lpRoot.TryGetProperty("tag_name", out var lpTagElement) ? lpTagElement.GetString() ?? string.Empty : string.Empty;
            var lpNotes = lpRoot.TryGetProperty("body", out var lpBodyElement) ? lpBodyElement.GetString() ?? string.Empty : string.Empty;

            var lpAssetName = string.Empty;
            var lpAssetSize = 0L;
            var lpAssetUrl = string.Empty;
            if (lpRoot.TryGetProperty("assets", out var lpAssets) && lpAssets.ValueKind == JsonValueKind.Array)
            {
                foreach (var lpAsset in lpAssets.EnumerateArray())
                {
                    var lpName = lpAsset.TryGetProperty("name", out var lpNameElement) ? lpNameElement.GetString() ?? string.Empty : string.Empty;
                    if (lpName.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        lpAssetName = lpName;
                        lpAssetSize = lpAsset.TryGetProperty("size", out var lpSizeElement) ? lpSizeElement.GetInt64() : 0;
                        lpAssetUrl = lpAsset.TryGetProperty("browser_download_url", out var lpUrlElement) ? lpUrlElement.GetString() ?? string.Empty : string.Empty;
                        break;
                    }
                }

                // 没有安装包时回退到便携 zip；安装按钮会在界面上标注不可用。
                if (string.IsNullOrEmpty(lpAssetName))
                {
                    foreach (var lpAsset in lpAssets.EnumerateArray())
                    {
                        var lpName = lpAsset.TryGetProperty("name", out var lpNameElement) ? lpNameElement.GetString() ?? string.Empty : string.Empty;
                        if (lpName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            lpAssetName = lpName;
                            lpAssetSize = lpAsset.TryGetProperty("size", out var lpSizeElement) ? lpSizeElement.GetInt64() : 0;
                            lpAssetUrl = lpAsset.TryGetProperty("browser_download_url", out var lpUrlElement) ? lpUrlElement.GetString() ?? string.Empty : string.Empty;
                            break;
                        }
                    }
                }
            }

            var lpRemoteVersion = lpTag.TrimStart('v', 'V');
            var lpHasUpdate = CompareVersion(lpRemoteVersion, Branding.Version) > 0;
            AppLog.Info($"更新检查完成: remote={lpRemoteVersion} local={Branding.Version} update={lpHasUpdate} asset={lpAssetName}");

            return new UpdateCheckResult
            {
                Ok = true,
                RemoteTag = lpTag,
                RemoteVersion = lpRemoteVersion,
                Notes = lpNotes,
                AssetName = lpAssetName,
                AssetSize = lpAssetSize,
                AssetUrl = lpAssetUrl,
                HasUpdate = lpHasUpdate
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"更新检查失败: {ex.Message}");
            return UpdateCheckResult.Failure(ex.Message);
        }
    }

    public static async Task DownloadAsync(
        UpdateCheckResult info,
        string destinationPath,
        Action<long, long> progress,
        CancellationToken cancellationToken = default)
    {
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
        await using var lpTarget = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var lpBuffer = new byte[81920];
        var lpReceived = 0L;
        var lpLastReport = 0L;
        while (true)
        {
            var lpRead = await lpSource.ReadAsync(lpBuffer, cancellationToken).ConfigureAwait(false);
            if (lpRead <= 0)
            {
                break;
            }

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

    /// <summary>语义版本比较；只比较数字段，后缀按预发布规则视为更旧。</summary>
    private static int CompareVersion(string left, string right)
    {
        var lpLeft = ParseParts(left);
        var lpRight = ParseParts(right);
        var lpMax = Math.Max(lpLeft.Count, lpRight.Count);
        for (var lpIndex = 0; lpIndex < lpMax; lpIndex++)
        {
            var lpL = lpIndex < lpLeft.Count ? lpLeft[lpIndex] : 0;
            var lpR = lpIndex < lpRight.Count ? lpRight[lpIndex] : 0;
            if (lpL != lpR)
            {
                return lpL > lpR ? 1 : -1;
            }
        }

        return 0;
    }

    private static List<int> ParseParts(string version)
    {
        var lpClean = version.Split('-', '+')[0];
        var lpParts = new List<int>();
        foreach (var lpPart in lpClean.Split('.'))
        {
            lpParts.Add(int.TryParse(lpPart, out var lpValue) ? lpValue : 0);
        }

        return lpParts;
    }
}

public sealed class UpdateCheckResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = string.Empty;

    public string RemoteTag { get; init; } = string.Empty;

    public string RemoteVersion { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string AssetName { get; init; } = string.Empty;

    public long AssetSize { get; init; }

    public string AssetUrl { get; init; } = string.Empty;

    public bool HasUpdate { get; init; }

    public static UpdateCheckResult Failure(string message) => new() { Ok = false, Message = message };
}
