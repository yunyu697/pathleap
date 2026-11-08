using System;

namespace Pathleap.Shared.Index;

/// <summary>
/// 运行期变更的合并节流：USN 日志每来一批记录只累加计数，等到「安静期」过去且距上次重建够久，
/// 才允许触发一次重建，避免安装程序、编译、下载这类连续写入把索引反复重建。
/// 纯逻辑，不依赖卷句柄，便于在非管理员环境用自检验证。
/// </summary>
public sealed class ChangeCoalescer
{
    private readonly object _lpGate = new();
    private readonly TimeSpan _lpQuietPeriod;
    private readonly TimeSpan _lpMinInterval;
    private long _lpPending;
    private DateTimeOffset _lpLastChange = DateTimeOffset.MinValue;
    private DateTimeOffset _lpLastRebuild;

    public ChangeCoalescer(TimeSpan quietPeriod, TimeSpan minInterval, DateTimeOffset lastRebuild)
    {
        _lpQuietPeriod = quietPeriod;
        _lpMinInterval = minInterval;
        _lpLastRebuild = lastRebuild;
    }

    public long Pending
    {
        get
        {
            lock (_lpGate)
            {
                return _lpPending;
            }
        }
    }

    public void Notify(long changes, DateTimeOffset now)
    {
        if (changes <= 0)
        {
            return;
        }

        lock (_lpGate)
        {
            _lpPending += changes;
            _lpLastChange = now;
        }
    }

    /// <summary>是否已经安静够久、且距上次重建达到最小间隔。</summary>
    public bool ShouldRebuild(DateTimeOffset now)
    {
        lock (_lpGate)
        {
            return _lpPending > 0
                   && now - _lpLastChange >= _lpQuietPeriod
                   && now - _lpLastRebuild >= _lpMinInterval;
        }
    }

    /// <summary>登记一次重建，返回本次合并掉的变更条数。</summary>
    public long MarkRebuilt(DateTimeOffset now)
    {
        lock (_lpGate)
        {
            var lpPending = _lpPending;
            _lpPending = 0;
            _lpLastRebuild = now;
            return lpPending;
        }
    }
}
