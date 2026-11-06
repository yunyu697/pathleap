using System.Diagnostics;
using System.Text.Json.Serialization;
using Pathleap.UI.Platform;

namespace Pathleap.UI.Perf;

/// <summary>
/// 一次“热键 -> 首帧 -> 可输入”采样的时间戳集合。
/// 所有时间戳都是 <see cref="Stopwatch.GetTimestamp"/> 的高精度 QPC tick，换算时统一除以 Frequency。
/// </summary>
internal sealed class WakeSample
{
    private readonly TaskCompletionSource _lpFocusTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _lpInputTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WakeSample(int index, string probeChar)
    {
        Index = index;
        ProbeChar = probeChar;
    }

    public int Index { get; }

    public string ProbeChar { get; }

    public long HotkeyTick { get; set; }

    public long? ShowCallTick { get; set; }

    public long? ActivateTick { get; set; }

    public long? FirstFrameTick { get; set; }

    public long? FocusTick { get; set; }

    public long? KeyTick { get; set; }

    public long? TextTick { get; set; }

    public string InputText { get; set; } = string.Empty;

    public bool ForegroundOk { get; set; }

    public bool VisibleOk { get; set; }

    public string Notes { get; set; } = string.Empty;

    public Task FocusTask => _lpFocusTcs.Task;

    public Task InputTask => _lpInputTcs.Task;

    public bool HotkeyReceived => ShowCallTick.HasValue;

    public bool FocusReached => FocusTick.HasValue;

    public bool InputDelivered => KeyTick.HasValue;

    public void MarkFocus(long tick)
    {
        FocusTick ??= tick;
        _lpFocusTcs.TrySetResult();
    }

    public void MarkKey(long tick)
    {
        KeyTick ??= tick;
        _lpInputTcs.TrySetResult();
    }

    public void MarkText(long tick)
    {
        TextTick ??= tick;
    }

    /// <summary>在输入事件发生的瞬间校验窗口确实是前台且可见，避免“焦点在但窗口没显示”的假达标。</summary>
    public void RecordWindowState(IntPtr hwnd)
    {
        ForegroundOk = NativeMethods.GetForegroundWindow().Equals(hwnd);
        VisibleOk = NativeMethods.IsWindowVisible(hwnd);
    }

    /// <summary>窗口因失焦/异常提前隐藏时调用，避免基准测试挂死。</summary>
    public void Abort(string reason)
    {
        Notes = string.IsNullOrEmpty(Notes) ? reason : $"{Notes};{reason}";
        _lpFocusTcs.TrySetResult();
        _lpInputTcs.TrySetResult();
    }

    public SampleResult ToResult()
    {
        var lpNotes = Notes;
        if (!HotkeyReceived)
        {
            lpNotes = Append(lpNotes, "hotkey-not-received");
        }

        if (!FocusReached)
        {
            lpNotes = Append(lpNotes, "focus-timeout");
        }

        if (!InputDelivered)
        {
            lpNotes = Append(lpNotes, "input-not-delivered");
        }

        if (!ForegroundOk)
        {
            lpNotes = Append(lpNotes, "foreground-not-ours");
        }

        if (!VisibleOk)
        {
            lpNotes = Append(lpNotes, "window-not-visible");
        }

        return new SampleResult
        {
            Index = Index,
            HotkeyToShowCallMs = Ms(HotkeyTick, ShowCallTick),
            HotkeyToActivateMs = Ms(HotkeyTick, ActivateTick),
            HotkeyToFirstFrameMs = Ms(HotkeyTick, FirstFrameTick),
            HotkeyToFocusMs = Ms(HotkeyTick, FocusTick),
            HotkeyToKeyMs = Ms(HotkeyTick, KeyTick),
            HotkeyToTextMs = Ms(HotkeyTick, TextTick),
            InputDelivered = InputDelivered,
            FocusReached = FocusReached,
            InputText = InputText,
            ForegroundOk = ForegroundOk,
            VisibleOk = VisibleOk,
            Notes = lpNotes
        };
    }

    private static string Append(string existing, string note) =>
        string.IsNullOrEmpty(existing) ? note : $"{existing};{note}";

    private static double Ms(long from, long? to) =>
        to.HasValue ? Math.Round((to.Value - from) * 1000.0 / Stopwatch.Frequency, 3) : -1;
}

/// <summary>单次采样的可序列化结果（-1 表示未发生）。</summary>
public sealed class SampleResult
{
    public int Index { get; set; }

    public double HotkeyToShowCallMs { get; set; }

    public double HotkeyToActivateMs { get; set; }

    public double HotkeyToFirstFrameMs { get; set; }

    public double HotkeyToFocusMs { get; set; }

    public double HotkeyToKeyMs { get; set; }

    public double HotkeyToTextMs { get; set; }

    public bool InputDelivered { get; set; }

    public bool FocusReached { get; set; }

    public string InputText { get; set; } = string.Empty;

    public bool ForegroundOk { get; set; }

    public bool VisibleOk { get; set; }

    /// <summary>是否为首个（冷）采样；冷采样不计入 warm 分位数。</summary>
    public bool Cold { get; set; }

    public string Notes { get; set; } = string.Empty;

    [JsonIgnore]
    public bool Valid => InputDelivered && FocusReached && ForegroundOk && VisibleOk;
}

/// <summary>统计摘要（毫秒）。</summary>
public sealed class LatencyStats
{
    public int Count { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }

    public double Mean { get; set; }

    public double P50 { get; set; }

    public double P95 { get; set; }

    public double P99 { get; set; }

    public static LatencyStats From(IEnumerable<double> values)
    {
        var lpSorted = values.Where(static v => v >= 0).OrderBy(static v => v).ToArray();
        if (lpSorted.Length == 0)
        {
            return new LatencyStats();
        }

        return new LatencyStats
        {
            Count = lpSorted.Length,
            Min = Math.Round(lpSorted[0], 3),
            Max = Math.Round(lpSorted[^1], 3),
            Mean = Math.Round(lpSorted.Average(), 3),
            P50 = Math.Round(Percentile(lpSorted, 50), 3),
            P95 = Math.Round(Percentile(lpSorted, 95), 3),
            P99 = Math.Round(Percentile(lpSorted, 99), 3)
        };
    }

    /// <summary>nearest-rank 百分位，避免小样本插值造成虚高/虚低。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        var lpRank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(lpRank, 0, sorted.Length - 1)];
    }
}
