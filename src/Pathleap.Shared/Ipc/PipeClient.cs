using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Pathleap.Shared.Ipc;

public sealed class IpcRequest
{
    public string Command { get; set; } = string.Empty;

    /// <summary>仅 BENCH 使用。</summary>
    public int Samples { get; set; }
}

public sealed class IpcReply
{
    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? SummaryPath { get; set; }

    public double P50Ms { get; set; } = -1;

    public double P95Ms { get; set; } = -1;

    public ServiceStatusDto? Status { get; set; }

    public static IpcReply Success(string message = "") => new() { Ok = true, Message = message };

    public static IpcReply Failure(string message) => new() { Ok = false, Message = message };
}

public sealed class ServiceStatusDto
{
    public string Product { get; set; } = Branding.ProductId;

    public string Mode { get; set; } = "console";

    public int Pid { get; set; }

    public long UptimeMs { get; set; }

    /// <summary>M1 固定为 not-implemented，M2 接入真实索引后替换。</summary>
    public string Index { get; set; } = "not-implemented";

    public string Version { get; set; } = Branding.Version;

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// 单行 JSON 的命名管道客户端。所有超时都用 CancellationToken，避免 Connect / Read 永久挂起。
/// </summary>
public static class PipeClient
{
    public static async Task<IpcReply> SendAsync(
        string pipeName,
        IpcRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var lpLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lpLinked.CancelAfter(timeout);

        using var lpPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await lpPipe.ConnectAsync(lpLinked.Token).ConfigureAwait(false);

        using var lpWriter = new StreamWriter(lpPipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var lpReader = new StreamReader(lpPipe, new UTF8Encoding(false), leaveOpen: true);

        var lpJson = JsonSerializer.Serialize(request, JsonDefaults.Compact);
        await lpWriter.WriteLineAsync(lpJson).ConfigureAwait(false);

        var lpLine = await lpReader.ReadLineAsync(lpLinked.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(lpLine))
        {
            return IpcReply.Failure("管道返回空响应");
        }

        return JsonSerializer.Deserialize<IpcReply>(lpLine, JsonDefaults.Options)
               ?? IpcReply.Failure("响应无法反序列化");
    }

    /// <summary>探测管道是否存在且可用，不抛异常。</summary>
    public static async Task<bool> ProbeAsync(string pipeName, TimeSpan timeout)
    {
        try
        {
            var lpReply = await SendAsync(pipeName, new IpcRequest { Command = "PING" }, timeout).ConfigureAwait(false);
            return lpReply.Ok;
        }
        catch
        {
            return false;
        }
    }
}
