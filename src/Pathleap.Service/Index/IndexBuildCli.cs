using System.Text.Json;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>
/// <c>--build-once &lt;mode&gt; &lt;root&gt; &lt;output&gt;</c> 入口：只跑一次构建，写完结果摘要即退出。
/// 由服务进程用短生命周期子进程调用，构建期的上百 MB 临时堆随进程退出一起归还系统。
/// </summary>
internal static class IndexBuildCli
{
    public static int Run(string mode, string root, string output)
    {
        try
        {
            var lpDirectory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(lpDirectory))
            {
                Directory.CreateDirectory(lpDirectory);
            }

            var lpResult = IndexBuildRunner.Run(root, mode, output, CancellationToken.None);
            File.WriteAllText(output + ".result.json", JsonSerializer.Serialize(lpResult));
            AppLog.Info(
                $"构建子进程完成：{lpResult.EntryCount} 条，{lpResult.FileBytes / 1024.0 / 1024.0:0.0} MB，" +
                $"耗时 {lpResult.BuildMs:0}ms");
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("构建子进程失败", ex);
            return 2;
        }
    }
}
