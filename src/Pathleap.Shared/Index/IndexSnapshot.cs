using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pathleap.Shared.Index;

/// <summary>
/// 索引文件格式常量。单文件、只读映射、无全量反序列化：
///  [Header 256B][Entries N × 16B][NameArena]。
/// 条目里只存文件名与父节点下标，完整路径在查询结果里按父链重建（只对前 K 条做）。
/// </summary>
public static class IndexFormat
{
    /// <summary>"PLXI"（Pathleap IndeX）。</summary>
    public const uint Magic = 0x504C5849;

    public const int FormatVersion = 4;

    public const int HeaderSize = 256;

    /// <summary>
    /// 名字前缀编码的块大小：每个块的第一条存全名，其余条目只存“与块首名的公共前缀长度 + 后缀”。
    /// 实测 C:\Windows：块大小 5 时名字区从 50.8 B/条降到 26.5 B/条，且块首名本身也在名字区里，
    /// 子串扫描仍然只扫一遍字节流。块越小压缩率越高，但块首名越多、扫描字节越多，5 是实测折中。
    /// </summary>
    public const int PrefixBlockSize = 5;

    /// <summary>前缀长度用 1 字节存，上限 255。</summary>
    public const int MaxPrefix = 255;

    /// <summary>
    /// 单条记录 16 字节（500 万条记录区约 80 MB）。字段打包规则：
    ///  - 体积按 KiB 存 28 位（上限 256 GiB，超出截断），高 4 位放 Flags；
    ///  - 名字长度按 UTF-8 字节存 10 位（Windows 文件名上限 255 个 UTF-16 码元，
    ///    UTF-8 最多约 765 字节，1023 足够），低 6 位放深度（超过 63 层按 63 记录）；
    ///  - 修改时间按天存 16 位（约到 2149 年，列表展示精确到天）。
    /// </summary>
    public const int RecordSize = 16;

    public const int MaxNameBytes = 1023;

    public const int MaxDepth = 63;

    // Header 内固定偏移
    public const int OffsetMagic = 0;
    public const int OffsetFormatVersion = 4;
    public const int OffsetEntryCount = 8;

    /// <summary>u16：当前记录宽度，便于读取侧/测量脚本按实际布局解析。</summary>
    public const int OffsetRecordSize = 12;

    public const int OffsetArenaOffset = 16;
    public const int OffsetArenaSize = 24;
    public const int OffsetBuildUnixMs = 32;
    public const int OffsetSourceLength = 40;
    public const int OffsetSourceText = 44;
    public const int SourceTextMax = HeaderSize - OffsetSourceText;

    /// <summary>卷根节点的 ParentIndex。</summary>
    public const int NoParent = -1;

    public const byte FlagDirectory = 1;
    public const byte FlagHidden = 2;
    public const byte FlagSystem = 4;
    public const byte FlagReparse = 8;
}

/// <summary>落盘条目（16 字节，字段顺序即磁盘布局，不可随意调整）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = IndexFormat.RecordSize)]
public struct IndexRecord
{
    public uint NameOffset;

    public int ParentIndex;

    /// <summary>高 4 位 Flags，低 28 位体积（KiB）。</summary>
    public uint SizeAndFlags;

    /// <summary>低 10 位名字字节数，高 6 位路径深度。</summary>
    public ushort LengthDepth;

    /// <summary>最后修改时间，Unix 天（0 表示未知）。</summary>
    public ushort ModifiedDays;

    public ushort NameLength => (ushort)(LengthDepth & 0x03FF);

    public byte Depth => (byte)((LengthDepth >> 10) & 0x3F);

    public byte Flags => (byte)(SizeAndFlags >> 28);

    /// <summary>文件体积，单位 KiB（0 表示目录或未知）。</summary>
    public uint SizeKb => SizeAndFlags & 0x0FFFFFFF;

    /// <summary>最后修改时间，Unix 秒（0 表示未知）。</summary>
    public uint ModifiedUnixSec => (uint)ModifiedDays * 86400u;

