using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pathleap.Shared.Index;

/// <summary>
/// 索引文件格式常量。单文件、只读映射、无全量反序列化：
///  [Header 256B][Entries N × 32B][NameArena]。
/// 条目里只存文件名与父节点下标，完整路径在查询结果里按父链重建（只对前 K 条做）。
/// </summary>
public static class IndexFormat
{
    /// <summary>"PLXI"（Pathleap IndeX）。</summary>
    public const uint Magic = 0x504C5849;

    public const int FormatVersion = 2;

    public const int HeaderSize = 256;

    /// <summary>
    /// 单条记录 20 字节：500 万条记录区约 100 MB（32 字节方案是 160 MB）。
    /// 为了压到索引体积目标，把体积按 KiB、修改时间按秒存：两者只用于结果列表展示，
    /// 不需要字节级/毫秒级精度。深度用 1 字节（路径深度不可能超过 255）。
    /// </summary>
    public const int RecordSize = 20;

    // Header 内固定偏移
    public const int OffsetMagic = 0;
    public const int OffsetFormatVersion = 4;
    public const int OffsetEntryCount = 8;
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

/// <summary>落盘条目（20 字节，字段顺序即磁盘布局，不可随意调整）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = IndexFormat.RecordSize)]
public struct IndexRecord
{
    public uint NameOffset;

    public ushort NameLength;

    public byte Depth;

    public byte Flags;

    public int ParentIndex;

    /// <summary>文件体积，单位 KiB（0 表示目录或未知）。</summary>
    public uint SizeKb;

    /// <summary>最后修改时间，Unix 秒（0 表示未知）。</summary>
    public uint ModifiedUnixSec;

    public bool IsDirectory => (Flags & IndexFormat.FlagDirectory) != 0;

    public long Size => (long)SizeKb * 1024;
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

    public double BuildMs { get; set; }

    public string Source { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
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

        for (var lpNew = 0; lpNew < lpOrder.Length; lpNew++)
        {
            var lpOld = lpOrder[lpNew];
            var lpNode = nodes[lpOld];
            var lpNameBytes = EncodeName(lpNode.Name);
            var lpParent = lpNode.ParentIndex < 0 ? IndexFormat.NoParent : lpRemap[lpNode.ParentIndex];

            lpRecords[lpNew] = new IndexRecord
            {
                NameOffset = (uint)lpArena.Count,
                NameLength = (ushort)Math.Min(lpNameBytes.Length, ushort.MaxValue),
                Depth = (byte)Math.Min(lpNode.Depth, byte.MaxValue),
                ParentIndex = lpParent,
                Flags = lpNode.Flags,
                SizeKb = (uint)Math.Clamp(lpNode.Size / 1024, 0L, (long)uint.MaxValue),
                ModifiedUnixSec = ToUnixSeconds(lpNode.ModifiedTicks)
            };
            lpArena.AddRange(lpNameBytes);
        }

        var lpArenaOffset = IndexFormat.HeaderSize + (long)lpRecords.Length * IndexFormat.RecordSize;
        var lpFileBytes = lpArenaOffset + lpArena.Count;
        var lpTemp = outputPath + ".tmp";

        var lpStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        using (var lpStream = new FileStream(lpTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan))
        {
            var lpHeader = new byte[IndexFormat.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetMagic), IndexFormat.Magic);
            BinaryPrimitives.WriteInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetFormatVersion), IndexFormat.FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(lpHeader.AsSpan(IndexFormat.OffsetEntryCount), lpRecords.Length);
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
                BinaryPrimitives.WriteUInt16LittleEndian(lpRecordBytes.AsSpan(4), lpRecord.NameLength);
                lpRecordBytes[6] = lpRecord.Depth;
                lpRecordBytes[7] = lpRecord.Flags;
                BinaryPrimitives.WriteInt32LittleEndian(lpRecordBytes.AsSpan(8), lpRecord.ParentIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(lpRecordBytes.AsSpan(12), lpRecord.SizeKb);
                BinaryPrimitives.WriteUInt32LittleEndian(lpRecordBytes.AsSpan(16), lpRecord.ModifiedUnixSec);
                lpStream.Write(lpRecordBytes, 0, lpRecordBytes.Length);
            }

            lpStream.Write(lpArena.ToArray(), 0, lpArena.Count);
            lpStream.Flush(flushToDisk: true);
        }

        File.Move(lpTemp, outputPath, overwrite: true);

        var lpTotalMs = buildMs + (System.Diagnostics.Stopwatch.GetTimestamp() - lpStartedTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        return new IndexBuildResult
        {
            EntryCount = lpRecords.Length,
            SkippedCount = skippedCount,
            FileBytes = lpFileBytes,
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

    public ReadOnlySpan<byte> GetNameSpan(IndexRecord record)
    {
        var lpBase = _lpBase;
        return new ReadOnlySpan<byte>(lpBase + ArenaOffset + record.NameOffset, record.NameLength);
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
            var lpName = DecodeName(GetNameSpan(lpRecord));
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

    /// <summary>FileTime → Unix 秒。0 或非法值返回 0（表示未知）。</summary>
    private static uint ToUnixSeconds(long fileTimeTicks)
    {
        if (fileTimeTicks <= 0)
        {
            return 0;
        }

        try
        {
            var lpSeconds = DateTimeOffset.FromFileTime(fileTimeTicks).ToUnixTimeSeconds();
            return (uint)Math.Clamp(lpSeconds, 0L, (long)uint.MaxValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
