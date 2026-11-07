using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Pathleap.Shared.Diagnostics;
using Pathleap.Shared.Index;

namespace Pathleap.Service.Index;

/// <summary>
/// USN/MFT 主路径：FSCTL_ENUM_USN_DATA 全量枚举 + FSCTL_READ_USN_JOURNAL 增量。
/// 需要卷句柄的读权限，即进程以管理员或 Windows 服务（LocalSystem）身份运行；
/// 普通用户下 CreateFile 返回 ERROR_ACCESS_DENIED(5)，上层应降级到目录遍历。
/// </summary>
internal sealed class UsnVolumeSource : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;

    private const int ErrorAccessDenied = 5;
    private const int EnumBufferSize = 64 * 1024;
    private const int MaxEnumBatches = 200_000_000;

    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;

    private IntPtr _lpHandle;
    private readonly string _lpVolume;
    private readonly string _lpVolumeLabel;

    private UsnVolumeSource(IntPtr handle, string volume, string volumeLabel)
    {
        _lpHandle = handle;
        _lpVolume = volume;
        _lpVolumeLabel = volumeLabel;
    }

    public string Volume => _lpVolume;

    public static UsnVolumeSource Open(string volume, string? volumeLabel = null)
    {
        var lpPath = volume.StartsWith(@"\\.\", StringComparison.Ordinal) ? volume : @"\\.\" + volume.TrimEnd('\\', ':') + ":";
        var lpHint = volumeLabel ?? volume.TrimEnd('\\', ':');
        var lpHandle = CreateFileW(lpPath, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (lpHandle == IntPtr.Zero || lpHandle == new IntPtr(-1))
        {
            var lpError = Marshal.GetLastWin32Error();
            var lpMessage = lpError == ErrorAccessDenied
                ? "打开卷句柄被拒绝（ERROR_ACCESS_DENIED），USN 主路径需要管理员或服务身份"
                : $"打开卷句柄失败（Win32={lpError}）";
            throw new UsnAccessException(lpPath, lpError, lpMessage);
        }

        return new UsnVolumeSource(lpHandle, lpPath, lpHint);
    }

    /// <summary>全量枚举：边读边加入构建器，最后统一解析父链并落盘。</summary>
    public IndexBuildResult BuildFull(
        string outputPath,
        Action<IndexBuildProgress>? progress,
        CancellationToken cancellationToken,
        int progressStep = 100_000)
    {
        var lpStarted = Stopwatch.GetTimestamp();
        var lpBuilder = new IndexBuilder();

        // 卷根目录（NTFS 的 FRN 固定为 5）单独建成第 0 个节点，父 FRN 找不到的记录都挂到它下面。
        const ulong lpRootFrn = 5;
        var lpRootIndex = lpBuilder.Add(new IndexNode(_lpVolumeLabel, IndexFormat.NoParent, 0, IndexFormat.FlagDirectory, 0, 0));

        var lpInput = new byte[24];
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(0), 0);
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(16), long.MaxValue);

        var lpOutput = new byte[EnumBufferSize];
        var lpBatches = 0;
        var lpRecords = 0;
        var lpSkipped = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DeviceIoControl(_lpHandle, FsctlEnumUsnData, lpInput, (uint)lpInput.Length, lpOutput, (uint)lpOutput.Length, out var lpReturned, IntPtr.Zero))
            {
                var lpError = Marshal.GetLastWin32Error();
                // ERROR_HANDLE_EOF / ERROR_MORE_DATA 都是正常终止条件。
                if (lpError == 38 || lpBatches > 0)
                {
                    break;
                }

                throw new InvalidOperationException($"FSCTL_ENUM_USN_DATA 失败（Win32={lpError}）");
            }

            if (lpReturned < 8)
            {
                break;
            }

            lpBatches++;
            var lpNextStart = BinaryPrimitives.ReadInt64LittleEndian(lpOutput.AsSpan(0));
            BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(0), lpNextStart);

            var lpOffset = 8;
            while (lpOffset < (int)lpReturned)
            {
                var lpRecordLength = BinaryPrimitives.ReadInt32LittleEndian(lpOutput.AsSpan(lpOffset));
                if (lpRecordLength <= 0 || lpOffset + lpRecordLength > (int)lpReturned)
                {
                    break;
                }

                var lpSlice = lpOutput.AsSpan(lpOffset, lpRecordLength);
                if (TryParseRecord(lpSlice, out var lpFrn, out var lpParentFrn, out var lpName, out var lpAttributes, out var lpIsDirectory, out _))
                {
                    if (!string.IsNullOrEmpty(lpName) && lpBuilder.FindByFrn(lpFrn) is null)
                    {
                        var lpFlags = lpIsDirectory ? IndexFormat.FlagDirectory : (byte)0;
                        if ((lpAttributes & (uint)FileAttributes.Hidden) != 0)
                        {
                            lpFlags |= IndexFormat.FlagHidden;
                        }

                        if ((lpAttributes & (uint)FileAttributes.System) != 0)
                        {
                            lpFlags |= IndexFormat.FlagSystem;
                        }

                        lpBuilder.AddWithFrn(lpFrn, new IndexNode(lpName, IndexFormat.NoParent, 0, lpFlags, 0, 0));
                        lpBuilder.SetParentFrn(lpFrn, lpParentFrn);
                        lpRecords++;
                    }
                    else
                    {
                        lpSkipped++;
                    }
                }
                else
                {
                    lpSkipped++;
                }

                lpOffset += lpRecordLength;
            }

            if (lpBuilder.Count % progressStep == 0)
            {
                progress?.Invoke(new IndexBuildProgress { Phase = "scan", Scanned = lpBuilder.Count });
            }

            if (lpBatches > MaxEnumBatches)
            {
                AppLog.Warn("USN 枚举批次异常多，提前终止。");
                break;
            }
        }

        // 父链解析：父 FRN 找不到（已删除/权限过滤）的节点挂到卷根。
        lpBuilder.ResolveParents(lpRootFrn, lpRootIndex);
        var lpBuildMs = (Stopwatch.GetTimestamp() - lpStarted) * 1000.0 / Stopwatch.Frequency;
        AppLog.Info($"USN 全量枚举完成：{lpRecords} 条，跳过 {lpSkipped}，{lpBatches} 批，耗时 {lpBuildMs:0}ms");
        progress?.Invoke(new IndexBuildProgress { Phase = "write", Scanned = lpBuilder.Count });

        return lpBuilder.Save(outputPath, $"usn:{_lpVolume}", lpSkipped, lpBuildMs);
    }

    /// <summary>查询日志状态；后续增量从返回的 NextUsn 开始读。</summary>
    public UsnJournalInfo QueryJournal()
    {
        var lpOutput = new byte[80];
        if (!DeviceIoControl(_lpHandle, FsctlQueryUsnJournal, IntPtr.Zero, 0, lpOutput, (uint)lpOutput.Length, out var lpReturned, IntPtr.Zero))
        {
            var lpError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"FSCTL_QUERY_USN_JOURNAL 失败（Win32={lpError}）");
        }

        if (lpReturned < 80)
        {
            throw new InvalidOperationException("USN 日志信息长度不足");
        }

        return new UsnJournalInfo
        {
            JournalId = BinaryPrimitives.ReadUInt64LittleEndian(lpOutput.AsSpan(0)),
            FirstUsn = BinaryPrimitives.ReadInt64LittleEndian(lpOutput.AsSpan(8)),
            NextUsn = BinaryPrimitives.ReadInt64LittleEndian(lpOutput.AsSpan(16)),
            MaximumSize = BinaryPrimitives.ReadUInt64LittleEndian(lpOutput.AsSpan(40))
        };
    }

    /// <summary>一次增量读取：返回这一批变更，并给出下一次的起始 USN（用于批次合并后落盘）。</summary>
    public IReadOnlyList<UsnChange> ReadChanges(long startUsn, ulong journalId, out long nextUsn)
    {
        const int lpReadBuffer = 256 * 1024;
        var lpInput = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(0), startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(lpInput.AsSpan(8), 0xFFFFFFFF);
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(24), 0);
        BinaryPrimitives.WriteInt64LittleEndian(lpInput.AsSpan(32), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(lpInput.AsSpan(40), journalId);

        var lpOutput = new byte[lpReadBuffer];
        var lpChanges = new List<UsnChange>(256);
        nextUsn = startUsn;

        if (!DeviceIoControl(_lpHandle, FsctlReadUsnJournal, lpInput, (uint)lpInput.Length, lpOutput, (uint)lpOutput.Length, out var lpReturned, IntPtr.Zero))
        {
            var lpError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"FSCTL_READ_USN_JOURNAL 失败（Win32={lpError}）");
        }

        if (lpReturned <= 8)
        {
            return lpChanges;
        }

        nextUsn = BinaryPrimitives.ReadInt64LittleEndian(lpOutput.AsSpan(0));
        var lpOffset = 8;
        while (lpOffset < (int)lpReturned)
        {
            var lpRecordLength = BinaryPrimitives.ReadInt32LittleEndian(lpOutput.AsSpan(lpOffset));
            if (lpRecordLength <= 0 || lpOffset + lpRecordLength > (int)lpReturned)
            {
                break;
            }

            if (TryParseRecord(lpOutput.AsSpan(lpOffset, lpRecordLength), out var lpFrn, out var lpParentFrn, out var lpName, out var lpAttributes, out var lpIsDirectory, out var lpReason))
            {
                lpChanges.Add(new UsnChange
                {
                    Frn = lpFrn,
                    ParentFrn = lpParentFrn,
                    Name = lpName,
                    Attributes = lpAttributes,
                    IsDirectory = lpIsDirectory,
                    Reason = lpReason
                });
            }

            lpOffset += lpRecordLength;
        }

        return lpChanges;
    }

    private static bool TryParseRecord(
        ReadOnlySpan<byte> record,
        out ulong frn,
        out ulong parentFrn,
        out string name,
        out uint attributes,
        out bool isDirectory,
        out uint reason)
    {
        frn = 0;
        parentFrn = 0;
        name = string.Empty;
        attributes = 0;
        isDirectory = false;
        reason = 0;

        if (record.Length < 60)
        {
            return false;
        }

        var lpMajor = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        if (lpMajor != 2)
        {
            // M2 只支持 USN_RECORD_V2（NTFS 常规输出）；V3/V4 为 128 位 FRN，留给后续版本。
            return false;
        }

        frn = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
        reason = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
        attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
        var lpNameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
        var lpNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
        if (lpNameOffset + lpNameLength > record.Length)
        {
            return false;
        }

        isDirectory = (attributes & (uint)FileAttributes.Directory) != 0;
        name = System.Text.Encoding.Unicode.GetString(record.Slice(lpNameOffset, lpNameLength));
        return true;
    }

    public static bool IsCreateOrRenameNew(uint reason) =>
        (reason & (UsnReasonFileCreate | UsnReasonRenameNewName)) != 0;

    public static bool IsDeleteOrRenameOld(uint reason) =>
        (reason & (UsnReasonFileDelete | UsnReasonRenameOldName)) != 0;

    public void Dispose()
    {
        if (_lpHandle != IntPtr.Zero && _lpHandle != new IntPtr(-1))
        {
            CloseHandle(_lpHandle);
            _lpHandle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, byte[] inBuffer, uint inSize, byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr handle, uint code, IntPtr inBuffer, uint inSize, byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class UsnAccessException : Exception
{
    public UsnAccessException(string path, int errorCode, string message) : base(message)
    {
        Path = path;
        ErrorCode = errorCode;
    }

    public string Path { get; }

    public int ErrorCode { get; }
}

internal sealed class UsnJournalInfo
{
    public ulong JournalId { get; init; }

    public long FirstUsn { get; init; }

    public long NextUsn { get; init; }

    public ulong MaximumSize { get; init; }
}

internal sealed class UsnChange
{
    public ulong Frn { get; init; }

    public ulong ParentFrn { get; init; }

    public string Name { get; init; } = string.Empty;

    public uint Attributes { get; init; }

    public bool IsDirectory { get; init; }

    public uint Reason { get; init; }
}
