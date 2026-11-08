using System;
using System.Runtime.InteropServices;

namespace Pathleap.Service.Index;

/// <summary>
/// USN 日志变更检测：只读卷句柄 + FSCTL_READ_USN_JOURNAL，累计运行期的文件变化条数。
/// 本类不负责重建，只负责「有没有变、变了多少」；节流与触发交给 <see cref="Pathleap.Shared.Index.ChangeCoalescer"/>。
/// 需要管理员或 LocalSystem 才能打开卷句柄；拿不到时 <see cref="TryStart"/> 返回 false，
/// 由调用方把原因写进日志/状态，不静默。
/// </summary>
internal sealed class UsnJournalWatcher : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const int QueryBufferSize = 64;
    private const int ReadBufferSize = 16 * 1024;

    private const int ErrorSemTimeout = 121;
    private const int ErrorWaitTimeout = 258;
    private const int ErrorTimeout = 1460;
    private const int ErrorJournalNotActive = 1359;
    private const int ErrorJournalDeleteInProgress = 1178;

    private readonly string _lpVolumePath;
    private IntPtr _lpHandle = IntPtr.Zero;
    private long _lpJournalId;
    private long _lpNextUsn;
    private readonly byte[] _lpReadBuffer = new byte[ReadBufferSize];

    public UsnJournalWatcher(string root)
    {
        var lpRoot = string.IsNullOrWhiteSpace(root) ? "C:\\" : root.Trim();
        var lpDrive = lpRoot.Length >= 2 && lpRoot[1] == ':' ? lpRoot[..2] : "C:";
        _lpVolumePath = @"\\.\" + lpDrive;
    }

    /// <summary>最近一次失败原因；TryStart 失败时为 Win32 错误描述。</summary>
    public string Error { get; private set; } = string.Empty;

    public bool TryStart()
    {
        _lpHandle = CreateFileW(
            _lpVolumePath,
            GenericRead,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (_lpHandle == IntPtr.Zero || _lpHandle == new IntPtr(-1))
        {
            var lpError = Marshal.GetLastWin32Error();
            _lpHandle = IntPtr.Zero;
            Error = $"打开卷句柄被拒绝（{Describe(lpError)}，Win32 {lpError}）";
            return false;
        }

        var lpQuery = new byte[QueryBufferSize];
        if (!DeviceIoControl(_lpHandle, FsctlQueryUsnJournal, IntPtr.Zero, 0, lpQuery, lpQuery.Length, out var lpBytes, IntPtr.Zero)
            || lpBytes < Marshal.SizeOf<UsnJournalData>())
        {
            var lpError = Marshal.GetLastWin32Error();
            Error = $"查询 USN 日志失败（{Describe(lpError)}，Win32 {lpError}）";
            CloseHandleQuietly();
            return false;
        }

        var lpJournal = BytesToStruct<UsnJournalData>(lpQuery);
        _lpJournalId = (long)lpJournal.JournalId;
        _lpNextUsn = lpJournal.NextUsn;
        Error = string.Empty;
        return true;
    }

    /// <summary>
    /// 阻塞最多 <paramref name="timeoutMs"/> 毫秒读一批变更。返回变更记录条数；
    /// 0 表示超时无变化；-1 表示日志不可用（原因写进 <see cref="Error"/>）。
    /// </summary>
    public int Poll(int timeoutMs)
    {
        if (_lpHandle == IntPtr.Zero)
        {
            return -1;
        }

        var lpRequest = new ReadUsnJournalData
        {
            StartUsn = _lpNextUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = (ulong)Math.Max(0, timeoutMs),
            BytesToWaitFor = 0,
            JournalId = (ulong)_lpJournalId
        };

        if (!DeviceIoControl(
                _lpHandle,
                FsctlReadUsnJournal,
                StructToBytes(lpRequest),
                Marshal.SizeOf<ReadUsnJournalData>(),
                _lpReadBuffer,
                _lpReadBuffer.Length,
                out var lpBytes,
                IntPtr.Zero))
        {
            var lpError = Marshal.GetLastWin32Error();
            if (lpError is ErrorSemTimeout or ErrorTimeout or ErrorWaitTimeout)
            {
                return 0;
            }

            Error = lpError switch
            {
                ErrorJournalNotActive => "USN 日志未启用或已被删除，运行期自动刷新停止",
                ErrorJournalDeleteInProgress => "USN 日志正在删除，运行期自动刷新停止",
                _ => $"读取 USN 日志失败（{Describe(lpError)}，Win32 {lpError}）"
            };
            return -1;
        }

        if (lpBytes < 8)
        {
            return 0;
        }

        _lpNextUsn = BitConverter.ToInt64(_lpReadBuffer, 0);
        var lpOffset = 8;
        var lpCount = 0;
        while (lpOffset + 8 <= lpBytes)
        {
            var lpRecordLength = BitConverter.ToUInt32(_lpReadBuffer, lpOffset);
            if (lpRecordLength < 60 || lpOffset + (int)lpRecordLength > _lpReadBuffer.Length)
            {
                break;
            }

            var lpMajor = BitConverter.ToUInt16(_lpReadBuffer, lpOffset + 4);
            if ((lpMajor == 2 || lpMajor == 3) && lpRecordLength >= 60)
            {
                var lpReason = BitConverter.ToUInt32(_lpReadBuffer, lpOffset + 40);
                if (lpReason != 0)
                {
                    lpCount++;
                }
            }

            lpOffset += (int)lpRecordLength;
            if (lpOffset >= lpBytes)
            {
                break;
            }
        }

        return lpCount;
    }

    private static string Describe(int error)
    {
        return error switch
        {
            5 => "ERROR_ACCESS_DENIED",
            1359 => "ERROR_JOURNAL_NOT_ACTIVE",
            1178 => "ERROR_JOURNAL_DELETE_IN_PROGRESS",
            _ => "error"
        };
    }

    private void CloseHandleQuietly()
    {
        if (_lpHandle != IntPtr.Zero)
        {
            CloseHandle(_lpHandle);
            _lpHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        CloseHandleQuietly();
    }

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var lpSize = Marshal.SizeOf<T>();
        var lpBytes = new byte[lpSize];
        var lpHandle = GCHandle.Alloc(lpBytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, lpHandle.AddrOfPinnedObject(), false);
        }
        finally
        {
            lpHandle.Free();
        }

        return lpBytes;
    }

    private static T BytesToStruct<T>(byte[] bytes) where T : struct
    {
        var lpHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(lpHandle.AddrOfPinnedObject());
        }
        finally
        {
            lpHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalData
    {
        public ulong JournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalData
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong JournalId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        IntPtr input,
        int inputSize,
        byte[] output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        byte[] input,
        int inputSize,
        byte[] output,
        int outputSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
