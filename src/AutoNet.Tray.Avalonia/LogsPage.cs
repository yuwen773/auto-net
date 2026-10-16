using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 日志页控制器（迁移工单 #7 / T09 / 故事 44–51）：
/// - 日期入口：枚举日志目录的单日文件（最新在前），默认选中最新一天，刷新保留仍在的选择；
/// - 受限读取：单日最多 <see cref="LogTailReader.DefaultMaxRecords"/> 条，范围与缺口显式投影（故事 45、49）；
/// - 快速切换：同一时刻至多一个在途读取；新选择立即取代旧读取并取消之，旧结果一律丢弃，
///   只展示当前选择对应的最新读取结果（故事 50）；
/// - 重复刷新稳定：同一选择重复刷新产生一致内容（故事 50）；
/// - 兼容与降级：读取失败按固定受控文案降级并只披露异常类型，页面不失效（故事 51、安全边界）。
/// 日期枚举与文件读取接缝全部注入，可受控测试；本类不依赖 UI 类型。
/// </summary>
internal sealed class LogsPage : IDisposable
{
    private readonly Func<string> resolveDirectory;
    private readonly Func<string, CancellationToken, LogTailRead> read;
    private readonly object gate = new();
    private LogsState state = LogsState.Initial;
    private bool disposed;
    private bool reloadRequested;
    private bool reading;
    private long generation;
    private CancellationTokenSource? readCancellation;
    private CancellationToken? navigationToken;
    private TaskCompletionSource<PageContent>? navigationCompletion;
    private Task inFlight = Task.CompletedTask;

    /// <summary>页内内容更新（导航加载、日期切换、筛选与行选择）；壳负责按当前页面守卫应用。</summary>
    public event Action<LogsContent>? Updated;

    public LogsPage(Func<string> resolveDirectory, Func<string, CancellationToken, LogTailRead>? read = null)
    {
        this.resolveDirectory = resolveDirectory;
        this.read = read ?? ((path, token) => LogTailReader.ReadDetailed(path, cancellationToken: token));
    }

    /// <summary>生产构造：读取既有日志目录（QA 夹具模式仅在同一 QA 模式下重定向，普通机器永不生效）。</summary>
    public static LogsPage Production(QaEnvironment qa) => new(
        () => qa.Mode == QaMode.Logs && Environment.GetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY") is { Length: > 0 } fixture
            ? fixture
            : Storage.Logs);

    /// <summary>
    /// 壳导航接缝：枚举日期并读取当前选择。结果同时经 <see cref="Updated"/> 应用；
    /// 页面已被切走时结果由壳守卫丢弃，旧读取不会覆写当前页（故事 50）。
    /// </summary>
    public Task<PageContent> LoadAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<PageContent> completion;
        lock (gate)
        {
            if (disposed) return Task.FromResult<PageContent>(LogsModel.From(state));
            // 旧导航接缝按取消收尾：它的结果已不代表当前导航。
            navigationCompletion?.TrySetCanceled();
            navigationToken = cancellationToken;
            completion = new TaskCompletionSource<PageContent>(TaskCreationOptions.RunContinuationsAsynchronously);
            navigationCompletion = completion;
            RequestReloadLocked();
        }

