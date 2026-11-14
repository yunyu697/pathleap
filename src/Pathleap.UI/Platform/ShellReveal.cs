using System.Diagnostics;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.UI.Platform;

/// <summary>在资源管理器中定位条目：文件与目录都交给 /select，由系统打开父级并选中。</summary>
internal static class ShellReveal
{
    internal static bool TryReveal(string lpPath)
    {
        try
        {
            var lpStartInfo = new ProcessStartInfo("explorer.exe", $"/select,\"{lpPath}\"") { UseShellExecute = true };
            Process.Start(lpStartInfo);
            AppLog.Info($"已在资源管理器中显示: {lpPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"在资源管理器中显示失败 {lpPath}: {ex.Message}");
            return false;
        }
    }
}
