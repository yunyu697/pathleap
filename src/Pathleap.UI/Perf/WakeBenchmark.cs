using Pathleap.Shared.Diagnostics;
using Pathleap.UI.Platform;

namespace Pathleap.UI.Perf;

/// <summary>
/// 唤醒基准：每个采样都完整走一次“隐藏窗口 -> 注入热键 -> WM_HOTKEY -> 显示/聚焦 -> 注入探测键 -> 收起”。
/// 注入使用 SendInput，与真实键盘同一条输入路径；不是对内部函数的计时。
/// </summary>
internal static class WakeBenchmark
{
    public static async Task<(IReadOnlyList<SampleResult> Samples, DateTimeOffset StartedAt, DateTimeOffset FinishedAt)> RunAsync(
        MainWindow window,
        HotkeySpec hotkey,
        string probeChar,
        int samples,
        CancellationToken cancellationToken)
    {
        var lpStartedAt = DateTimeOffset.Now;
        var lpResults = new List<SampleResult>(samples);
        AppLog.Info($"基准测试开始：{samples} 次，热键 {hotkey.Display}，探测键 '{probeChar}'");

        window.HideForBenchmark();
        await Task.Delay(600, cancellationToken).ConfigureAwait(true);

        for (var lpIndex = 0; lpIndex < samples; lpIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(350, cancellationToken).ConfigureAwait(true);

            var lpSample = window.ArmMeasurement(lpIndex, probeChar);

            if (!InputInjector.SendHotkey(hotkey))
            {
                lpSample.Abort("send-hotkey-failed");
                var lpFailed = lpSample.ToResult();
                lpFailed.Cold = lpIndex == 0;
                lpResults.Add(lpFailed);
                window.CancelMeasurement("send-hotkey-failed");
                continue;
            }

            var lpFocusOk = await WaitAsync(lpSample.FocusTask, 1000, cancellationToken).ConfigureAwait(true);
            if (!lpFocusOk)
            {
                var lpNoFocus = lpSample.ToResult();
                lpNoFocus.Cold = lpIndex == 0;
                lpResults.Add(lpNoFocus);
                window.CancelMeasurement("focus-timeout");
                AppLog.Warn($"采样 {lpIndex}: 热键后 1000ms 内未获得焦点。");
                window.HideForBenchmark();
                continue;
            }

            if (!InputInjector.SendProbeKey(probeChar[0]))
            {
                lpSample.Abort("send-probe-failed");
            }

            await WaitAsync(lpSample.InputTask, 600, cancellationToken).ConfigureAwait(true);
            await Task.Delay(80, cancellationToken).ConfigureAwait(true);

            var lpResult = lpSample.ToResult();
            lpResult.Cold = lpIndex == 0;
            lpResults.Add(lpResult);

            if (!lpResult.Valid)
            {
                AppLog.Warn(
                    $"采样 {lpIndex} 异常：key={lpResult.HotkeyToKeyMs}ms focus={lpResult.HotkeyToFocusMs}ms " +
                    $"notes={lpResult.Notes}");
            }

            window.HideForBenchmark();

            if ((lpIndex + 1) % 20 == 0)
            {
                AppLog.Info($"基准进度: {lpIndex + 1}/{samples}");
            }
        }

        var lpFinishedAt = DateTimeOffset.Now;
        AppLog.Info($"基准测试结束：{lpResults.Count} 条记录，耗时 {(lpFinishedAt - lpStartedAt).TotalSeconds:0.0}s");
        return (lpResults, lpStartedAt, lpFinishedAt);
    }

    private static async Task<bool> WaitAsync(Task task, int timeoutMs, CancellationToken cancellationToken)
    {
        using var lpLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lpLinked.CancelAfter(timeoutMs);
        try
        {
            await task.WaitAsync(lpLinked.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