    public bool IsDirectory => (Flags & IndexFormat.FlagDirectory) != 0;

    public long Size => (long)SizeKb * 1024;

    public static uint PackSizeAndFlags(long sizeBytes, byte flags)
    {
        var lpSizeKb = (uint)Math.Clamp(sizeBytes / 1024, 0L, 0x0FFFFFFFL);
        return lpSizeKb | ((uint)(flags & 0x0F) << 28);
    }

    public static ushort PackLengthDepth(int nameBytes, int depth)
    {
        var lpLength = Math.Clamp(nameBytes, 0, IndexFormat.MaxNameBytes);
        var lpDepth = Math.Clamp(depth, 0, IndexFormat.MaxDepth);
        return (ushort)(lpLength | (lpDepth << 10));
    }

    public static ushort PackModifiedDays(long fileTimeTicks)
    {
        if (fileTimeTicks <= 0)
        {
            return 0;
        }

        try
        {
            var lpDays = DateTimeOffset.FromFileTime(fileTimeTicks).ToUnixTimeSeconds() / 86400;
            return (ushort)Math.Clamp(lpDays, 0L, (long)ushort.MaxValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}

/// <summary>构建期可变节点。只在建索引时存在，落盘前转成紧凑记录。</summary>
public sealed class IndexNode
{
    public IndexNode(string name, int parentIndex, int depth, byte flags, long size, long modifiedTicks)
    {
        Name = name;
        ParentIndex = parentIndex;
        Depth = depth;
        Flags = flags;
        Size = size;
        ModifiedTicks = modifiedTicks;
    }

    public string Name { get; }

    public int ParentIndex { get; set; }

    public int Depth { get; set; }

    public byte Flags { get; }

    public long Size { get; }

    public long ModifiedTicks { get; }

    /// <summary>用于排序与匹配的小写形式（ASCII 折叠，非 ASCII 原样保留）。</summary>
    public string FoldedName { get; set; } = string.Empty;
}

/// <summary>一次构建的结果摘要。</summary>
public sealed class IndexBuildResult
{
    public int EntryCount { get; set; }

    public int SkippedCount { get; set; }

    public long FileBytes { get; set; }

    /// <summary>因名字前缀编码而省掉的名字区字节数。</summary>
    public long NamePrefixSavedBytes { get; set; }

    public double BuildMs { get; set; }

    public string Source { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    /// <summary>构建过程中的降级或告警说明（例如无卷句柄权限已降级目录遍历），空串表示无。</summary>
    public string Warning { get; set; } = string.Empty;
}

/// <summary>
/// 构建进度。子进程按节流节奏写进 <c>&lt;output&gt;.progress.json</c>，服务进程轮询后转成 INDEX_STATUS 上报，
/// UI 据此显示「已扫描条目 / 百分比 / 当前阶段」。
/// </summary>
public sealed class IndexBuildProgress
{
    /// <summary>scan（枚举中）/ write（排序并落盘中）。</summary>
    public string Phase { get; set; } = "scan";

    public int Scanned { get; set; }

    /// <summary>预计总量；0 表示未知（USN 与目录遍历都拿不到确切总数）。</summary>
    public int Total { get; set; }

    /// <summary>0–100；负数表示总量未知。</summary>
    public double Percent { get; set; } = -1;

    /// <summary>构建过程中的降级说明（如 USN 无权限已改走目录遍历），空串表示无。</summary>
    public string Warning { get; set; } = string.Empty;
}

/// <summary>
/// 索引快照：写入（构建后一次性顺序落盘）与只读映射读取。
/// 读取侧直接持有映射视图指针，查询时零拷贝访问条目与名字区。
/// </summary>
public sealed unsafe class IndexSnapshot : IDisposable
{
    private MemoryMappedFile? _lpFile;
    private MemoryMappedViewAccessor? _lpView;
    private byte* _lpBase;
    private bool _lpPointerAcquired;

    private IndexSnapshot()
    {
    }

    public int EntryCount { get; private set; }

    public long ArenaOffset { get; private set; }

    public long ArenaSize { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public DateTimeOffset BuiltAt { get; private set; } = DateTimeOffset.MinValue;

    public long FileBytes { get; private set; }

    public string FilePath { get; private set; } = string.Empty;

    /// <summary>把构建结果写成索引文件：先写临时文件、校验后原子替换，避免半成品被读到。</summary>
    public static IndexBuildResult Write(
        IReadOnlyList<IndexNode> nodes,
        string outputPath,
        string sourceLabel,
        int skippedCount,
        double buildMs)
    {

        var lpDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(lpDirectory))
        {
            Directory.CreateDirectory(lpDirectory);
        }

        // 排序键：小写名字 -> 原名字 -> 父下标，保证顺序稳定（同分结果不会跳动）。
        var lpOrder = Enumerable.Range(0, nodes.Count).ToArray();
        var lpKeys = new string[nodes.Count];
        for (var lpI = 0; lpI < nodes.Count; lpI++)
        {
            lpKeys[lpI] = nodes[lpI].FoldedName;
        }

        Array.Sort(lpOrder, (lpLeft, lpRight) =>
        {
            var lpCompare = string.CompareOrdinal(lpKeys[lpLeft], lpKeys[lpRight]);
            if (lpCompare != 0)
            {
                return lpCompare;
            }

            lpCompare = string.CompareOrdinal(nodes[lpLeft].Name, nodes[lpRight].Name);
            return lpCompare != 0 ? lpCompare : lpLeft.CompareTo(lpRight);
        });

        // 旧下标 -> 新下标，用于重映射父节点。
        var lpRemap = new int[nodes.Count];
        for (var lpNew = 0; lpNew < lpOrder.Length; lpNew++)
        {
            lpRemap[lpOrder[lpNew]] = lpNew;
        }

        var lpRecords = new IndexRecord[nodes.Count];
        var lpArena = new List<byte>(Math.Max(1024, nodes.Count * 8));
        var lpPrefixLens = new byte[nodes.Count];

        // 名字前缀编码：每个块的首条存全名，块内其余条目只存与块首名的公共前缀长度和后缀。
        // 真实目录里相邻名字（尤其 WinSxS 的组件名）共享很长前缀，实测 C:\Windows 平均共享 38 字节。
        // 同一个块的首条名本身也是名字区里的普通字节，所以子串扫描仍可整块流式扫描，不需要逐条解码。
        var lpBlockStartBytes = Array.Empty<byte>();
        var lpPrefixSavedBytes = 0L;
        for (var lpNew = 0; lpNew < lpOrder.Length; lpNew++)
        {
            var lpOld = lpOrder[lpNew];
            var lpNode = nodes[lpOld];
            var lpNameBytes = EncodeNameCapped(lpNode.Name);
            var lpParent = lpNode.ParentIndex < 0 ? IndexFormat.NoParent : lpRemap[lpNode.ParentIndex];

            int lpPrefixLen;
            if (lpNew % IndexFormat.PrefixBlockSize == 0)
            {
                lpBlockStartBytes = lpNameBytes;
                lpPrefixLen = 0;
            }
            else
            {
                lpPrefixLen = Math.Min(
                    CommonPrefixLength(lpBlockStartBytes, lpNameBytes),
                    Math.Min(IndexFormat.MaxPrefix, lpNameBytes.Length));
            }

            lpPrefixLens[lpNew] = (byte)lpPrefixLen;
            lpPrefixSavedBytes += lpPrefixLen;
            var lpSuffix = lpNameBytes.AsSpan(lpPrefixLen);

            lpRecords[lpNew] = new IndexRecord
            {
                NameOffset = (uint)lpArena.Count,
                ParentIndex = lpParent,
                SizeAndFlags = IndexRecord.PackSizeAndFlags(lpNode.Size, lpNode.Flags),
                LengthDepth = IndexRecord.PackLengthDepth(lpNameBytes.Length, lpNode.Depth),
                ModifiedDays = IndexRecord.PackModifiedDays(lpNode.ModifiedTicks)
            };
            lpArena.AddRange(lpSuffix);
        }

        // 布局：[Header 256B][Records 16B × N][PrefixLens 1B × N][名字后缀区]
        var lpArenaOffset = IndexFormat.HeaderSize
            + (long)lpRecords.Length * IndexFormat.RecordSize
            + lpRecords.Length;
        var lpFileBytes = lpArenaOffset + lpArena.Count;
        var lpTemp = outputPath + ".tmp";

        var lpStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var lpStream = new FileStream(lpTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
        {
            var lpHeader = new byte[IndexFormat.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetMagic), IndexFormat.Magic);
            BinaryPrimitives.WriteInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetFormatVersion), IndexFormat.FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetEntryCount), lpRecords.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetRecordSize), (ushort)IndexFormat.RecordSize);
            BinaryPrimitives.WriteInt64LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetArenaOffset), lpArenaOffset);
            BinaryPrimitives.WriteInt64LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetArenaSize), lpArena.Count);
            BinaryPrimitives.WriteInt64LittleEndian(
                lpHeader.AsSpan(IndexFormat.OffsetBuildUnixMs),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var lpSourceBytes = System.Text.Encoding.UTF8.GetBytes(sourceLabel);
            var lpSourceLength = Math.Min(lpSourceBytes.Length, IndexFormat.SourceTextMax);
            BinaryPrimitives.WriteInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetSourceLength), lpSourceLength);
            Array.Copy(lpSourceBytes, 0, lpHeader, IndexFormat.OffsetSourceText, lpSourceLength);

            lpStream.Write(lpHeader, 0, lpHeader.Length);

            var lpRecordBytes = new byte[IndexFormat.RecordSize];
            foreach (var lpRecord in lpRecords)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(lpRecordBytes.AsSpan(0), lpRecord.NameOffset);
                BinaryPrimitives.WriteInt32LittleEndian(lpRecordBytes.AsSpan(4), lpRecord.ParentIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(lpRecordBytes.AsSpan(8), lpRecord.SizeAndFlags);
                BinaryPrimitives.WriteUInt16LittleEndian(lpRecordBytes.AsSpan(12), lpRecord.LengthDepth);
                BinaryPrimitives.WriteUInt16LittleEndian(lpRecordBytes.AsSpan(14), lpRecord.ModifiedDays);
                lpStream.Write(lpRecordBytes, 0, lpRecordBytes.Length);
            }

            lpStream.Write(lpPrefixLens, 0, lpPrefixLens.Length);
            lpStream.Write(lpArena.ToArray(), 0, lpArena.Count);
            lpStream.Flush(flushToDisk: true);
        }

        // 构建期间服务进程仍映射着同路径的旧索引，Windows 不允许删除已被映射的文件：
        // 服务收到 write 阶段进度后会主动释放映射，这里再做有界重试，避免两侧时序竞争。
        ReplaceFile(lpTemp, outputPath);

        var lpTotalMs = buildMs + (System.Diagnostics.Stopwatch.GetTimestamp() - lpStartedTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        return new IndexBuildResult
        {
            EntryCount = lpRecords.Length,
            SkippedCount = skippedCount,
            FileBytes = lpFileBytes,
            NamePrefixSavedBytes = lpPrefixSavedBytes,
            BuildMs = Math.Round(lpTotalMs, 1),
            Source = sourceLabel,
            FilePath = outputPath
        };
    }

    /// <summary>只读映射一个索引文件。格式不符直接抛 InvalidDataException，上层据此重建。</summary>
    public static IndexSnapshot Open(string filePath)
    {
        var lpSnapshot = new IndexSnapshot { FilePath = filePath, FileBytes = new FileInfo(filePath).Length };
        var lpFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var lpView = lpFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        try
        {
            byte* lpPointer = null;
            lpView.SafeMemoryMappedViewHandle.AcquirePointer(ref lpPointer);
            lpSnapshot._lpPointerAcquired = true;
            lpSnapshot._lpBase = lpPointer + lpView.PointerOffset;
            lpSnapshot._lpFile = lpFile;
            lpSnapshot._lpView = lpView;

            var lpHeader = (byte*)lpSnapshot._lpBase;
            var lpMagic = Unsafe.ReadUnaligned<uint>(lpHeader + IndexFormat.OffsetMagic);
            if (lpMagic != IndexFormat.Magic)
            {
                throw new InvalidDataException("索引文件 magic 不匹配");
            }

            var lpVersion = Unsafe.ReadUnaligned<int>(lpHeader + IndexFormat.OffsetFormatVersion);
            if (lpVersion != IndexFormat.FormatVersion)
            {
                throw new InvalidDataException($"索引文件版本不兼容: {lpVersion}");
            }

            lpSnapshot.EntryCount = Unsafe.ReadUnaligned<int>(lpHeader + IndexFormat.OffsetEntryCount);
            lpSnapshot.ArenaOffset = Unsafe.ReadUnaligned<long>(lpHeader + IndexFormat.OffsetArenaOffset);
            lpSnapshot.ArenaSize = Unsafe.ReadUnaligned<long>(lpHeader + IndexFormat.OffsetArenaSize);
            var lpBuildMs = Unsafe.ReadUnaligned<long>(lpHeader + IndexFormat.OffsetBuildUnixMs);
            lpSnapshot.BuiltAt = DateTimeOffset.FromUnixTimeMilliseconds(lpBuildMs);

            var lpSourceLength = Unsafe.ReadUnaligned<int>(lpHeader + IndexFormat.OffsetSourceLength);
            var lpSourceBytes = new byte[Math.Clamp(lpSourceLength, 0, IndexFormat.SourceTextMax)];
            Marshal.Copy((nint)(lpHeader + IndexFormat.OffsetSourceText), lpSourceBytes, 0, lpSourceBytes.Length);
            lpSnapshot.Source = System.Text.Encoding.UTF8.GetString(lpSourceBytes);
            return lpSnapshot;
        }
        catch
        {
            lpFile.Dispose();
            lpView.Dispose();
            throw;
        }
    }

    public IndexRecord GetRecord(int index)
    {
        var lpBase = _lpBase;
        return Unsafe.ReadUnaligned<IndexRecord>(lpBase + IndexFormat.HeaderSize + index * IndexFormat.RecordSize);
    }

    /// <summary>前缀长度区紧跟在记录区之后：Header 256B + Records 16B × N。</summary>
    private long PrefixLensOffset => IndexFormat.HeaderSize + (long)EntryCount * IndexFormat.RecordSize;

    public byte GetPrefixLength(int index)
    {
        return Unsafe.ReadUnaligned<byte>(_lpBase + PrefixLensOffset + index);
    }

    public int BlockStartIndex(int index)
    {
        return index - index % IndexFormat.PrefixBlockSize;
    }

    /// <summary>条目自己的名字后缀（不含与块首名共享的前缀）。</summary>
    public ReadOnlySpan<byte> GetSuffixSpan(int index, IndexRecord record)
    {
        var lpPrefixLen = (int)GetPrefixLength(index);
        return new ReadOnlySpan<byte>(
            _lpBase + ArenaOffset + record.NameOffset,
            record.NameLength - lpPrefixLen);
    }

    /// <summary>块首条目的完整名字字节（块首条前缀长度为 0，所以其名字区字节就是全名）。</summary>
    public ReadOnlySpan<byte> GetBlockStartNameSpan(int index)
    {
        var lpStart = BlockStartIndex(index);
        var lpRecord = GetRecord(lpStart);
        return new ReadOnlySpan<byte>(_lpBase + ArenaOffset + lpRecord.NameOffset, lpRecord.NameLength);
    }

    /// <summary>解码第 index 条的名字：块首名前缀 + 自己的后缀。</summary>
    public string DecodeNameAt(int index)
    {
        var lpRecord = GetRecord(index);
        var lpPrefixLen = (int)GetPrefixLength(index);
        if (lpPrefixLen == 0)
        {
            return DecodeName(GetSuffixSpan(index, lpRecord));
        }

        var lpBytes = new byte[lpRecord.NameLength];
        GetBlockStartNameSpan(index)[..lpPrefixLen].CopyTo(lpBytes);
        GetSuffixSpan(index, lpRecord).CopyTo(lpBytes.AsSpan(lpPrefixLen));
        return DecodeName(lpBytes);
    }

    public ReadOnlySpan<byte> GetArenaSpan()
    {
        return new ReadOnlySpan<byte>(_lpBase + ArenaOffset, (int)ArenaSize);
    }

    /// <summary>按父链重建完整路径（只对要展示的前若干条调用，成本可忽略）。</summary>
    public string BuildPath(int index)
    {
        var lpParts = new List<string>(8);
        var lpCurrent = index;
        var lpGuard = 0;
        while (lpCurrent >= 0 && lpGuard++ < 256)
        {
            var lpRecord = GetRecord(lpCurrent);
            var lpName = DecodeNameAt(lpCurrent);
            if (lpName.Length > 0)
            {
                lpParts.Add(lpName);
            }

            lpCurrent = lpRecord.ParentIndex;
        }

        // 反转后按 '\' 拼接：卷根节点名形如 "C:"，其余都是单段名字。
        lpParts.Reverse();
        if (lpParts.Count == 0)
        {
            return string.Empty;
        }

        var lpBuilder = new System.Text.StringBuilder(lpParts[0]);
        for (var lpI = 1; lpI < lpParts.Count; lpI++)
        {
            if (lpBuilder.Length > 0 && lpBuilder[^1] != '\\')
            {
                lpBuilder.Append('\\');
            }

            lpBuilder.Append(lpParts[lpI]);
        }

        return lpBuilder.ToString();
    }

    public void Dispose()
    {
        if (_lpPointerAcquired && _lpView is not null)
        {
            _lpView.SafeMemoryMappedViewHandle.ReleasePointer();
            _lpPointerAcquired = false;
        }

        _lpBase = null;
        _lpView?.Dispose();
        _lpFile?.Dispose();
        _lpView = null;
        _lpFile = null;
    }

    public static string DecodeName(ReadOnlySpan<byte> utf8)
    {
        return System.Text.Encoding.UTF8.GetString(utf8);
    }

    private static byte[] EncodeName(string name)
    {
        return System.Text.Encoding.UTF8.GetBytes(name);
    }

    /// <summary>
    /// 编码文件名并保证不超过记录里 10 位的长度上限。Windows 文件名上限 255 个 UTF-16 码元，
    /// UTF-8 字节数最多约 765，正常不会触顶；真触顶时按字符截断，避免产生半个 UTF-8 码元。
    /// </summary>
    private static byte[] EncodeNameCapped(string name)
    {
        var lpBytes = EncodeName(name);
        if (lpBytes.Length <= IndexFormat.MaxNameBytes)
        {
            return lpBytes;
        }

        var lpLength = name.Length;
        while (lpLength > 0 && EncodeName(name[..lpLength]).Length > IndexFormat.MaxNameBytes)
        {
            lpLength--;
        }

        return EncodeName(name[..lpLength]);
    }

    private static int CommonPrefixLength(byte[] left, byte[] right)
    {
        var lpMax = Math.Min(left.Length, right.Length);
        var lpLength = 0;
        while (lpLength < lpMax && left[lpLength] == right[lpLength])
        {
            lpLength++;
        }

        return lpLength;
    }

    /// <summary>把临时文件替换成正式索引；目标被别的进程映射时最多重试 15 秒。</summary>
    private static void ReplaceFile(string tempPath, string outputPath)
    {
        var lpDeadline = System.Diagnostics.Stopwatch.GetTimestamp()
                         + System.Diagnostics.Stopwatch.Frequency * 15L;
        while (true)
        {
            try
            {
                File.Move(tempPath, outputPath, overwrite: true);
                return;
            }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetTimestamp() < lpDeadline)
            {
                Thread.Sleep(250);
            }
        }
    }
}
