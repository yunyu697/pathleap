using System.Diagnostics;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>
/// 合成索引源：仅用于压测查询侧性能（不产生真实文件 I/O）。
/// 生成“卷根 -> 200 个一级目录 -> 1000 个二级目录 -> N 个文件”的层级，
/// 名字由固定词表 + 序号 + 扩展名组成，用于验证 500 万条目下的搜索 P95。
/// </summary>
internal static class SyntheticIndexSource
{
    private static readonly string[] Words =
    [
        "report", "photo", "invoice", "project", "backup", "design", "notes", "video",
        "archive", "config", "script", "budget", "slide", "dataset", "readme", "draft",
        "meeting", "spec", "release", "build"
    ];

    private static readonly string[] Extensions =
    [
        ".txt", ".docx", ".xlsx", ".png", ".jpg", ".mp4", ".zip", ".json", ".cs", ".dll", ".pdf", ".md"
    ];

    public static IndexBuildResult Build(
        int entryCount,
        string outputPath,
        Action<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpBuilder = new IndexBuilder();
        lpBuilder.Add(new IndexNode("C:", IndexFormat.NoParent, 0, IndexFormat.FlagDirectory, 0, 0));

        var lpTopDirs = 200;
        var lpSubDirs = 1000;
        var lpTopIndexes = new int[lpTopDirs];
        for (var lpI = 0; lpI < lpTopDirs; lpI++)
        {
            lpTopIndexes[lpI] = lpBuilder.Add(
                new IndexNode($"dir{lpI:000}", 0, 1, IndexFormat.FlagDirectory, 0, 0));
        }

        var lpSubIndexes = new int[lpSubDirs];
        for (var lpI = 0; lpI < lpSubDirs; lpI++)
        {
            var lpParent = lpTopIndexes[lpI % lpTopDirs];
            lpSubIndexes[lpI] = lpBuilder.Add(
                new IndexNode($"sub{lpI:0000}", lpParent, 2, IndexFormat.FlagDirectory, 0, 0));
        }

        var lpFileCount = Math.Max(0, entryCount - 1 - lpTopDirs - lpSubDirs);
        for (var lpI = 0; lpI < lpFileCount; lpI++)
        {
            if ((lpI & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(new IndexBuildProgress
                {
                    Phase = "scan",
                    Scanned = lpBuilder.Count,
                    Total = entryCount,
                    Percent = entryCount <= 0 ? -1 : Math.Round(lpBuilder.Count * 100.0 / entryCount, 1)
                });
            }

            var lpWord = Words[lpI % Words.Length];
            var lpExtension = Extensions[(lpI / Words.Length) % Extensions.Length];
            var lpName = $"{lpWord}_{lpI:0000000}{lpExtension}";
            var lpParent = lpSubIndexes[lpI % lpSubDirs];
            var lpSize = ((lpI * 7919L) % 4_000_000) + 256;
            lpBuilder.Add(new IndexNode(lpName, lpParent, 3, 0, lpSize, 0));
        }

        var lpNodeMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
        AppLog.Info($"合成索引节点生成完成：{lpBuilder.Count} 条，{lpNodeMs:0}ms");

        progress?.Invoke(new IndexBuildProgress
        {
            Phase = "write",
            Scanned = lpBuilder.Count,
            Total = lpBuilder.Count,
            Percent = 100
        });

        var lpResult = lpBuilder.Save(outputPath, $"synthetic:{lpBuilder.Count}", 0, lpNodeMs);
        lpResult.BuildMs = Math.Round((Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency, 1);
        return lpResult;
    }
}