        return completion.Task;
    }

    /// <summary>切换日期入口（故事 50）：立即取代在途读取；返回已合并的全部读取完成后的任务。</summary>
    public Task SelectDateAsync(string? date)
    {
        Task inFlight;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            state = state with { SelectedDate = date };
            RequestReloadLocked();
            inFlight = this.inFlight;
        }

        return inFlight;
    }

    /// <summary>刷新（故事 50）：重新枚举日期并重读当前选择；重复刷新内容稳定。</summary>
    public Task RefreshAsync()
    {
        Task inFlight;
        lock (gate)
        {
            if (disposed) return Task.CompletedTask;
            RequestReloadLocked();
            inFlight = this.inFlight;
        }

        return inFlight;
    }

    /// <summary>切换筛选：纯投影，不重读文件；读取进行中只记录，待读取完成后生效。</summary>
    public void SetFilter(LogsFilter filter)
    {
        lock (gate)
        {
            if (disposed) return;
            state = state with { Filter = filter };
            if (state.Loading) return;
        }

        Apply(LogsModel.From(Snapshot()));
    }

    /// <summary>选择列表行以查看详情（故事 46）：纯投影，不重读文件。</summary>
    public void SelectRow(int index)
    {
        lock (gate)
        {
            if (disposed) return;
            state = state with { SelectedIndex = index };
            if (state.Loading) return;
        }

        Apply(LogsModel.From(Snapshot()));
    }

    public LogsContent Current
    {
        get { lock (gate) return LogsModel.From(state); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            reloadRequested = false;
            readCancellation?.Cancel();
            navigationCompletion?.TrySetCanceled();
        }
    }

    /// <summary>打开既有日志目录（故事 48）：访问界面列表之外的本地记录；打不开时静默（与既有托盘一致）。</summary>
    public static void OpenLogDirectory()
    {
        try
        {
            if (Directory.Exists(Storage.Logs))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Storage.Logs) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打开目录失败不打断界面；目录入口本身仍然可用（可手动导航到存储位置）。
        }
    }

    // 以下为合并读取循环（与既有日志中心的「一个在途读取 + 合并最新请求」同构）。
    // 调用方必须已持有 gate；启动新循环前先取消上一读取，使旧结果永远追不上新请求。
    private void RequestReloadLocked()
    {
        generation++;
        reloadRequested = true;
        readCancellation?.Cancel();
        if (reading) return;
        reading = true;
        inFlight = RunLoopAsync();
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                long mine;
                CancellationTokenSource cancellation;
                lock (gate)
                {
                    if (disposed || !reloadRequested) return;
                    reloadRequested = false;
                    mine = generation;
                    // 读取同时受导航取消（页面切走）与新请求取消（最新选择获胜）约束。
                    cancellation = navigationToken is { } navigation
                        ? CancellationTokenSource.CreateLinkedTokenSource(navigation)
                        : new CancellationTokenSource();
                    readCancellation = cancellation;
                }

                LogsContent? content = null;
                var superseded = false;
                try
                {
                    content = await ReadOnceAsync(mine, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 无条件捕获（含销毁后的取消）：在途任务不允许带着未观察异常收尾。
                    superseded = true;
                }
                catch (Exception failure)
                {
                    // 受控降级：只披露异常类型（安全边界），页面保持可用（故事 49、51）。
                    lock (gate)
                    {
                        state = state with { Entries = [], Skipped = 0, Complete = true, FailureType = failure.GetType().Name, Loading = false };
                        content = LogsModel.From(state);
                    }
                }

                if (superseded)
                {
                    lock (gate)
                    {
                        if (!reloadRequested) navigationCompletion?.TrySetCanceled();
                    }
                }
                else if (content is not null)
                {
                    var applies = false;
                    lock (gate) applies = !disposed && mine == generation;
                    if (applies) Apply(content);
                }
            }
        }
        finally
        {
            lock (gate)
            {
                reading = false;
                readCancellation = null;
                if (reloadRequested && !disposed)
                {
                    reading = true;
                    inFlight = RunLoopAsync();
                }
            }
        }
    }

    /// <summary>单次读取：枚举日期、保持/默认选择、投影「正在读取」占位，然后受限读取并投影结果。</summary>
    private async Task<LogsContent?> ReadOnceAsync(long mine, CancellationToken token)
    {
        var directory = resolveDirectory();
        var dates = LogsModel.DateEntries(directory);
        string path;
        lock (gate)
        {
            // 刷新与切换都保持仍在的选择；选择缺失（新目录/文件消失）默认最新一天（故事 44）。
            var selected = state.SelectedDate is { } current && dates.Contains(current) ? current : dates.FirstOrDefault();
            state = state with
            {
                Dates = dates,
                SelectedDate = selected,
                Entries = [],
                Skipped = 0,
                Complete = true,
                FailureType = null,
                Loading = selected is not null
            };
            if (selected is null) return LogsModel.From(state);
            path = Path.Combine(directory, selected);   // 锁内定稿，避免与并发切换竞窗
        }

        Apply(LogsModel.From(Snapshot()), mine, completesNavigation: false);
        var result = await Task.Run(() => read(path, token), token).ConfigureAwait(false);
        lock (gate)
        {
            if (disposed || mine != generation) return null;   // 旧读取：结果一律丢弃（故事 50）
            state = state with
            {
                Entries = result.Records,
                Skipped = result.Skipped,
                Complete = result.Complete,
                FailureType = null,
                Loading = false,
                SelectedIndex = result.Records.Count > 0 ? 0 : -1
            };
        }

        return LogsModel.From(Snapshot());
    }

    private LogsState Snapshot()
    {
        lock (gate) return state;
    }

    private void Apply(LogsContent content, long? mine = null, bool completesNavigation = true)
    {
        TaskCompletionSource<PageContent>? completion;
        lock (gate)
        {
            if (disposed || (mine.HasValue && mine.Value != generation)) return;
            // 「正在读取」占位不得完成导航接缝：导航加载必须以最终内容（或受控降级）收尾。
            completion = completesNavigation ? navigationCompletion : null;
        }

        Updated?.Invoke(content);
        completion?.TrySetResult(content);
    }
}
