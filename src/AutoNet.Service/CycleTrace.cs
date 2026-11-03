using System.Globalization;
using AutoNet.Core;

namespace AutoNet.Service;

/// <summary>
/// One monitoring cycle's bounded, de-identified stage timings. Stage names are fixed
/// literals; only elapsed milliseconds are recorded. A container phase may overlap its
/// children, so <see cref="Summary"/> ranks work — it is not a decomposition of the total.
/// </summary>
internal sealed class CycleTrace(TimeProvider clock)
{
    public const double SlowStepMilliseconds = 500;
    public const double SlowCycleMilliseconds = 5000;

    private readonly long started = clock.GetTimestamp();
    private readonly Dictionary<string, Stage> stages = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool flushed;

    public double TotalMilliseconds => Math.Max(0, clock.GetElapsedTime(started).TotalMilliseconds);

    public (string Name, double Milliseconds, int Count)? Slowest
    {
        get
        {
            lock (gate)
                return stages.Count == 0
                    ? null
                    : stages.OrderByDescending(pair => pair.Value.Milliseconds)
                        .Select(pair => (pair.Key, pair.Value.Milliseconds, pair.Value.Count)).First();
        }
    }

    public T Measure<T>(string stage, Func<T> action)
    {
        var begin = clock.GetTimestamp();
        try { return action(); }
        finally { Add(stage, clock.GetElapsedTime(begin).TotalMilliseconds); }
    }

    public void Measure(string stage, Action action)
    {
        var begin = clock.GetTimestamp();
        try { action(); }
        finally { Add(stage, clock.GetElapsedTime(begin).TotalMilliseconds); }
    }

    public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> action)
    {
        var begin = clock.GetTimestamp();
        try { return await action().ConfigureAwait(true); }
        finally { Add(stage, clock.GetElapsedTime(begin).TotalMilliseconds); }
    }

    public async Task MeasureAsync(string stage, Func<Task> action)
    {
        var begin = clock.GetTimestamp();
        try { await action().ConfigureAwait(true); }
        finally { Add(stage, clock.GetElapsedTime(begin).TotalMilliseconds); }
    }

    public string Summary(int top = 5)
    {
        lock (gate)
        {
            if (stages.Count == 0) return "无";
            return string.Join("; ", stages.OrderByDescending(pair => pair.Value.Milliseconds).Take(top)
                .Select(pair => pair.Key + "=" + Format(pair.Value.Milliseconds) + "ms"
                    + (pair.Value.Count > 1 ? "×" + pair.Value.Count.ToString(CultureInfo.InvariantCulture) : "")));
        }
    }

    /// <summary>
    /// Writes at most one aggregate event per cycle. A cycle at or above the slow threshold
    /// produces one <c>SlowCycle</c> carrying the top stages; otherwise each aggregated stage
    /// at or above the step threshold produces a <c>SlowStep</c> (max five). When
    /// <paramref name="limiter"/> is supplied, repeated evidence for the same stage is
    /// suppressed across cycles instead of writing one entry every round.
    ///
    /// Never throws: this runs from <c>finally</c>, so a logging fault must not mask the
    /// cycle's original outcome. <see cref="RecoveryJournal"/> already reports write failures
    /// through its own fallback channel.
    /// </summary>
    public void Flush(RecoveryJournal journal, SlowStepLimiter? limiter = null,
        double slowStep = SlowStepMilliseconds, double slowCycle = SlowCycleMilliseconds)
    {
        try { FlushCore(journal, limiter, slowStep, slowCycle); }
        catch (Exception) { }
    }

    private void FlushCore(RecoveryJournal journal, SlowStepLimiter? limiter, double slowStep, double slowCycle)
    {
        lock (gate)
        {
            if (flushed) return;
            flushed = true;
        }
        var total = TotalMilliseconds;
        if (total >= slowCycle)
        {
            var summary = Summary();
            journal.Write("SlowCycle",
                $"检测轮次耗时 {Format(total)} ms，超过 {Format(slowCycle)} ms；阶段排名：{summary}。阶段可能重叠，排名不是总和的分解。",
                new DiagnosticData
                {
                    Stage = "Cycle", Outcome = "Slow", ElapsedMilliseconds = Math.Round(total, 1), Reason = summary
                });
            return;
        }
        var slow = Snapshot().Where(stage => stage.Milliseconds >= slowStep).Take(5).ToArray();
        foreach (var stage in slow)
        {
            if (limiter is not null && !limiter.ShouldEmit(stage.Name, stage.Milliseconds)) continue;
            journal.Write("SlowStep",
                $"检测阶段 {stage.Name} 累计耗时 {Format(stage.Milliseconds)} ms，超过 {Format(slowStep)} ms。",
                new DiagnosticData
                {
                    Stage = stage.Name, Outcome = "Slow", ElapsedMilliseconds = Math.Round(stage.Milliseconds, 1)
                });
        }
    }

    private IReadOnlyList<(string Name, double Milliseconds, int Count)> Snapshot()
    {
        lock (gate)
            return stages.OrderByDescending(pair => pair.Value.Milliseconds)
                .Select(pair => (pair.Key, pair.Value.Milliseconds, pair.Value.Count)).ToArray();
    }

    private void Add(string stage, double milliseconds)
    {
        lock (gate)
        {
            stages[stage] = stages.TryGetValue(stage, out var existing)
                ? existing with { Milliseconds = existing.Milliseconds + Math.Max(0, milliseconds), Count = existing.Count + 1 }
                : new Stage(Math.Max(0, milliseconds), 1);
        }
    }

    private static string Format(double milliseconds) => milliseconds.ToString("0.#", CultureInfo.InvariantCulture);

    private readonly record struct Stage(double Milliseconds, int Count);
}
