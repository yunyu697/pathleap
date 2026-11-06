using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Pathleap.Shared;
using Pathleap.UI.Platform;

namespace Pathleap.UI.Perf;

public sealed class MachineInfo
{
    public string Os { get; set; } = RuntimeInformation.OSDescription;

    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;

    public string Cpu { get; set; } = string.Empty;

    public int LogicalProcessors { get; set; } = Environment.ProcessorCount;

    public int DisplayRefreshHz { get; set; }

    public uint DisplayDpi { get; set; }

    public string Framework { get; set; } = RuntimeInformation.FrameworkDescription;

    public string ProcessArchitecture { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();

    public static MachineInfo Capture()
    {
        var lpInfo = new MachineInfo();
        try
        {
            using var lpKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            lpInfo.Cpu = (lpKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
        }
        catch
        {
            lpInfo.Cpu = "unknown";
        }

        var lpMode = new NativeMethods.DevModeW
        {
            DeviceName = string.Empty,
            Size = (ushort)Marshal.SizeOf<NativeMethods.DevModeW>()
        };
        if (NativeMethods.EnumDisplaySettingsW(null, -1, ref lpMode))
        {
            lpInfo.DisplayRefreshHz = (int)lpMode.DisplayFrequency;
        }

        try
        {
            var lpPoint = new NativeMethods.Point { X = 0, Y = 0 };
            var lpMonitor = NativeMethods.MonitorFromPoint(lpPoint, NativeMethods.MonitorDefaultToNearest);
            if (!IntPtr.Zero.Equals(lpMonitor)
                && NativeMethods.GetDpiForMonitor(lpMonitor, NativeMethods.MdtEffectiveDpi, out var lpDpiX, out _) == 0)
            {
                lpInfo.DisplayDpi = lpDpiX;
            }
        }
        catch
        {
            // 某些远程/无显示器会话下取不到 DPI，留 0 即可。
        }

        return lpInfo;
    }
}

public sealed class BenchSummary
{
    public string Product { get; set; } = Branding.ProductId;

    public string AppVersion { get; set; } = Branding.Version;

    public string StartedAt { get; set; } = string.Empty;

    public string FinishedAt { get; set; } = string.Empty;

    public MachineInfo Machine { get; set; } = new();

    public string Hotkey { get; set; } = string.Empty;

    public string ProbeChar { get; set; } = string.Empty;

    public int SamplesRequested { get; set; }

    public int SamplesCompleted { get; set; }

    public int Failures { get; set; }

    public double P50TargetMs { get; set; } = 45;

    public double P95TargetMs { get; set; } = 90;

    /// <summary>热键 -> 首帧（CompositionTarget.Rendering 首次回调）。</summary>
    public LatencyStats FirstFrame { get; set; } = new();

    /// <summary>热键 -> 输入框获得焦点。</summary>
    public LatencyStats Focus { get; set; } = new();

    /// <summary>热键 -> 注入探测键被 UI 输入栈接收（“可输入”主指标）。</summary>
    public LatencyStats InputReady { get; set; } = new();

    /// <summary>热键 -> 文本框实际收到字符（受输入法影响，可能少于采样数）。</summary>
    public LatencyStats TextCommit { get; set; } = new();

    public LatencyStats Activate { get; set; } = new();

    public LatencyStats ShowCall { get; set; } = new();

    public SampleResult? ColdSample { get; set; }

    public bool Pass { get; set; }

    public string Verdict { get; set; } = string.Empty;

    public long PrivateWorkingSetBytes { get; set; }

    public long PrivateMemoryBytes { get; set; }

    public string Methodology { get; set; } =
        "T0 = 原生消息线程收到 WM_HOTKEY 并执行的第一条指令时间戳（Stopwatch/QPC）；" +
        "首帧 = WinUI CompositionTarget.Rendering 首次回调；" +
        "可输入 = UI 线程 TextBox.KeyDown 收到由 SendInput 注入的探测键（随后 80ms 内读取实际文本）；" +
        "探测键在 GotFocus 之后立刻注入，走与真实键盘相同的系统输入路径。" +
        "冷采样（第 1 次）单列，不计入 warm 分位数；分位数使用 nearest-rank。";

    public List<SampleResult> Samples { get; set; } = new();

    public string CsvPath { get; set; } = string.Empty;

    public string JsonPath { get; set; } = string.Empty;
}

public static class PerfReport
{
    public static BenchSummary Build(
        IReadOnlyList<SampleResult> samples,
        string hotkey,
        string probeChar,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt)
    {
        var lpSummary = new BenchSummary
        {
            StartedAt = startedAt.ToString("O"),
            FinishedAt = finishedAt.ToString("O"),
            Machine = MachineInfo.Capture(),
            Hotkey = hotkey,
            ProbeChar = probeChar,
            SamplesRequested = samples.Count,
            SamplesCompleted = samples.Count(static s => s.HotkeyToKeyMs >= 0),
            Failures = samples.Count(static s => !s.Cold && !s.Valid),
            Samples = samples.ToList()
        };

        if (samples.Count > 0)
        {
            lpSummary.ColdSample = samples[0];
        }

        var lpWarm = samples.Where(static s => !s.Cold && s.Valid).ToArray();
        lpSummary.FirstFrame = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToFirstFrameMs));
        lpSummary.Focus = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToFocusMs));
        lpSummary.InputReady = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToKeyMs));
        lpSummary.TextCommit = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToTextMs));
        lpSummary.Activate = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToActivateMs));
        lpSummary.ShowCall = LatencyStats.From(lpWarm.Select(static s => s.HotkeyToShowCallMs));

        using var lpProcess = System.Diagnostics.Process.GetCurrentProcess();
        lpProcess.Refresh();
        lpSummary.PrivateWorkingSetBytes = lpProcess.WorkingSet64;
        lpSummary.PrivateMemoryBytes = lpProcess.PrivateMemorySize64;

        var lpEnoughValid = lpWarm.Length >= Math.Max(10, (int)(samples.Count * 0.95));
        lpSummary.Pass = lpEnoughValid
                         && lpSummary.Failures == 0
                         && lpSummary.InputReady.P50 <= lpSummary.P50TargetMs
                         && lpSummary.InputReady.P95 <= lpSummary.P95TargetMs;
        lpSummary.Verdict = lpSummary.Pass
            ? $"达标：可输入 P50 {lpSummary.InputReady.P50:0.###}ms / P95 {lpSummary.InputReady.P95:0.###}ms（阈值 {lpSummary.P50TargetMs}/{lpSummary.P95TargetMs}ms）"
            : $"未达标或数据不足：可输入 P50 {lpSummary.InputReady.P50:0.###}ms / P95 {lpSummary.InputReady.P95:0.###}ms，" +
              $"有效 warm 样本 {lpWarm.Length}/{samples.Count}，失败 {lpSummary.Failures}";

        return lpSummary;
    }

    public static void Write(BenchSummary summary)
    {
        var lpRoot = AppPaths.EnsureDirectory(AppPaths.PerfRoot);
        var lpStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        summary.CsvPath = Path.Combine(lpRoot, $"wake-latency-{lpStamp}.csv");
        summary.JsonPath = Path.Combine(lpRoot, $"wake-latency-{lpStamp}.json");

        var lpCsv = BuildCsv(summary.Samples);
        File.WriteAllText(summary.CsvPath, lpCsv, new UTF8Encoding(false));

        var lpJson = JsonSerializer.Serialize(summary, JsonDefaults.Options);
        File.WriteAllText(summary.JsonPath, lpJson, new UTF8Encoding(false));

        // 便利副本，供脚本/后续里程碑直接读取“最近一次”结果。
        File.WriteAllText(Path.Combine(lpRoot, "last-bench-summary.json"), lpJson, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(lpRoot, "last-bench-samples.csv"), lpCsv, new UTF8Encoding(false));
    }

    private static string BuildCsv(IEnumerable<SampleResult> samples)
    {
        var lpBuilder = new StringBuilder();
        lpBuilder.AppendLine(
            "index,cold,hotkey_to_show_call_ms,hotkey_to_activate_ms,hotkey_to_first_frame_ms," +
            "hotkey_to_focus_ms,hotkey_to_key_ms,hotkey_to_text_ms,input_delivered,focus_reached," +
            "foreground_ok,visible_ok,input_text,notes");
        foreach (var lpSample in samples)
        {
            lpBuilder.Append(lpSample.Index).Append(',')
                .Append(lpSample.Cold ? '1' : '0').Append(',')
                .Append(Format(lpSample.HotkeyToShowCallMs)).Append(',')
                .Append(Format(lpSample.HotkeyToActivateMs)).Append(',')
                .Append(Format(lpSample.HotkeyToFirstFrameMs)).Append(',')
                .Append(Format(lpSample.HotkeyToFocusMs)).Append(',')
                .Append(Format(lpSample.HotkeyToKeyMs)).Append(',')
                .Append(Format(lpSample.HotkeyToTextMs)).Append(',')
                .Append(lpSample.InputDelivered ? '1' : '0').Append(',')
                .Append(lpSample.FocusReached ? '1' : '0').Append(',')
                .Append(lpSample.ForegroundOk ? '1' : '0').Append(',')
                .Append(lpSample.VisibleOk ? '1' : '0').Append(',')
                .Append(Escape(lpSample.InputText)).Append(',')
                .Append(Escape(lpSample.Notes))
                .AppendLine();
        }

        return lpBuilder.ToString();
    }

    private static string Format(double value) =>
        value < 0 ? string.Empty : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
    }
}
