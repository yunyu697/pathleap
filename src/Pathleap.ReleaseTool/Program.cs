using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pathleap.Shared;
using Pathleap.Shared.Ipc;
using Pathleap.Shared.Update;

namespace Pathleap.ReleaseTool;

/// <summary>
/// 发布工具：生成/签名 latest.json、生成签名密钥、跑本地自检。
/// 只在发布流水线与本地 dry-run 使用，不随安装包分发。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var lpCommand = args.Length > 0 ? args[0] : "help";
        var lpOptions = ParseOptions(args.Skip(1).ToArray());

        try
        {
            switch (lpCommand)
            {
                case "selftest":
                    return RunSelfTest();
                case "gen-key":
                    return GenerateKey(lpOptions);
                case "create":
                    return CreateManifest(lpOptions);
                case "verify":
                    return VerifyManifest(lpOptions);
                default:
                    PrintHelp();
                    return lpCommand == "help" ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Pathleap.ReleaseTool

              selftest                 校验 Ed25519/版本比较/清单签名，全部通过退出 0
              gen-key [--out-seed PATH] 生成 Ed25519 seed 与公钥（seed 必须只存到发布流水线密钥）
              create --version 1.0.5 --tag v1.0.5 --notes-file docs/1.0.5-发布说明.md
                     --installer dist/xxx-setup.exe --portable dist/xxx.zip
                     --out dist/latest.json (--seed-file PATH | --seed-base64 BASE64)
                     [--repo owner/name] [--released-at ISO8601] [--channel stable] [--platform win-x64]
                     [--installer-url URL] [--portable-url URL] [--sig-out dist/latest.json.sig]
              verify --manifest dist/latest.json [--public-key BASE64]
            """);
    }

    private static int GenerateKey(Dictionary<string, string> options)
    {
        var lpSeed = Ed25519.GenerateSeed();
        var lpSeedBase64 = Convert.ToBase64String(lpSeed);
        var lpPublicBase64 = ReleaseSigning.GetPublicKeyBase64(lpSeed);

        Console.WriteLine($"publicKeyBase64={lpPublicBase64}");
        Console.WriteLine($"seedBase64={lpSeedBase64}");
        Console.WriteLine("把 publicKeyBase64 填入 src/Pathleap.Shared/Update/ReleaseSigning.cs；");
        Console.WriteLine("把 seedBase64 存入发布仓库的 secret PATHLEAP_SIGNING_KEY，切勿提交到源码。");
        Console.WriteLine("注意：发布私钥已固定，发版流程不得生成新钥；仅明确换钥时使用本命令，且必须先发布内置新公钥的客户端。");

        var lpOut = options.GetValueOrDefault("out-seed");
        if (!string.IsNullOrWhiteSpace(lpOut))
        {
            File.WriteAllText(lpOut, lpSeedBase64, new UTF8Encoding(false));
            Console.WriteLine($"seed 已写入 {lpOut}（该文件不得提交，用后删除）");
        }

        return 0;
    }

    private static int CreateManifest(Dictionary<string, string> options)
    {
        var lpVersion = Require(options, "version");
        var lpTag = options.GetValueOrDefault("tag", "v" + lpVersion);
        var lpRepo = options.GetValueOrDefault("repo", ReleaseFeed.Repository);
        var lpChannel = options.GetValueOrDefault("channel", "stable");
        var lpReleasedAt = options.GetValueOrDefault(
            "released-at",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        var lpOut = Require(options, "out");

        var lpSeed = ReadSeed(options);
        if (!HasEmbeddedKey())
        {
            throw new InvalidOperationException(
                "客户端未内置发布公钥（ReleaseSigning.PublicKeyBase64 为空或占位值），拒绝签名发布。");
        }

        var lpPublicExpected = ReleaseSigning.GetPublicKeyBase64(lpSeed);
        if (lpPublicExpected != ReleaseSigning.PublicKeyBase64)
        {
            throw new InvalidOperationException(
                "签名 seed 与客户端内置公钥不一致，已中止：本项目固定使用同一把发布私钥，发布流程不得临时生成新钥。"
                + "确需换钥时必须先发布内置新公钥的客户端版本，再发布用新钥签名的清单。");
        }

        var lpNotesFile = options.GetValueOrDefault("notes-file");
        var lpNotes = options.GetValueOrDefault("notes", string.Empty);
        if (!string.IsNullOrWhiteSpace(lpNotesFile) && File.Exists(lpNotesFile))
        {
            lpNotes = File.ReadAllText(lpNotesFile, Encoding.UTF8);
        }

        var lpManifest = new LatestManifest
        {
            Version = lpVersion,
            Tag = lpTag,
            ReleasedAt = lpReleasedAt,
            Channel = lpChannel,
            Platform = options.GetValueOrDefault("platform", "win-x64"),
            Notes = lpNotes,
            MinimumSystem = new MinimumSystem
            {
                Windows = options.GetValueOrDefault("minimum-windows", "10.0.17763.0"),
                DotnetDesktop = options.GetValueOrDefault("minimum-dotnet", "8.0"),
                WindowsAppRuntime = options.GetValueOrDefault("minimum-windows-app-runtime", "2.6.5")
            }
        };

        AddAsset(lpManifest, ReleaseFeed.AssetInstaller, options, "installer", lpRepo, lpTag);
        AddAsset(lpManifest, ReleaseFeed.AssetPortable, options, "portable", lpRepo, lpTag);

        ReleaseSigning.Sign(lpManifest, lpSeed);
        lpManifest.Validate();

        var lpJson = JsonSerializer.Serialize(
            lpManifest,
            new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true });
        var lpDirectory = Path.GetDirectoryName(Path.GetFullPath(lpOut));
        if (!string.IsNullOrEmpty(lpDirectory))
        {
            Directory.CreateDirectory(lpDirectory);
        }

        File.WriteAllText(lpOut, lpJson + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"清单已生成并签名: {lpOut}");

        var lpSigOut = options.GetValueOrDefault("sig-out");
        if (!string.IsNullOrWhiteSpace(lpSigOut))
        {
            var lpSigPath = Path.GetFullPath(lpSigOut);
            var lpSigDir = Path.GetDirectoryName(lpSigPath);
            if (!string.IsNullOrEmpty(lpSigDir))
            {
                Directory.CreateDirectory(lpSigDir);
            }

            File.WriteAllText(lpSigPath, lpManifest.Signature + Environment.NewLine, new UTF8Encoding(false));
            Console.WriteLine($"分离签名已写入: {lpSigPath}");
        }

        Console.WriteLine($"  version={lpVersion} tag={lpTag} channel={lpChannel}");
        foreach (var lpEntry in lpManifest.Assets)
        {
            Console.WriteLine($"  {lpEntry.Key}: {lpEntry.Value.Name} size={lpEntry.Value.Size} sha256={lpEntry.Value.Sha256}");
        }

        return 0;
    }

    private static int VerifyManifest(Dictionary<string, string> options)
    {
        var lpPath = Require(options, "manifest");
        var lpPublic = options.GetValueOrDefault("public-key", ReleaseSigning.PublicKeyBase64);
        var lpManifest = LatestManifest.Parse(File.ReadAllText(lpPath, Encoding.UTF8));
        lpManifest.Validate();
        var lpOk = ReleaseSigning.Verify(lpManifest, lpPublic);
        Console.WriteLine(lpOk ? $"验签通过: {lpPath}（version={lpManifest.Version}）" : $"验签失败: {lpPath}");
        return lpOk ? 0 : 1;
    }

    private static void AddAsset(
        LatestManifest manifest,
        string key,
        Dictionary<string, string> options,
        string optionName,
        string repo,
        string tag)
    {
        var lpPath = options.GetValueOrDefault(optionName);
        if (string.IsNullOrWhiteSpace(lpPath))
        {
            return;
        }

        if (!File.Exists(lpPath))
        {
            throw new FileNotFoundException($"资产不存在: {lpPath}", lpPath);
        }

        var lpName = Path.GetFileName(lpPath);
        var lpUrl = options.GetValueOrDefault(optionName + "-url")
                    ?? $"https://github.com/{repo}/releases/download/{tag}/{lpName}";
        manifest.Assets[key] = new ManifestAsset
        {
            Name = lpName,
            Size = new FileInfo(lpPath).Length,
            Sha256 = ComputeSha256(lpPath),
            Url = lpUrl
        };
    }

    private static string ComputeSha256(string path)
    {
        using var lpStream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(lpStream)).ToLowerInvariant();
    }

    private static byte[] ReadSeed(Dictionary<string, string> options)
    {
        var lpBase64 = options.GetValueOrDefault("seed-base64");
        var lpFile = options.GetValueOrDefault("seed-file");
        if (!string.IsNullOrWhiteSpace(lpFile))
        {
            lpBase64 = File.ReadAllText(lpFile, Encoding.UTF8).Trim();
        }

        if (string.IsNullOrWhiteSpace(lpBase64))
        {
            throw new InvalidOperationException("缺少签名密钥：用 --seed-file 或 --seed-base64 提供，未签名清单一律拒绝发布。");
        }

        var lpSeed = Convert.FromBase64String(lpBase64);
        if (lpSeed.Length != 32)
        {
            throw new FormatException("签名 seed 必须是 32 字节（Base64 编码前）");
        }

        return lpSeed;
    }

    private static bool HasEmbeddedKey()
    {
        try
        {
            return Convert.FromBase64String(ReleaseSigning.PublicKeyBase64) is { Length: 32 } lpKey
                   && lpKey.Any(static b => b != 0);
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var lpOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var lpIndex = 0; lpIndex < args.Length; lpIndex++)
        {
            var lpArg = args[lpIndex];
            if (!lpArg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var lpKey = lpArg[2..];
            if (lpIndex + 1 < args.Length && !args[lpIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                lpOptions[lpKey] = args[++lpIndex];
            }
            else
            {
                lpOptions[lpKey] = string.Empty;
            }
        }

        return lpOptions;
    }

    private static string Require(Dictionary<string, string> options, string key)
    {
        var lpValue = options.GetValueOrDefault(key);
        if (string.IsNullOrWhiteSpace(lpValue))
        {
            throw new InvalidOperationException($"缺少参数 --{key}");
        }

        return lpValue;
    }

    private static int RunSelfTest()
    {
        var lpFailures = 0;
        void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name + (detail.Length == 0 ? string.Empty : " — " + detail));
            if (!condition)
            {
                lpFailures++;
            }
        }

        // RFC 8032 测试向量 1、2（空消息 / 单字节消息）。
        const string lpSeed1 = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
        const string lpPublic1 = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
        const string lpSignature1 = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";
        var lpSeedBytes1 = Convert.FromHexString(lpSeed1);
        var lpPublicBytes1 = Convert.FromHexString(lpPublic1);
        var lpSignatureBytes1 = Convert.FromHexString(lpSignature1);
        var lpDerived1 = Convert.ToHexString(Ed25519.GetPublicKey(lpSeedBytes1));
        Check("RFC8032 向量1 公钥派生", lpDerived1.ToLowerInvariant() == lpPublic1, lpDerived1);
        Check("RFC8032 向量1 签名", Convert.ToHexString(Ed25519.Sign(lpSeedBytes1, [])).ToLowerInvariant() == lpSignature1);
        Check("RFC8032 向量1 验签", Ed25519.Verify(lpPublicBytes1, [], lpSignatureBytes1));

        const string lpSeed2 = "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb";
        const string lpPublic2 = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
        const string lpMessage2 = "72";
        const string lpSignature2 = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";
        var lpSeedBytes2 = Convert.FromHexString(lpSeed2);
        var lpPublicBytes2 = Convert.FromHexString(lpPublic2);
        var lpMessageBytes2 = Convert.FromHexString(lpMessage2);
        var lpSignatureBytes2 = Convert.FromHexString(lpSignature2);
        Check("RFC8032 向量2 公钥派生", Convert.ToHexString(Ed25519.GetPublicKey(lpSeedBytes2)).ToLowerInvariant() == lpPublic2);
        Check("RFC8032 向量2 验签", Ed25519.Verify(lpPublicBytes2, lpMessageBytes2, lpSignatureBytes2));

        var lpRandomSeed = Ed25519.GenerateSeed();
        var lpRandomPublic = Ed25519.GetPublicKey(lpRandomSeed);
        var lpMessage = Encoding.UTF8.GetBytes("pathleap-selftest");
        var lpSignature = Ed25519.Sign(lpRandomSeed, lpMessage);
        Check("随机密钥 签名验签", Ed25519.Verify(lpRandomPublic, lpMessage, lpSignature));
        Check("篡改消息拒绝", !Ed25519.Verify(lpRandomPublic, Encoding.UTF8.GetBytes("pathleap-selftesT"), lpSignature));
        var lpTamperedSignature = lpSignature.ToArray();
        lpTamperedSignature[5] ^= 0x01;
        Check("篡改签名拒绝", !Ed25519.Verify(lpRandomPublic, lpMessage, lpTamperedSignature));
        Check("错误公钥拒绝", !Ed25519.Verify(lpPublicBytes1, lpMessage, lpSignature));

        // 版本比较边界（用户要求的四点）。
        Check("边界 等于最新", SemanticVersion.Compare("1.0.4", "1.0.4") == 0 && !SemanticVersion.IsNewer("1.0.4", "1.0.4"));
        Check("边界 本地更高", SemanticVersion.Compare("1.0.4", "1.0.5") < 0 && !SemanticVersion.IsNewer("1.0.4", "1.0.5"));
        Check("边界 最新为预发布", SemanticVersion.IsNewer("1.0.5-alpha1", "1.0.4"));
        Check("边界 同版预发布低于正式版", SemanticVersion.Compare("1.0.4-alpha2", "1.0.4") < 0);
        Check("边界 正式版高于同版预发布", SemanticVersion.IsNewer("1.0.4", "1.0.4-alpha2"));
        Check("边界 1.0.10 高于 1.0.9", SemanticVersion.Compare("1.0.10", "1.0.9") > 0);
        Check("预发布数字段按数值", SemanticVersion.Compare("1.0.4-alpha.2", "1.0.4-alpha.10") < 0);
        Check("预发布短段优先级低", SemanticVersion.Compare("1.0.4-alpha", "1.0.4-alpha.1") < 0);
        Check("非法版本抛出", TryCompareThrows());

        // 清单签名与 JSON 往返：签名只覆盖固定文本，JSON 排版不影响验签。
        var lpManifest = new LatestManifest
        {
            Version = "1.0.5",
            Tag = "v1.0.5",
            ReleasedAt = "2026-11-08T00:00:00Z",
            Channel = "stable",
            Notes = "第一行\r\n第二行\n第三行",
            MinimumSystem = new MinimumSystem(),
            Assets = new Dictionary<string, ManifestAsset>
            {
                [ReleaseFeed.AssetInstaller] = new()
                {
                    Name = "Pathleap-1.0.5-win-x64-setup.exe",
                    Size = 8_831_614,
                    Sha256 = new string('a', 64),
                    Url = "https://github.com/yunyu697/pathleap/releases/download/v1.0.5/Pathleap-1.0.5-win-x64-setup.exe"
                },
                [ReleaseFeed.AssetPortable] = new()
                {
                    Name = "Pathleap-1.0.5-win-x64.zip",
                    Size = 11_461_968,
                    Sha256 = new string('b', 64),
                    Url = "https://github.com/yunyu697/pathleap/releases/download/v1.0.5/Pathleap-1.0.5-win-x64.zip"
                }
            }
        };
        ReleaseSigning.Sign(lpManifest, lpRandomSeed);
        var lpJson = lpManifest.ToJson();
        var lpReparsed = LatestManifest.Parse(lpJson);
        Check("清单 JSON 往返字段一致", lpReparsed.BuildSignedContent() == lpManifest.BuildSignedContent());
        Check("清单验签通过", ReleaseSigning.Verify(lpReparsed, ReleaseSigning.GetPublicKeyBase64(lpRandomSeed)));
        var lpTampered = LatestManifest.Parse(lpJson);
        lpTampered.Assets[ReleaseFeed.AssetInstaller]!.Sha256 = new string('c', 64);
        Check("清单篡改 SHA256 验签失败", !ReleaseSigning.Verify(lpTampered, ReleaseSigning.GetPublicKeyBase64(lpRandomSeed)));
        var lpTamperedNotes = LatestManifest.Parse(lpJson);
        lpTamperedNotes.Notes += "tampered";
        Check("清单篡改说明验签失败", !ReleaseSigning.Verify(lpTamperedNotes, ReleaseSigning.GetPublicKeyBase64(lpRandomSeed)));

        Console.WriteLine(lpFailures == 0 ? "自检全部通过" : $"自检失败 {lpFailures} 项");
        return lpFailures == 0 ? 0 : 1;
    }

    private static bool TryCompareThrows()
    {
        try
        {
            SemanticVersion.Compare("1.0", "1.0.1");
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
