using System.Diagnostics;
using Pathleap.Shared;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Ipc;

namespace Pathleap.UI.Platform;

/// <summary>
/// M1 的服务进程拉起器：优先使用已运行的 SCM / 控制台服务，否则从 UI 目录启动
/// Pathleap.Service.exe --console（无窗口）。只负责“两个进程分离”的骨架，不做索引。
/// </summary>
internal sealed class ServiceLauncher : IDisposable
{
    private Process? _lpOwnedProcess;
    private bool _lpDisposed;

    public bool Connected { get; private set; }

    public string StatusText { get; private set; } = "服务未连接";

    public async Task EnsureRunningAsync(bool startIfMissing, TimeSpan startupTimeout)
    {
        if (await TryConnectAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false))
        {
            return;
        }

        StatusText = "服务未连接";
        if (!startIfMissing)
        {
            return;
        }

        var lpExePath = Path.Combine(AppContext.BaseDirectory, "Pathleap.Service.exe");
        if (!File.Exists(lpExePath))
        {
            AppLog.Warn($"服务可执行文件不存在: {lpExePath}（UI 仍可独立运行）");
            StatusText = "服务未安装";
            return;
        }

        try
        {
            AppLog.Info($"拉起服务控制台进程: {lpExePath}");
            _lpOwnedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = lpExePath,
                Arguments = "--console",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            });

            var lpDeadline = DateTime.UtcNow + startupTimeout;
            while (DateTime.UtcNow < lpDeadline)
            {
                if (await TryConnectAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false))
                {
                    return;
                }

                await Task.Delay(200).ConfigureAwait(false);
            }

            AppLog.Warn("服务进程已启动，但在超时内未响应管道。");
            StatusText = "服务启动中";
        }
        catch (Exception ex)
        {
            AppLog.Error("服务进程启动失败", ex);
            StatusText = "服务启动失败";
        }
    }

    private async Task<bool> TryConnectAsync(TimeSpan timeout)
    {
        try
        {
            var lpReply = await PipeClient.SendAsync(
                Branding.ServicePipeName,
                new IpcRequest { Command = "STATUS" },
                timeout).ConfigureAwait(false);
            if (!lpReply.Ok || lpReply.Status is null)
            {
                return false;
            }

            Connected = true;
            StatusText = $"服务已连接 · PID {lpReply.Status.Pid} · 索引未启用";
            AppLog.Info($"服务连接成功: mode={lpReply.Status.Mode}, pid={lpReply.Status.Pid}, uptime={lpReply.Status.UptimeMs}ms");
            return true;
        }
        catch
        {
            Connected = false;
            return false;
        }
    }

    public void Dispose()
    {
        if (_lpDisposed)
        {
            return;
        }

        _lpDisposed = true;
        if (_lpOwnedProcess is null)
        {
            return;
        }

        try
        {
            if (!_lpOwnedProcess.HasExited)
            {
                AppLog.Info($"停止 UI 拉起的服务进程 PID={_lpOwnedProcess.Id}");
                _lpOwnedProcess.Kill(entireProcessTree: true);
                _lpOwnedProcess.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"停止服务进程失败: {ex.Message}");
        }
        finally
        {
            _lpOwnedProcess.Dispose();
            _lpOwnedProcess = null;
        }
    }
}
