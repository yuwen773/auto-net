using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>一次状态读取：原始快照、经规则映射的显示状态与可信度。</summary>
internal sealed record StatusReading(
    StatusSnapshot? Snapshot,
    NetworkDisplay Display,
    bool Valid,
    bool Fresh,
    TimeSpan? Age,
    DateTimeOffset ReadAt)
{
    /// <summary>
    /// 壳底部状态栏文本：只描述后台服务与状态文件的可信度，不复述网络状态本身
    /// （网络状态由概览页呈现）。措辞与既有 WinForms 托盘逐字一致。
    /// </summary>
    public string StripText
    {
        get
        {
            if (Snapshot is null) return "后台服务未就绪";
            if (!Valid) return "后台状态不可确认";
            var stamp = Snapshot.UpdatedAt.ToLocalTime().ToString("HH:mm:ss");
            return Fresh
                ? $"后台服务运行中 · 最近检测 {stamp}"
                : $"后台状态已过期 · 最后更新 {stamp}";
        }
    }
}

/// <summary>
/// 状态管道：周期异步读取 status.json，并经 Core 的完整性、时间有效性与安全降级规则
/// （<see cref="StatusSnapshotReader"/>、<see cref="NetworkDisplayMap"/>）产出显示状态。
/// 边界（规格实施决策 4）：只轮询状态文件，不新增 HTTP 服务、socket 或守护进程。
/// 读取按发起序号应用，旧读不覆盖新读；<see cref="Dispose"/> 后不再产生任何通知（销毁保护）。
/// </summary>
internal sealed class StatusPipe : IDisposable
{
    /// <summary>轮询周期；与既有 WinForms 托盘一致。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>状态文件路径；QA 可用 AUTONET_QA_STATUS_PATH 重定向到夹具（与既有托盘约定一致）。</summary>
    public static string ResolveStatusPath() =>
        Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH") is { Length: > 0 } path
            ? path
            : Storage.StatusPath;

    private readonly string path;
    private readonly TimeSpan interval;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private long issued;
    private long applied;
    private StatusReading? current;
    private CancellationTokenSource? loopCancellation;
    private bool disposed;

    public StatusPipe(string path, TimeSpan? interval = null, Func<DateTimeOffset>? clock = null)
    {
        this.path = path;
        this.interval = interval ?? DefaultInterval;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>最近一次已应用的状态读取；首次读取完成前为 null。</summary>
    public StatusReading? Current
    {
        get { lock (gate) return current; }
    }

    /// <summary>状态更新（在读取线程上触发；UI 侧负责调度到界面线程）。</summary>
    public event Action<StatusReading>? Updated;

    /// <summary>开始周期读取。幂等；销毁后调用被忽略。</summary>
    public void Start()
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed || loopCancellation is not null) return;
            loopCancellation = new CancellationTokenSource();
            token = loopCancellation.Token;
        }

        _ = Task.Run(() => LoopAsync(token));
    }

    /// <summary>立即读取一次并应用（窗口激活或 QA 驱动夹具切换时使用）。销毁后调用抛出 <see cref="ObjectDisposedException"/>。</summary>
    public async Task<StatusReading> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 先取号：销毁后调用立即抛出，不产生多余的文件读取。
        var sequence = NextSequence();
        var reading = await Task.Run(() => ReadNow(path, clock()), cancellationToken).ConfigureAwait(false);
        Apply(sequence, reading);
        return reading;
    }

    /// <summary>
    /// 同步读取一次（不应用；供测试与 QA 断言使用）。
    /// 文件缺失、不可读或不可解析一律按“没有状态”降级，绝不沿用上一次内容。
    /// </summary>
    public static StatusReading ReadNow(string path, DateTimeOffset now)
    {
        StatusSnapshot? status = null;
        try
        {
            if (File.Exists(path)) status = StatusSnapshotReader.Parse(Storage.ReadShared(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // 与既有托盘一致：读取失败按“后台服务未就绪”呈现。
        }

        var age = status is null ? (TimeSpan?)null : now - status.UpdatedAt;
        var valid = StatusSnapshotReader.IsValid(status);
        var fresh = valid && NetworkDisplayMap.IsFresh(age);
        return new StatusReading(status, NetworkDisplayMap.Map(status, age), valid, fresh, age, now);
    }

    /// <summary>销毁：取消后台循环并拒绝一切后续通知。幂等。</summary>
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation = loopCancellation;
            loopCancellation = null;
        }

        // 只取消，不主动 Dispose，避免与在途读取的注册/检查竞争。
        cancellation?.Cancel();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var reading = await Task.Run(() => ReadNow(path, clock()), token).ConfigureAwait(false);
                    Apply(NextSequence(), reading);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // 读取以外的意外异常不中断循环；下一轮读取自行恢复。异常绝不留给未观察的任务。
                }

                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private long NextSequence()
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(StatusPipe));
            return ++issued;
        }
    }

    private void Apply(long sequence, StatusReading reading)
    {
        lock (gate)
        {
            if (disposed || sequence <= applied) return;
            applied = sequence;
            current = reading;
            // 在锁内回调：保证 Dispose 返回后不再有任何通知（处理程序只做轻量调度）。
            Updated?.Invoke(reading);
        }
    }
}
