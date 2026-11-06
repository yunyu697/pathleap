using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Pathleap.Shared.Diagnostics;

namespace Pathleap.Shared.Ipc;

/// <summary>
/// 命名管道服务端骨架。每连接一个任务、单行 JSON 请求 / 单行 JSON 响应。
/// 只用于 M1 控制面（STATUS / SHOW / BENCH / QUIT），不承载索引查询。
/// </summary>
public static class PipeServer
{
    public static async Task RunAsync(
        string pipeName,
        Func<IpcRequest, Task<IpcReply>> handler,
        CancellationToken cancellationToken)
    {
        AppLog.Info($"管道服务启动: {pipeName}");

        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? lpServer = null;
            try
            {
                lpServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await lpServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var lpAccepted = lpServer;
                lpServer = null;
                _ = Task.Run(() => HandleOneAsync(lpAccepted, handler), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                lpServer?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                lpServer?.Dispose();
                AppLog.Warn($"管道接受连接失败: {ex.Message}");
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        AppLog.Info($"管道服务退出: {pipeName}");
    }

    private static async Task HandleOneAsync(NamedPipeServerStream server, Func<IpcRequest, Task<IpcReply>> handler)
    {
        try
        {
            using (server)
            using (var lpReader = new StreamReader(server, new UTF8Encoding(false), leaveOpen: true))
            using (var lpWriter = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
            {
                var lpLine = await lpReader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(lpLine))
                {
                    return;
                }

                IpcReply lpReply;
                try
                {
                    var lpRequest = JsonSerializer.Deserialize<IpcRequest>(lpLine, JsonDefaults.Options)
                                    ?? new IpcRequest { Command = "UNKNOWN" };
                    lpReply = await handler(lpRequest).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error("管道请求处理失败", ex);
                    lpReply = IpcReply.Failure(ex.Message);
                }

                await lpWriter.WriteLineAsync(JsonSerializer.Serialize(lpReply, JsonDefaults.Compact)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"管道连接异常: {ex.Message}");
        }
    }
}
