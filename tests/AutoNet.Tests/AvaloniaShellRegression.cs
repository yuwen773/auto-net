using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 应用壳回归（迁移工单 #3）：导航模型、页面代次守卫（T02/故事 16）、状态管道的
/// 完整性/时间有效性/安全降级矩阵（T02/故事 15）与托盘入口语义（含 #4 恢复开关条目）。
/// 受控 UI 证据由 scripts/qa-avalonia-shell.ps1（shell 模式截图）提供；
/// 概览页内容与动作见 OverviewRegression / scripts/qa-avalonia-overview.ps1。
/// </summary>
internal static class AvaloniaShellRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("壳回归：左侧导航固定四页且顺序与术语一致", () => Sync(() =>
        {
            Require(ShellPages.All.Select(info => info.Page).SequenceEqual(
                [ShellPage.Overview, ShellPage.Diagnostics, ShellPage.Logs, ShellPage.Settings]), "导航顺序变化");
            Require(ShellPages.All.Select(info => info.Name).SequenceEqual(
                ["概览", "网络诊断", "日志", "设置"]), "导航名称变化");
            Require(ShellPages.All.All(info => info.Description.Length > 0), "页面说明缺失");
            Require(Enum.GetValues<ShellPage>().Length == ShellPages.All.Count, "存在未纳入导航的页面");
            Require(ShellPages.Default == ShellPage.Overview, "默认页必须为概览");
            foreach (var page in Enum.GetValues<ShellPage>())
                Require(ShellPages.Info(page).Page == page, "页面目录映射不一致：" + page);
        })),
        ("壳回归：快速切换页面时旧读取不覆写当前页", async () =>
        {
            using var coordinator = new PageLoadCoordinator();
            var applied = new List<string>();
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var slow = coordinator.NavigateAsync("概览",
                (page, _) => release.Task,
                (page, content) => applied.Add(page + ":" + content));
            await coordinator.NavigateAsync("日志",
                (page, _) => Task.FromResult("新内容"),
                (page, content) => applied.Add(page + ":" + content));
            Require(applied.SequenceEqual(["日志:新内容"]), "最新页面的内容未先应用");
            release.SetResult("旧内容");
            await slow;
            Require(applied.SequenceEqual(["日志:新内容"]), "旧读取覆写了当前页面");
        }),
        ("壳回归：被取代的读取收到取消，销毁后结果一律丢弃", async () =>
        {
            using var cancellationObserved = new ManualResetEventSlim();
            using var coordinator = new PageLoadCoordinator();
            var applied = new List<string>();
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = coordinator.NavigateAsync("旧页", async (page, token) =>
            {
                using var registration = token.Register(() => cancellationObserved.Set());
                return await release.Task;
            }, (page, content) => applied.Add(page + ":" + content));
            await coordinator.NavigateAsync("新页",
                (page, _) => Task.FromResult("新内容"),
                (page, content) => applied.Add(page + ":" + content));
            Require(cancellationObserved.IsSet, "被取代的读取未收到取消");
            Require(applied.SequenceEqual(["新页:新内容"]));
            release.SetResult("旧内容");
            await first;
            Require(applied.SequenceEqual(["新页:新内容"]), "已取消的读取仍被应用");

            var disposeRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var third = coordinator.NavigateAsync("销毁前", (page, _) => disposeRelease.Task,
                (page, content) => applied.Add(page + ":" + content));
            coordinator.Dispose();
            disposeRelease.SetResult("销毁后内容");
            await third;
            Require(applied.SequenceEqual(["新页:新内容"]), "销毁后的结果仍被应用");
            await coordinator.NavigateAsync("销毁后", (page, _) => Task.FromResult("任何内容"),
                (page, content) => applied.Add(page + ":" + content));
            Require(applied.SequenceEqual(["新页:新内容"]), "销毁后的导航仍产生应用");
        }),
        ("壳回归：页面读取失败走降级回调且不逃逸", async () =>
        {
            using var coordinator = new PageLoadCoordinator();
            var applied = new List<string>();
            var failures = new List<string>();
            await coordinator.NavigateAsync("当前",
                (page, _) => Task.FromException<string>(new IOException("夹具读取错误")),
                (page, content) => applied.Add(content),
                (page, failure) => failures.Add(failure.GetType().Name));
            Require(failures.SequenceEqual(["IOException"]) && applied.Count == 0, "当前代次失败未走降级回调");

            // 被取代的失败不再回调、不再应用，也不向调用方抛出。
            var failRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<string> StaleLoad(string page, CancellationToken token)
            {
                await failRelease.Task;
                throw new IOException("旧失败");
            }

            var stale = coordinator.NavigateAsync("旧", StaleLoad,
                (page, content) => applied.Add(content),
                (page, failure) => failures.Add("旧:" + failure.GetType().Name));
            await coordinator.NavigateAsync("新", (page, _) => Task.FromResult("新内容"), (page, content) => applied.Add(content));
            failRelease.SetResult();
            await stale;
            Require(failures.Count == 1 && applied.SequenceEqual(["新内容"]), "被取代的失败不应回调或应用");

            // 已取消的标记在读取前就被检查：内容加载必须尊重取消。
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var observed = false;
            try { await ShellContent.LoadAsync(ShellPage.Logs, cancelled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed, "已取消的读取仍在继续");
        }),
        ("壳回归：状态管道按完整性、时间有效性与安全降级规则映射", () => Sync(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-shell-pipe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
                string Stamp(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss");

                var missing = StatusPipe.ReadNow(path, now);
                Require(missing.Snapshot is null && missing.StripText == "后台服务未就绪", "缺失文件未按未就绪降级");
                Require(missing.Display.Primary == NetworkPrimaryState.Unrecognized && !missing.Display.IsHealthy, "缺失文件不得呈现健康");

                File.WriteAllText(path, JsonSerializer.Serialize(Online() with { UpdatedAt = now }, Storage.Json));
                var fresh = StatusPipe.ReadNow(path, now);
                Require(fresh.Valid && fresh.Fresh && fresh.Display.Primary == NetworkPrimaryState.Online, "有效状态未按在线映射");
                Require(fresh.StripText == "后台服务运行中 · 最近检测 " + Stamp(now), "在线状态栏文本不符");

                var stale = StatusPipe.ReadNow(WriteStatus(directory, Online() with { UpdatedAt = now.AddMinutes(-1) }), now);
                Require(!stale.Fresh && !stale.Display.IsHealthy && stale.Display.Title == "后台状态已过期", "过期状态未安全降级");
                Require(stale.StripText == "后台状态已过期 · 最后更新 " + Stamp(now.AddMinutes(-1)), "过期状态栏文本不符");

                var future = StatusPipe.ReadNow(WriteStatus(directory, Online() with { UpdatedAt = now.AddSeconds(30) }), now);
                Require(!future.Fresh && !future.Display.IsHealthy, "未来异常时间不得呈现健康");
                Require(future.StripText.StartsWith("后台状态已过期 · 最后更新", StringComparison.Ordinal), "未来异常未按过期降级");

                var skew = StatusPipe.ReadNow(WriteStatus(directory, Online() with { UpdatedAt = now.AddSeconds(3) }), now);
                Require(skew.Fresh && skew.Display.IsHealthy, "5 秒内的时钟偏差应被容忍");

                File.WriteAllText(path, "{not json");
                var corrupt = StatusPipe.ReadNow(path, now);
                Require(corrupt.Snapshot is null && corrupt.StripText == "后台服务未就绪", "损坏文件未按未就绪降级");

                var invalid = StatusPipe.ReadNow(WriteStatus(directory, Online() with { Tunnel = null! }), now);
                Require(!invalid.Valid && invalid.StripText == "后台状态不可确认", "字段损坏未按不可确认降级");
                Require(invalid.Display.Primary == NetworkPrimaryState.Unrecognized && invalid.Display.Title == "状态不可确认", "字段损坏不得呈现健康");

                var unknown = StatusPipe.ReadNow(WriteStatus(directory, Online() with { State = "未来服务状态", UpdatedAt = now }), now);
                Require(unknown.Valid && !unknown.Display.IsHealthy, "未识别状态不得呈现健康");
                Require(unknown.Display.Primary == NetworkPrimaryState.Unrecognized && unknown.Display.Title == "未识别的服务状态", "未识别状态未按不可确认映射");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("壳回归：状态管道读取失败不中断且销毁后不再通知", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-shell-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "status.json");
            var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(60));
            var seen = new List<StatusReading>();
            var gate = new object();
            void Note(StatusReading reading) { lock (gate) seen.Add(reading); }
            pipe.Updated += Note;
            bool Has(Func<StatusReading, bool> predicate)
            {
                lock (gate) return seen.Any(predicate);
            }

            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                pipe.Start();
                Require(WaitUntil(() => Has(reading => reading.Fresh)), "未产生首次有效读取");

                File.WriteAllText(path, "{bad json");
                Require(WaitUntil(() => Has(reading => reading.Snapshot is null && !reading.Fresh)), "读取失败后未按未就绪降级");

                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                Require(WaitUntil(() => Has(reading => reading.Fresh)), "读取失败后轮询未自行恢复");

                pipe.Dispose();
                int count;
                lock (gate) count = seen.Count;
                Require(pipe.Current is not null, "销毁前应当至少应用过一次状态");
                await Task.Delay(300);
                lock (gate) Require(seen.Count == count, "销毁后仍在产生通知");

                pipe.Start();
                await Task.Delay(150);
                lock (gate) Require(seen.Count == count, "销毁后 Start 仍在工作");

                var disposed = false;
                try { await pipe.RefreshAsync(); }
                catch (ObjectDisposedException) { disposed = true; }
                Require(disposed, "销毁后即时刷新应当直接拒绝");
            }
            finally
            {
                pipe.Dispose();
                Directory.Delete(directory, recursive: true);
            }
        }),
        ("壳回归：导航选择立即生效且内容经代次守卫应用", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-shell-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(120));
                ShellPage? pendingLengthPage = null;
                var hold = new TaskCompletionSource<PageContent>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var controller = new ShellController(pipe, (page, token) =>
                    page == pendingLengthPage ? hold.Task : ShellContent.LoadAsync(page, token));

                var applied = new List<ShellPage>();
                var selected = new List<ShellPage>();
                controller.SelectionChanged += page => selected.Add(page);
                controller.ContentChanged += content => applied.Add(content.Page);

                await controller.SelectAsync(ShellPage.Logs);
                Require(applied.SequenceEqual([ShellPage.Logs]) && controller.Content!.Page == ShellPage.Logs, "页面内容未应用");

                // 诊断页（#6）由状态投影同步应用，不经异步加载接缝：即使接缝被占用，内容也立即生效。
                pendingLengthPage = ShellPage.Diagnostics;
                await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Diagnostics);
                Require(controller.SelectedPage == ShellPage.Diagnostics && selected[^1] == ShellPage.Diagnostics, "选择未立即生效");
                Require(controller.Content is DiagnosticsContent { Trusted: true }, "诊断页内容未按状态同步应用");
                pendingLengthPage = null;

                pendingLengthPage = ShellPage.Settings;
                var slow = controller.SelectAsync(ShellPage.Settings);
                Require(controller.SelectedPage == ShellPage.Settings && selected[^1] == ShellPage.Settings, "选择未立即生效");
                pendingLengthPage = null;
                await controller.SelectAsync(ShellPage.Logs);
                Require(applied.SequenceEqual([ShellPage.Logs, ShellPage.Diagnostics, ShellPage.Logs]), "新页面内容未应用");
                hold.SetResult(new PageScaffold(ShellPage.Settings, "设置", "旧内容", "旧内容"));
                await slow;
                Require(applied.SequenceEqual([ShellPage.Logs, ShellPage.Diagnostics, ShellPage.Logs]), "旧页面内容覆写了当前页");
                Require(controller.Content!.Page == ShellPage.Logs, "当前内容与选择不一致");

                StatusReading? latest = null;
                controller.StatusChanged += reading => latest = reading;
                await controller.RefreshStatusAsync();
                Require(latest is { Fresh: true } && controller.Status!.StripText.StartsWith("后台服务运行中", StringComparison.Ordinal),
                    "状态未流经控制器");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("壳回归：托盘入口保持既有语义，恢复开关随状态同步", () => Sync(() =>
        {
            // 解除认证暂停（#5）与查看日志（#7）随后续切片接入；本票按既有完整菜单补入自动恢复开关。
            var labels = TrayMenuModel.Entries.Select(entry => entry.Label).ToArray();
            // #5 起托盘恢复既有“解除认证暂停…”条目：与概览解除入口同源，仅在存在待解除阻断时可用。
            Require(labels.SequenceEqual(["打开 AutoNet", "设置认证信息…", "自动恢复状态待确认", "解除认证暂停…", "", "退出托盘（后台继续运行）"]), "托盘入口文案变化");
            Require(TrayMenuModel.Entries[0].Action == TrayMenuAction.Open, "打开入口动作变化");
            Require(TrayMenuModel.Entries[1].Action == TrayMenuAction.Settings, "设置入口动作变化");
            Require(TrayMenuModel.Entries[2].Action == TrayMenuAction.RecoverySwitch, "恢复开关入口动作变化");
            Require(TrayMenuModel.Entries[2].Label == OverviewModel.RecoverySwitch(null).Label, "托盘开关占位文案应与概览开关同源");
            Require(TrayMenuModel.Entries[3].Action == TrayMenuAction.UnblockAuthentication, "解除入口动作变化");
            Require(TrayMenuModel.Entries[3].Label == OverviewModel.PlaceholderLabel, "托盘解除占位文案应与概览同源");
            Require(ReferenceEquals(TrayMenuModel.Entries[4], TrayMenuEntry.Separator), "分隔线必须使用 Separator");
            Require(TrayMenuModel.Entries[5].Action == TrayMenuAction.Exit, "退出入口动作变化");
            Require(ElevatedOperationKind.Settings.CommandLineArgument() == "--settings", "提权入口参数与既有契约不符");
        }))
    ];

    private static StatusSnapshot Online() => new()
    {
        Enabled = true,
        State = "校园网在线",
        Direct = "外网可达",
        Wifi = "已连接",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private static string WriteStatus(string directory, StatusSnapshot snapshot)
    {
        var path = Path.Combine(directory, "status.json");
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));
        return path;
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(15);
        }

        return condition();
    }

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
