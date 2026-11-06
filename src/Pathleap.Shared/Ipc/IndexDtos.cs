namespace Pathleap.Shared.Ipc;

/// <summary>索引状态（服务端上报）。</summary>
public sealed class IndexStatusDto
{
    public bool Loaded { get; set; }

    public string Source { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    public long FileBytes { get; set; }

    public double BuildMs { get; set; }

    public double LoadMs { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset? BuiltAt { get; set; }

    /// <summary>M1 兼容字段：not-implemented / ready / building。</summary>
    public string State { get; set; } = "not-implemented";

    public string Note { get; set; } = string.Empty;
}

/// <summary>一条搜索结果的传输形态。</summary>
public sealed class SearchHitDto
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public long Size { get; set; }

    public string? ModifiedAt { get; set; }

    public int Tier { get; set; }

    public double Score { get; set; }
}
