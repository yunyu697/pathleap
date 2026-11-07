namespace Pathleap.Shared.Update;

/// <summary>
/// 清单验签入口。公钥内置在客户端；私钥只存在于发布流水线的密钥配置中。
/// 验签失败必须直接中止更新流程，不允许「跳过签名继续安装」。
/// </summary>
public static class ReleaseSigning
{
    /// <summary>
    /// 发布清单验签公钥（Ed25519，Base64，32 字节）。
    /// 替换方式见 docs/更新源与签名.md：运行发布工具 gen-key 后把 publicKeyBase64 粘到这里。
    /// </summary>
    public const string PublicKeyBase64 = "G9wbQU3gevloPnt2jdep/cO+bg3o1kGngNI+cI+6+1I=";

    public static bool HasEmbeddedPublicKey
    {
        get
        {
            try
            {
                return Convert.FromBase64String(PublicKeyBase64) is { Length: 32 } lpKey
                       && lpKey.Any(static b => b != 0);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool Verify(LatestManifest manifest) => Verify(manifest, PublicKeyBase64);

    public static bool Verify(LatestManifest manifest, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(manifest.Signature) || string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return false;
        }

        try
        {
            var lpPublicKey = Convert.FromBase64String(publicKeyBase64);
            var lpSignature = Convert.FromBase64String(manifest.Signature);
            var lpPayload = System.Text.Encoding.UTF8.GetBytes(manifest.BuildSignedContent());
            return Ed25519.Verify(lpPublicKey, lpPayload, lpSignature);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>发布工具用：对清单计算签名（会写入 <see cref="LatestManifest.Signature"/>）。</summary>
    public static void Sign(LatestManifest manifest, byte[] seed)
    {
        var lpPayload = System.Text.Encoding.UTF8.GetBytes(manifest.BuildSignedContent());
        manifest.Signature = Convert.ToBase64String(Ed25519.Sign(seed, lpPayload));
    }

    public static string GetPublicKeyBase64(byte[] seed) =>
        Convert.ToBase64String(Ed25519.GetPublicKey(seed));
}
