using System.Text.Json;
using System.Text.Json.Nodes;
using AutoNet.Core;
using AutoNet.Service;
using AutoNet.Tray;
using static TrayRegression;

internal static class UiFreezeRegression
{
    private const string Notice = "物理出口、UU 会话与真实认证仍待现场验收；此处记录不等于已通过验证。";

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("UI-01.2 SPEC-01：服务输出的在线暂停与历史绑定组合均保留联网事实", () => RunStaAsync(() =>
        {
            using var files = new Fixture();
            using var form = new MainForm(_ => throw new Exception("夹具禁止执行恢复命令")) { AllowExit = true };
            ShowForQa(form);
            foreach (var enabled in new[] { true, false })
                foreach (var blocked in new[] { false, true })
                {
                    var snapshot = ServiceStatusAsync(enabled, blocked, true).GetAwaiter().GetResult();
                    Require(snapshot.State == (enabled ? "校园网在线" : "已暂停恢复"), "夹具必须来自服务实际输出");
                    files.WriteStatus(snapshot);
                    form.RefreshStatus();
                    var display = NetworkDisplayMap.Map(snapshot, TimeSpan.Zero);
                    var expected = NetworkAdditional.EgressUnverified
                        | (enabled ? NetworkAdditional.None : NetworkAdditional.RecoveryPaused)
                        | (blocked ? NetworkAdditional.AuthenticationSuspended : NetworkAdditional.None);
                    Require(display.Primary == NetworkPrimaryState.Online && display.IsHealthy,
                        $"Enabled={enabled}, Blocked={blocked} 不应覆盖在线事实，实际 {display.Primary}");
                    Require(display.Additional == expected, "附加事实必须独立保留");
                    Require(Field<Label>(form, "stateTitle").Text == "网络在线");
                    var chips = Field<Panel>(form, "additionalStrip").Controls.Cast<Control>()
                        .SelectMany(c => c.Controls.Cast<Label>()).Select(c => c.Text).ToArray();
                    Require(chips.Contains("认证提交已暂停") == blocked, "主窗口必须显示认证安全阻断附加状态");
                    Require(chips.Contains("自动恢复已暂停") == !enabled);
                    Capture(form, $"ui012-online-enabled-{enabled}-blocked-{blocked}.png");
                }
        })),
        ("UI-01.2 SPEC-01：离线绑定与缺失过期未知无效快照不显示健康", () => RunStaAsync(() =>
        {
            using var files = new Fixture();
            using var form = new MainForm(_ => throw new Exception("夹具禁止执行恢复命令")) { AllowExit = true };
            var offline = ServiceStatusAsync(true, true, false).GetAwaiter().GetResult();
            files.WriteStatus(offline);
            form.RefreshStatus();
            var display = NetworkDisplayMap.Map(offline, TimeSpan.Zero);
            Require(display.Primary == NetworkPrimaryState.BindingRejected && !display.IsHealthy);
            Require(display.Additional == NetworkAdditional.AuthenticationSuspended);
            Require(Field<Label>(form, "stateTitle").Text == "VLAN 绑定被拒绝");
            Capture(form, "ui012-offline-binding.png");
            var online = ServiceStatusAsync(false, true, true).GetAwaiter().GetResult();
            foreach (var (name, snapshot, age, title) in new (string, StatusSnapshot?, TimeSpan?, string)[]
            {
                ("missing", null, null, "后台服务未就绪"),
                ("stale", online with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, TimeSpan.FromMinutes(1), "后台状态已过期"),
                ("future", online with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1) }, TimeSpan.FromMinutes(-1), "后台状态已过期"),
                ("unknown", online with { State = "未来服务状态" }, TimeSpan.Zero, "未识别的服务状态"),
                ("invalid", online with { Tunnel = null! }, TimeSpan.Zero, "状态不可确认")
            })
            {
                if (snapshot is null) File.Delete(files.StatusPath);
                else files.WriteStatus(snapshot);
                form.RefreshStatus();
                display = NetworkDisplayMap.Map(snapshot, age);
                Require(display.Primary == NetworkPrimaryState.Unrecognized && !display.IsHealthy, name + " 不得呈现健康");
                var expectedFlags = name is "stale" or "future" or "unknown"
                    ? NetworkAdditional.AuthenticationSuspended | NetworkAdditional.RecoveryPaused | NetworkAdditional.EgressUnverified
                    : NetworkAdditional.None;
                Require(display.Additional == expectedFlags, name + " 附加事实不符合降级契约");
                Require(Field<Label>(form, "stateTitle").Text == title, name + " 主窗口标题未降级");
            }
        })),
        ("UI-01.2 SPEC-02：日志必需字段和默认非法时间在读取边界被拒绝", () =>
        {
            foreach (var field in new[] { "detail", "action", "time" })
                foreach (var kind in new[] { "missing", "null", "empty", "whitespace", "wrong-type" })
                {
                    // An explicitly empty description is valid and renders safely; action/time are required values.
                    if (field == "detail" && kind is "empty" or "whitespace") continue;
                    var node = JsonNode.Parse(Serialize(Good("正常事件")))!.AsObject();
                    if (kind == "missing") node.Remove(field);
                    else node[field] = kind switch { "null" => null, "empty" => JsonValue.Create(""),
                        "whitespace" => JsonValue.Create("  "), _ => JsonValue.Create(123) };
                    Require(LogTailReader.Parse([node.ToJsonString()]).Count == 0, field + "/" + kind + " 不应进入渲染");
                }
            foreach (var time in new[] { "0001-01-01T00:00:00+00:00", "not-a-date", "2026-99-99T00:00:00Z" })
            {
                var node = JsonNode.Parse(Serialize(Good("正常事件")))!;
                node["time"] = time;
                Require(LogTailReader.Parse([node.ToJsonString()]).Count == 0, "无效时间不应进入渲染");
            }
            foreach (var schema in new[] { 1, 2 })
                Require(LogTailReader.Parse([Serialize(Good("空说明") with { SchemaVersion = schema, Detail = "" })]).Count == 1);
            const string complete = "\"TIME\":\"2026-10-11T00:00:00Z\",\"Action\":\"正常\",\"det\\u0061il\":\"\"";
            Require(LogTailReader.Parse(["{" + complete + "}"]).Count == 1, "大小写和转义属性名必须兼容");
            Require(LogTailReader.Parse(["{" + complete + ",\"detail\":null}"]).Count == 0, "重复必需字段必须拒绝");
            Require(LogTailReader.Parse(["{\"nested\":{" + complete + "}}"]).Count == 0, "嵌套字段不能替代根层必需字段");
            return Task.CompletedTask;
        }),
        ("UI-01.2 SPEC-02：新版嵌套诊断关键空值被逐条拒绝", () =>
        {
            foreach (var diagnostics in new[]
            {
                "{\"error\":{\"type\":\"IOException\",\"callSites\":null}}",
                "{\"error\":{\"type\":null,\"callSites\":[]}}",
                "{\"error\":{\"type\":\"IOException\",\"callSites\":[null]}}",
                "{\"probes\":[null]}",
                "{\"probes\":[{\"target\":null,\"failure\":\"None\"}]}",
                "{\"probes\":[{\"target\":\"BaiduRobots\",\"failure\":null}]}",
                "{\"probes\":[{\"target\":\"BaiduRobots\",\"failure\":\"None\",\"error\":{\"type\":\"IOException\",\"callSites\":null}}]}",
                "{\"authentication\":{\"error\":{\"type\":\"IOException\",\"callSites\":null}}}"
            })
            {
                var node = JsonNode.Parse(Serialize(Good("坏诊断")))!;
                node["diagnostics"] = JsonNode.Parse(diagnostics);
                Require(LogTailReader.Parse([node.ToJsonString()]).Count == 0, "不应接受坏嵌套诊断：" + diagnostics);
            }
            var good = Good("完整诊断") with { Diagnostics = new()
            {
                Error = new("IOException", 0, null, null, null, []),
                Probes = [new("BaiduRobots", false, "Timeout", 2)],
                Authentication = new(LoginResult.Rejected, PortalStage.Status, PortalFailure.Timeout)
            } };
            Require(LogTailReader.Parse([Serialize(good), Serialize(Good("无可选诊断"))]).Count == 2);
            return Task.CompletedTask;
        }),
        ("UI-01.2 SPEC-02：正常损坏正常混排只跳过坏记录且首末选择可刷新", () => RunStaAsync(() =>
        {
            using var files = new Fixture();
            var path = Path.Combine(Path.GetDirectoryName(files.StatusPath)!, "autonet-2026-10-11.jsonl");
            var bad = JsonNode.Parse(Serialize(Good("损坏事件")))!;
            bad["detail"] = null;
            File.WriteAllText(path, Serialize(Good("较早正常")) + "\n" + bad.ToJsonString() + "\n" + Serialize(Good("最新正常")) + "\n");
            Require(LogTailReader.Read(path).Select(e => e.Action).SequenceEqual(["最新正常", "较早正常"]), "坏记录拖垮了同批读取");
            using var form = new LogForm();
            ShowForQa(form);
            PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
            Require(Field<List<ActivityLog>>(form, "loaded").Count == 2);
            var list = Field<ListView>(form, "list");
            foreach (var index in new[] { 0, 1 })
            {
                Select(form, list, index);
                Require(Field<TextBox>(form, "detail").Text.Contains(index == 0 ? "最新正常" : "较早正常"));
                Capture(form, $"ui012-mixed-log-selected-{index}.png");
            }
            Method(form, "LoadEntries").Invoke(form, null);
            PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
            Require(Field<List<ActivityLog>>(form, "loaded").Count == 2 && list.Items.Count == 2);
        })),
        ("UI-01.2 SPEC-02：坏详情在首次后续选择和刷新期间均安全降级", () => RunStaAsync(() =>
        {
            using var files = new Fixture();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            using var form = new LogForm((_, token) =>
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    entered.Set();
                    WaitHandle.WaitAny([release.WaitHandle, token.WaitHandle], TimeSpan.FromSeconds(10));
                    token.ThrowIfCancellationRequested();
                }
                return [Good("正常事件")];
            });
            ShowForQa(form);
            PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
            var list = Field<ListView>(form, "list");
            var records = Field<List<ActivityLog>>(form, "visible");
            var threadErrors = new List<Exception>();
            ThreadExceptionEventHandler handler = (_, e) => threadErrors.Add(e.Exception);
            Application.ThreadException += handler;
            try
            {
                foreach (var bad in new[]
                {
                    Good("坏说明") with { Detail = null! },
                    Good("坏诊断") with { Diagnostics = new() { Error = new("IOException", 0, null, null, null, null!) } }
                })
                {
                    // Simulate a damaged entry already in memory, bypassing the reader to exercise the UI guard itself.
                    records[0] = bad;
                    Select(form, list, 0);
                    Require(Field<TextBox>(form, "detail").Text.Contains("该条日志详情不可读取"));
                    Require(!Field<TextBox>(form, "detail").Text.Contains("fixture-secret"));
                }
                records[0] = Good("恢复正常");
                records.Add(Good("后续坏记录") with { Detail = null! });
                list.Items.Add(new ListViewItem("00:00:01") { SubItems = { "后续坏记录", "虚构损坏记录" } });
                Select(form, list, 1);
                Require(Field<TextBox>(form, "detail").Text.Contains("该条日志详情不可读取"));
                Capture(form, "ui012-bad-log-selection.png");
                Select(form, list, 0);
                Require(Field<TextBox>(form, "detail").Text.Contains("恢复正常"));
                Method(form, "LoadEntries").Invoke(form, null);
                PumpUntil(() => entered.IsSet);
                Method(form, "ShowDetail").Invoke(form, null);
                Require(Field<TextBox>(form, "detail").Text.Contains("没有符合筛选条件"));
            }
            finally { release.Set(); Application.ThreadException -= handler; }
            PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
            Require(!Field<Task>(form, "pending").IsFaulted && threadErrors.Count == 0, "详情异常逃逸至 UI 消息循环");
            Require(Field<TextBox>(form, "detail").Text.Contains("正常事件"), "刷新后未恢复显示");
        })),
        ("UI-01.2 SPEC-P2：新旧日志及详情降级共用固定验收声明", () => RunStaAsync(() =>
        {
            using var files = new Fixture();
            foreach (var schema in new[] { 1, 2 })
            {
                using var form = new LogForm((_, _) => [Good("正常事件") with { SchemaVersion = schema }]);
                ShowForQa(form);
                PumpUntil(() => Field<Task>(form, "pending").IsCompleted);
                Require(Field<TextBox>(form, "detail").Text.EndsWith(Notice), "正常详情缺少固定声明");
                Field<List<ActivityLog>>(form, "visible")[0] = Good("坏事件") with { Detail = null! };
                Method(form, "ShowDetail").Invoke(form, null);
                Require(Field<TextBox>(form, "detail").Text.EndsWith(Notice), "降级详情缺少固定声明");
            }
        }))
    ];

    private static async Task<StatusSnapshot> ServiceStatusAsync(bool enabled, bool blocked, bool online)
    {
        var fixture = new MonitorFixture { Blocked = blocked, BlockFailure = PortalFailure.BindingRejected };
        fixture.Settings = fixture.Settings with { Enabled = enabled };
        fixture.Http.Online = online;
        using var worker = new MonitorWorker(fixture, fixture.Clock);
        var status = await worker.RunOnceAsync(default);
        Require(fixture.Connects == 0 && fixture.Disconnects == 0 && !fixture.Http.Requests.Any(u => u.AbsolutePath == "/eportal/portal/login"));
        return status with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static ActivityLog Good(string action) => new()
    {
        SchemaVersion = 2, EventCode = "StatusChanged", Time = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero),
        Action = action, Detail = "虚构事件；不代表真实恢复或认证验收。"
    };

    private static string Serialize(ActivityLog entry) => JsonSerializer.Serialize(entry, ActivityLog.Json);

    private static void Select(LogForm form, ListView list, int index)
    {
        foreach (ListViewItem item in list.Items) item.Selected = false;
        list.Items[index].Selected = true;
        Method(form, "ShowDetail").Invoke(form, null);
        Application.DoEvents();
    }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }
}
