namespace AutoNet.Service;

/// <summary>
/// Cross-cycle limiter for repeated <c>SlowStep</c> evidence (工单 #1 复查).
///
/// A stage that is persistently slow would otherwise write one JSONL entry every monitoring
/// cycle — roughly every two seconds — making the diagnostic path itself a source of I/O.
/// This keeps the first occurrence, re-reports the same stage after <c>repeatInterval</c>,
/// and re-reports immediately when the stage significantly worsens. Different stages are
/// never suppressed by each other.
/// </summary>
internal sealed class SlowStepLimiter(TimeProvider clock, TimeSpan repeatInterval)
{
    private readonly object gate = new();
    private string? lastStage;
    private double lastMilliseconds;
    private long? lastTimestamp;

    public bool ShouldEmit(string stage, double milliseconds)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            var same = string.Equals(lastStage, stage, StringComparison.Ordinal);
            var elapsed = lastTimestamp is { } at ? clock.GetElapsedTime(at) : TimeSpan.MaxValue;
            // 显著恶化才在间隔内立即补记：同一阶段至少翻倍且达到 1 秒，过滤正常抖动。
            var worsened = same && milliseconds > lastMilliseconds
                && milliseconds >= Math.Max(lastMilliseconds * 2, 1000);
            if (same && !worsened && elapsed < repeatInterval) return false;
            lastStage = stage;
            lastMilliseconds = milliseconds;
            lastTimestamp = now;
            return true;
        }
    }
}
