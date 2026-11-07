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

    /// <summary>M1 兼容字段：not-implemented / ready / building / error / not-built。</summary>
    public string State { get; set; } = "not-implemented";

    public string Note { get; set; } = string.Empty;

    /// <summary>构建期当前阶段：scan（枚举中）/ write（排序落盘中），非构建期为空。</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>已扫描条目数（构建期有效）。</summary>
    public int Scanned { get; set; }

    /// <summary>预计总量；0 表示未知。</summary>
    public int Total { get; set; }

    /// <summary>0–100；负数表示总量未知，UI 只显示已扫描条目。</summary>
    public double Percent { get; set; } = -1;

    /// <summary>降级或失败说明（如 USN 权限不足已降级目录遍历），空串表示无。</summary>
    public string Warning { get; set; } = string.Empty;
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
