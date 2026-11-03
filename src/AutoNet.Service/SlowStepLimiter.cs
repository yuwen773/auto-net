namespace AutoNet.Service;

/// <summary>
/// Cross-cycle limiter for repeated <c>SlowStep</c> evidence (工单 #1 复查).
///
/// A stage that is persistently slow would otherwise write one JSONL entry every monitoring
/// cycle — roughly every two seconds — making the diagnostic path itself a source of I/O.
/// This keeps the first occurrence, re-reports each stage after <c>repeatInterval</c>,
/// and re-reports immediately when that stage significantly worsens. Every stage keeps its
/// own window, so alternating slow stages neither suppress nor reset each other. Stage names
/// come from fixed measurement sites only, so the state table stays small and bounded.
/// </summary>
internal sealed class SlowStepLimiter(TimeProvider clock, TimeSpan repeatInterval)
{
    private sealed record StageState(double Milliseconds, long Timestamp);

    private readonly object gate = new();
    private readonly Dictionary<string, StageState> stages = new(StringComparer.Ordinal);

    public bool ShouldEmit(string stage, double milliseconds)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (stages.TryGetValue(stage, out var seen))
            {
                var elapsed = clock.GetElapsedTime(seen.Timestamp);
                // 显著恶化才在间隔内立即补记：同一阶段至少翻倍且达到 1 秒，过滤正常抖动。
                var worsened = milliseconds > seen.Milliseconds
                    && milliseconds >= Math.Max(seen.Milliseconds * 2, 1000);
                if (!worsened && elapsed < repeatInterval) return false;
            }
            stages[stage] = new StageState(milliseconds, now);
            return true;
        }
    }
}
