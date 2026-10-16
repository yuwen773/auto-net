using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 日志页回归（迁移工单 #7 / T09 / 故事 44–51）：日期入口默认最新、受限列表显式标注范围、
/// 详情关联原因动作结果、复制保持脱敏、损坏记录跳过并说明缺口、快速切换最新获胜、
/// 重复刷新稳定、新旧日志兼容与读取失败降级。
/// 受控 UI 证据由 scripts/qa-avalonia-logs.ps1（logs 模式）提供。
/// </summary>
internal static class LogsPageRegression
{
    private static readonly DateTimeOffset Base = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    private const string Newest = "autonet-2026-10-16.jsonl";
    private const string Older = "autonet-2026-10-15.jsonl";

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("日志页回归：日期入口按降序列出单日文件且默认选中最新一天", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, "autonet-2026-10-14.jsonl"), [Line(Entry(0))]);
                File.WriteAllLines(Path.Combine(directory, "autonet-2026-10-16.jsonl"), [Line(Entry(1))]);
                File.WriteAllLines(Path.Combine(directory, "autonet-2026-10-15.jsonl"), [Line(Entry(2))]);
                File.WriteAllLines(Path.Combine(directory, "readme.txt"), ["不是日志"]);
                File.WriteAllLines(Path.Combine(directory, "autonet-2026-13-99.jsonl"), ["不是合法日期"]);

                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.Dates.SequenceEqual([Newest, Older, "autonet-2026-10-14.jsonl"]),
                    "日期入口必须只含合法单日文件且最新在前：" + string.Join(",", content.Dates));
                Require(content.SelectedDate == Newest, "必须默认选中最新一天（故事 44）");
                Require(content.Rows.Count == 1 && content.Rows[0].Preview == Entry(1).Detail, "列表必须来自所选日期");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：受限列表最新在前并显式标注读取范围", async () =>
        {
            var directory = NewDirectory();
            try
            {
                var entries = Enumerable.Range(0, 305).Select(i => Entry(i)).ToArray();
                File.WriteAllLines(Path.Combine(directory, Newest), entries.Select(e => Line(e)));

                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.Rows.Count == LogTailReader.DefaultMaxRecords,
                    $"列表必须受单日上限约束，实际 {content.Rows.Count}，Scope={content.ScopeNote}，Date={content.SelectedDate}，Gap={content.GapNote}，Failure={content.FailureNote}");
                Require(content.Rows[0].Time == entries[^1].Time.ToLocalTime().ToString("HH:mm:ss"), "列表必须最新在前");
                Require(content.ScopeNote.Contains($"单日最多 {LogTailReader.DefaultMaxRecords} 条"), "范围标注必须写明单日上限");
                Require(content.ScopeNote.Contains("更早的记录请通过「日志目录」查看"), "触达上限时必须显式声明更早记录不在列表内（故事 45）");

                var small = NewDirectory();
                try
                {
                    File.WriteAllLines(Path.Combine(small, Newest), Enumerable.Range(0, 5).Select(i => Entry(i)).Select(Line));
                    var complete = (LogsContent)await new LogsPage(() => small).LoadAsync(CancellationToken.None);
                    Require(complete.ScopeNote.Contains("已包含当日全部记录"), "未触达上限时必须声明覆盖全部记录");
                }
                finally { Directory.Delete(small, recursive: true); }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：详情关联原因、动作与结果且旧记录按兼容文案渲染", async () =>
        {
            var directory = NewDirectory();
            try
            {
                var modern = Entry(0);
                var legacy = new ActivityLog
                {
                    Time = Base.AddMinutes(1), Action = "认证结果", Detail = "旧版记录说明。",
                    Wifi = "已连接", Direct = "外网不可达或未知", Portal = "需认证",
                    Attempts = 2, DisconnectAttempts = 1,
                    AuthenticationStage = nameof(PortalStage.Submission),
                    AuthenticationFailure = nameof(PortalFailure.BindingRejected)
                };
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(modern)]);
                File.WriteAllLines(Path.Combine(directory, Older), [Line(legacy)]);

                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.Rows[0].Detail.Contains("决策原因：InsufficientFailureSamples"), "新版详情必须关联决策原因（故事 46）");
                Require(content.Rows[0].Detail.Contains("执行阶段：MainCycle"), "新版详情必须关联执行阶段");
                Require(content.Rows[0].Detail.Contains("结果：Skipped"), "新版详情必须关联后续结果");
                Require(content.Rows[0].Detail.EndsWith(LogsModel.ValidationNotice, StringComparison.Ordinal), "详情必须以验证脚注收尾");

                await page.SelectDateAsync(Older);
                var old = page.Current;
                Require(old.Rows[0].Detail.Contains("时间："), "旧版详情必须按兼容文案渲染（故事 51）");
                Require(old.Rows[0].Detail.Contains("认证阶段：Submission") && old.Rows[0].Detail.Contains("失败分类：BindingRejected"),
                    "旧版详情必须保留受控阶段与失败分类");
                Require(old.Rows[0].Detail.Contains("旧版记录说明。"), "旧版详情必须保留服务撰写说明");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：复制保持脱敏摘要且不携带自由文本", async () =>
        {
            var directory = NewDirectory();
            try
            {
                var hostile = new ActivityLog
                {
                    Time = Base, Action = "fixture-secret-action", SchemaVersion = 1,
                    Detail = "网关登录失败 https://login.example.com?user_account=fixture&user_password=fixture-secret",
                    LastError = "fixture-secret-error", Wifi = "fixture-secret-wifi", Portal = "fixture-secret-portal"
                };
                var modern = Entry(0, detail: "受控说明。");
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(hostile), Line(modern)]);

                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.CopyText.StartsWith("AutoNet 日志脱敏摘要", StringComparison.Ordinal), "复制文本必须是脱敏摘要（故事 47）");
                Require(!content.CopyText.Contains("fixture-secret") && !content.CopyText.Contains("user_password") && !content.CopyText.Contains("http"),
                    "复制文本绝不允许携带自由文本或 URL");
                Require(content.CopyText.Contains("其他事件"), "未知动作必须折叠为受控占位");
                Require(content.CopyText.Contains("事件 RecoveryDecision") && content.CopyText.Contains("决策 InsufficientFailureSamples"),
                    "受控字段必须按白名单输出");
                Require(content.CopyText.Contains(LogSummary.Notice(2, 2)), "复制文本必须附条数说明");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：损坏记录跳过并说明缺口且页面不失效", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest),
                [
                    Line(Entry(0)),
                    "{not json",
                    "{\"time\":\"2026-10-16T08:01:00Z\"}",
                    Line(Entry(2)),
                    "{\"time\":\"2026-10-16T08:03:00Z\",\"action\":\"服务启动\",\"detail\":null}"
                ]);

                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.Rows.Count == 2, "损坏行不得进入列表，也不得拖垮整页（故事 49）");
                Require(content.GapNote.Contains("已跳过 3 条无法读取的记录"), "必须显式说明缺口：" + content.GapNote);
                Require(content.Rows.All(row => row.Action.Length > 0), "剩余行必须可正常渲染");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：快速切换时最新选择获胜且旧结果不覆写", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(Entry(0, action: "最新一天"))]);
                File.WriteAllLines(Path.Combine(directory, Older), [Line(Entry(1, action: "较早一天"))]);

                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var page = new LogsPage(() => directory, (path, token) =>
                {
                    if (path.Contains(Newest, StringComparison.Ordinal))
                    {
                        started.TrySetResult();
                        try { release.Task.Wait(token); }
                        catch (OperationCanceledException) { throw; }
                        return new LogTailRead([Entry(0, action: "慢读结果")], 0, true);
                    }

                    return new LogTailRead([Entry(1, action: "较快结果")], 0, true);
                });

                var applied = new List<string>();
                page.Updated += content => applied.Add(content.SelectedDate + ":" + content.Rows.Count);
                var first = page.SelectDateAsync(Newest);        // 慢读进入在途
                await started.Task;
                var second = page.SelectDateAsync(Older);        // 新选择立即取代旧读取（故事 50）
                await second;
                var settled = page.Current;
                Require(settled.SelectedDate == Older && settled.Rows.Count == 1 && settled.Rows[0].Action == "较快结果",
                    "必须展示最新选择对应的读取结果");
                Require(applied.All(item => !item.StartsWith(Newest + ":1", StringComparison.Ordinal)),
                    "被取代的读取结果不得应用：" + string.Join(",", applied));

                release.TrySetResult();
                await first;
                Require(page.Current.SelectedDate == Older && page.Current.Rows[0].Action == "较快结果",
                    "旧读取完成后仍不得覆写新选择");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：重复刷新稳定且刷新保留当前日期选择", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(Entry(0))]);
                File.WriteAllLines(Path.Combine(directory, Older), [Line(Entry(1)), Line(Entry(2))]);

                var page = new LogsPage(() => directory);
                var first = (LogsContent)await page.LoadAsync(CancellationToken.None);
                await page.RefreshAsync();
                var second = page.Current;
                var third = page.Current;
                Require(first.ContentKey == second.ContentKey && second.ContentKey == third.ContentKey,
                    "同一选择的重复刷新必须产生一致内容（故事 50）");

                await page.SelectDateAsync(Older);
                await page.RefreshAsync();
                var refreshed = page.Current;
                Require(refreshed.SelectedDate == Older && refreshed.Rows.Count == 2, "刷新必须保留仍在的日期选择");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：筛选收窄列表且复制只含可见行", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest),
                [
                    Line(Entry(0)),                                                              // Recovery
                    Line(Entry(1, eventCode: "StatusChanged", action: "服务启动")),               // State
                    Line(Entry(2, eventCode: "PortalStep", action: "认证步骤"))                   // Authentication
                ]);

                var page = new LogsPage(() => directory);
                await page.LoadAsync(CancellationToken.None);
                page.SetFilter(LogsFilter.Authentication);
                var filtered = page.Current;
                Require(filtered.Filter == LogsFilter.Authentication && filtered.Rows.Count == 1, "认证筛选必须只保留认证事件");
                Require(filtered.CopyText.Contains("事件 PortalStep") && !filtered.CopyText.Contains("事件 StatusChanged"),
                    "复制必须只含可见行");
                Require(filtered.ScopeNote.StartsWith("显示 1 条 / 已读取 3 条", StringComparison.Ordinal), "范围标注必须同时给出可见与已读计数");
                page.SetFilter(LogsFilter.All);
                Require(page.Current.Rows.Count == 3, "切回全部必须恢复完整可见列表");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：选择列表行驱动详情且越界选择收敛", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(Entry(0)), Line(Entry(1)), Line(Entry(2))]);

                var page = new LogsPage(() => directory);
                await page.LoadAsync(CancellationToken.None);
                Require(page.Current.SelectedIndex == 0, "加载后必须默认选中最新一行（与既有日志中心一致）");
                page.SelectRow(2);
                Require(page.Current.SelectedIndex == 2 && page.Current.DetailText == page.Current.Rows[2].Detail,
                    "选中行必须驱动详情（故事 46）");
                page.SelectRow(99);
                Require(page.Current.SelectedIndex == 2, "越界选中必须收敛到列表范围");
                page.SelectRow(-3);
                Require(page.Current.SelectedIndex == 0, "负数选中必须回到默认最新行");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：读取失败按固定文案降级、只披露异常类型并可恢复", async () =>
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(Entry(0))]);
                Func<string, CancellationToken, LogTailRead> current = (_, _) => throw new IOException("夹具机密消息不得回显");
                var page = new LogsPage(() => directory, (path, token) => current(path, token));

                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.FailureNote == "读取失败：IOException", "失败说明必须只含异常类型名（安全边界）");
                Require(content.DetailText.StartsWith(LogsModel.ReadFailureNote, StringComparison.Ordinal), "详情必须按固定文案降级（故事 51）");
                Require(content.Rows.Count == 0 && content.CopyText.Length == 0, "失败时列表与复制必须清空");
                var rendered = content.DetailText + content.FailureNote + content.ScopeNote + content.GapNote;
                Require(!rendered.Contains("夹具机密"), "任何可见文本都不得回显异常消息");

                current = (_, _) => new LogTailRead([Entry(0)], 0, true);
                await page.RefreshAsync();
                var recovered = page.Current;
                Require(recovered.FailureNote.Length == 0 && recovered.Rows.Count == 1, "失败后刷新必须恢复（页面不失效）");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：目录为空或缺失时显示固定空态", async () =>
        {
            var directory = NewDirectory();
            try
            {
                var page = new LogsPage(() => directory);
                var content = (LogsContent)await page.LoadAsync(CancellationToken.None);
                Require(content.Dates.Count == 0 && content.SelectedDate is null, "空目录不得虚构日期入口");
                Require(content.ScopeNote == LogsModel.EmptyDirectoryNote && content.DetailText == LogsModel.EmptyDirectoryNote,
                    "空目录必须显示固定空态");
                Require(content.CopyText.Length == 0 && content.Rows.Count == 0, "空目录不得提供复制或列表");

                var missing = new LogsPage(() => Path.Combine(directory, "不存在"));
                var absent = (LogsContent)await missing.LoadAsync(CancellationToken.None);
                Require(absent.ScopeNote == LogsModel.EmptyDirectoryNote, "目录缺失按空目录处理");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("日志页回归：页内更新与导航接缝汇聚到壳守卫", async () =>
        {
            var directory = NewDirectory();
            var statusDirectory = NewDirectory();
            try
            {
                File.WriteAllLines(Path.Combine(directory, Newest), [Line(Entry(0))]);
                File.WriteAllText(Path.Combine(statusDirectory, "status.json"), JsonSerializer.Serialize(new StatusSnapshot
                {
                    Enabled = true, State = "校园网在线", Direct = "外网可达", Wifi = "已连接",
                    Portal = "未查询（外网检查已通过）", Tunnel = "系统路径可达，出口未核验", Uu = "未核验"
                }, Storage.Json));

                // 页面不在日志页时，页内更新必须被壳守卫丢弃。
                using var pipe = new StatusPipe(Path.Combine(statusDirectory, "status.json"), TimeSpan.FromMinutes(5));
                using var controller = new ShellController(pipe);
                var applied = new List<PageContent>();
                controller.ContentChanged += content => applied.Add(content);
                await controller.SelectAsync(ShellPage.Settings);
                var before = controller.Content;
                controller.ApplyExternal(new LogsContent());
                Require(ReferenceEquals(controller.Content, before), "非当前页的页内更新不得覆写当前页");
                await controller.SelectAsync(ShellPage.Logs);
                controller.ApplyExternal(new LogsContent());
                Require(controller.Content is LogsContent, "当前页的页内更新必须应用");

                // 生产接线形状：导航接缝 + 页内更新汇聚后，日志页呈现受限列表。
                var logsPage = new LogsPage(() => directory);
                using var shell = new ShellController(pipe, (page, token) =>
                    page == ShellPage.Logs ? logsPage.LoadAsync(token) : ShellContent.LoadAsync(page, token));
                logsPage.Updated += content => shell.ApplyExternal(content);
                await shell.SelectAsync(ShellPage.Logs);
                Require(shell.Content is LogsContent { Rows.Count: 1 }, "导航接缝必须加载日志页内容");
                await logsPage.SelectDateAsync(Newest);
                Require(shell.Content is LogsContent loaded && loaded.Rows.Count == 1 && loaded.SelectedDate == Newest,
                    "页内切换必须经壳守卫呈现最新内容");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
                Directory.Delete(statusDirectory, recursive: true);
            }
        })
    ];

    private static ActivityLog Entry(int seq, string action = "恢复决策", string detail = "受控说明。", string? eventCode = "RecoveryDecision") => new()
    {
        SchemaVersion = 2,
        EventCode = eventCode,
        Action = action,
        Detail = detail,
        Time = Base.AddMinutes(seq),
        Diagnostics = eventCode is null ? null : new DiagnosticData
        {
            Reason = "InsufficientFailureSamples",
            Stage = "MainCycle",
            Outcome = "Skipped"
        }
    };

    private static string Line(ActivityLog entry) => JsonSerializer.Serialize(entry, ActivityLog.Json);

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "autonet-logs-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
