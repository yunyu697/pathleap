using System.Diagnostics;
using System.Text.Json;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>
/// <c>--build-once &lt;mode&gt; &lt;root&gt; &lt;output&gt;</c> 入口：只跑一次构建，写完结果摘要即退出。
/// 由服务进程用短生命周期子进程调用，构建期的上百 MB 临时堆随进程退出一起归还系统。
/// 构建进度按节流节奏写到 <c>&lt;output&gt;.progress.json</c>，失败原因写到 <c>&lt;output&gt;.fail.txt</c>，
/// 供服务进程轮询并转成 INDEX_STATUS / 失败提示。
/// </summary>
internal static class IndexBuildCli
{
    private const long ProgressIntervalMs = 300;

    public static int Run(string mode, string root, string output)
    {
        var lpProgressFile = output + ".progress.json";
        var lpFailFile = output + ".fail.txt";
        TryDelete(lpProgressFile);
        TryDelete(lpFailFile);

        try
        {
            var lpDirectory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(lpDirectory))
            {
                Directory.CreateDirectory(lpDirectory);
            }

            var lpLastWriteTick = 0L;
            var lpStarted = Stopwatch.GetTimestamp();
            void Report(IndexBuildProgress progress)
            {
                var lpNow = Stopwatch.GetTimestamp();
                var lpElapsedMs = (lpNow - lpLastWriteTick) * 1000.0 / Stopwatch.Frequency;
                // 落盘阶段只报一次，必须写出；扫描阶段按 300ms 节流，避免频繁小文件写入拖慢构建。
                if (progress.Phase != "write" && lpLastWriteTick != 0 && lpElapsedMs < ProgressIntervalMs)
                {
                    return;
                }

                lpLastWriteTick = lpNow;
                try
                {
                    File.WriteAllText(lpProgressFile, JsonSerializer.Serialize(progress));
                }
                catch (IOException)
                {
                    // 进度文件写失败不影响构建本身，服务端最多看不到这一次进度。
                }
            }

            var lpResult = IndexBuildRunner.Run(root, mode, output, Report, CancellationToken.None);
            File.WriteAllText(output + ".result.json", JsonSerializer.Serialize(lpResult));
            AppLog.Info(
                $"构建子进程完成：{lpResult.EntryCount} 条，{lpResult.FileBytes / 1024.0 / 1024.0:0.0} MB，" +
                $"耗时 {lpResult.BuildMs:0}ms，" +
                $"进程总耗时 {(Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency:0}ms");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("构建子进程失败", ex);
            try
            {
                File.WriteAllText(lpFailFile, ex.Message);
            }
            catch (IOException)
            {
                // 失败原因写不出去时，服务端只能拿到退出码。
            }

            return 2;
        }
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
        catch (IOException)
        {
            // 残留文件清理失败不影响本次构建。
        }
    }
}
