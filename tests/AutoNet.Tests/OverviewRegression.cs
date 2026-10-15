using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 概览页回归（迁移工单 #4）：状态/进度/下一步投影、在线与认证暂停分列、缺失/过期/损坏/不可信
/// 状态不可确认与动作禁用、恢复开关的明确意图与动作前重验、术语与语义色一致性。
/// 受控 UI 证据由 scripts/qa-avalonia-overview.ps1（overview 模式）提供。
/// </summary>
internal static class OverviewRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("概览回归：同时回答当前状态、应用进度与下一步操作", () => Sync(() =>
        {
            var content = OverviewModel.From(Reading(Online(), TimeSpan.Zero));
            Require(content.Page == ShellPage.Overview && content.Title == "概览");
            Require(content.Trusted && content.Primary == NetworkPrimaryState.Online && content.Tone == OverviewTone.Good);
            Require(content.StatusTitle == "网络在线");
            Require(content.StatusExplanation == "校园网 HTTPS 内容检查已通过。");
            Require(content.NextStep == "无需操作，保持当前连接即可。");

            Require(content.Progress.Select(row => row.Caption).SequenceEqual(["主动断线额度", "下次允许重试", "最近一次恢复"]),
                "进度必须覆盖断线额度、重试时间与最近恢复");
            Require(content.Progress[0].Value == "本次故障主动断线额度：0/3 次");
            Require(content.Progress[1].Value == "当前无需重试");
            Require(content.Progress[1].Note == "额度在连续在线 60 秒后重置；每次断线请求至少间隔 120 秒。");
            Require(content.Progress[2].Value == "尚无恢复记录。");

            Require(content.Recovery == new RecoverySwitchState("已开启", "关闭自动恢复", true, RecoveryIntent.Disable));
            Require(!content.Unblock.Visible && !content.HasTechnical);

            // 重试与恢复记录按快照投影格式化（时间使用本地时区，格式与既有界面一致）。
            var next = DateTimeOffset.Now.AddSeconds(90);
            var recovered = DateTimeOffset.Now.AddMinutes(-5);
            var recorded = OverviewModel.From(Reading(Online() with
            {
                State = "认证状态未知", Direct = "外网不可达或未知",
                NextAttempt = next, LastRecoveryAt = recovered, LastRecoverySeconds = 12.5
            }, TimeSpan.Zero));
            Require(recorded.Progress[1].Value == next.ToLocalTime().ToString("HH:mm:ss") + "（等待期间继续检测）");
            Require(recorded.Progress[2].Value == recovered.ToLocalTime().ToString("MM-dd HH:mm:ss") + " · 观测耗时 12.5 秒");
            Require(recorded.Progress[2].Note.Contains("无法区分"), "恢复记录必须保留观测口径声明");
        })),
        ("概览回归：在线与认证暂停并存分列且不改写联网事实", () => Sync(() =>
        {
            var online = Online() with
            {
                CredentialsBlocked = true,
                AuthenticationFailure = "CredentialsRejected",
                AuthenticationPersistentState = "CredentialsRejected"
            };
            var content = OverviewModel.From(Reading(online, TimeSpan.Zero));
            Require(content.Primary == NetworkPrimaryState.Online && content.Tone == OverviewTone.Good,
                "认证暂停不得把在线改写成断网");
            Require(content.StatusTitle == "网络在线");
            Require(content.Auth.Visible && content.Auth.Kind == OverviewAuthKind.Suspended
                && content.Auth.Title == "认证提交已暂停" && content.Auth.Tone == OverviewTone.Problem,
                "在线与认证暂停必须能并存分列");
            Require(content.Auth.Note.Length > 0);
            Require(content.Unblock.Visible && content.Unblock.Enabled && content.Unblock.Label == "解除认证暂停…");

            // 离线且绑定被拒绝：绑定拒绝是故障主状态，认证暂停仍独立成列。
            var binding = OverviewModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知",
                CredentialsBlocked = true, AuthenticationFailure = "BindingRejected"
            }, TimeSpan.Zero));
            Require(binding.Primary == NetworkPrimaryState.BindingRejected && binding.Tone == OverviewTone.Problem);
            Require(binding.StatusTitle == "VLAN 绑定被拒绝");
            Require(binding.Auth.Visible && binding.Auth.Kind == OverviewAuthKind.Suspended && binding.Auth.Title == "认证提交已暂停");
            Require(binding.Unblock.Visible && binding.Unblock.Label == "查看认证诊断并解除…");

            // 门户结构疑似变化不等于认证暂停：单独成列、不提供解除入口。
            var layout = OverviewModel.From(Reading(Online() with { PortalLayoutSuspected = true }, TimeSpan.Zero));
            Require(layout.Primary == NetworkPrimaryState.Online, "结构信号不得改写联网事实");
            Require(layout.Auth.Visible && layout.Auth.Kind == OverviewAuthKind.LayoutSuspected
                && layout.Auth.Title == "门户结构疑似变化" && layout.Auth.Tone == OverviewTone.Warning);
            Require(!layout.Unblock.Visible, "结构疑似变化不得提供解除入口");
        })),
        ("概览回归：缺失、过期、损坏与不可信状态不可确认且不沿用上次画面", () => Sync(() =>
        {
            var cases = new (string Name, StatusReading Reading, string Title)[]
            {
                ("缺失", Reading(null), "后台服务未就绪"),
                ("过期", Reading(Online() with { DisconnectAttempts = 2 }, TimeSpan.FromMinutes(1)), "后台状态已过期"),
                ("未来异常", Reading(Online(), TimeSpan.FromSeconds(-30)), "后台状态已过期"),
                ("损坏", Reading(Online() with { Tunnel = null! }, TimeSpan.Zero), "状态不可确认")
            };
            foreach (var (name, reading, title) in cases)
            {
                var content = OverviewModel.From(reading);
                Require(!content.Trusted, name + "：不应判定为可信");
                Require(content.Primary == NetworkPrimaryState.Unrecognized && content.StatusTitle == title,
                    name + "：标题未按规则降级（" + content.StatusTitle + "）");
                Require(content.StatusTitle != "网络在线", name + "：沿用了上次在线画面");
                Require(content.Chips.Count == 0, name + "：沿用了上次附加事实");
                Require(content.Auth.Visible && content.Auth.Kind == OverviewAuthKind.Unconfirmable
                    && content.Auth.Title == "认证状态不可确认", name + "：认证列未按不可确认降级");
                Require(content.Auth.Title != "认证提交已暂停" && content.Auth.Note.Contains("暂不可用"),
                    name + "：认证列沿用了上次画面");
                Require(content.Recovery == new RecoverySwitchState("待确认", "自动恢复状态待确认", false, null),
                    name + "：恢复开关未禁用");
                Require(content.Unblock.Visible && !content.Unblock.Enabled, name + "：解除入口应为可见但禁用");
                Require(content.Progress.Count == 1 && content.Progress[0].Value == title + "，暂不显示进度信息。",
                    name + "：进度未降级");
                Require(content.Progress.All(row => !row.Value.Contains("2/3")), name + "：进度泄露了上次画面");
                Require(content.NextStep.Length > 0, name + "：必须仍给出下一步");
            }

            // 未识别状态与四种不可信不同：状态文件本身可信，但界面不得把它当作正常。
            var unknownState = OverviewModel.From(Reading(Online() with { State = "未来服务状态" }, TimeSpan.Zero));
            Require(unknownState.Trusted && unknownState.Primary == NetworkPrimaryState.Unrecognized
                && unknownState.StatusTitle == "未识别的服务状态", "未识别状态不得呈现为正常");
            Require(unknownState.Recovery == new RecoverySwitchState("已开启", "关闭自动恢复", true, RecoveryIntent.Disable),
                "可信边界内开关仍按 Enabled 事实给出明确意图");
        })),
        ("概览回归：恢复开关文案与意图跟随可信状态且不使用切换语义", () => Sync(() =>
        {
            Require(OverviewModel.RecoverySwitch(Reading(Online(), TimeSpan.Zero))
                == new RecoverySwitchState("已开启", "关闭自动恢复", true, RecoveryIntent.Disable));
            Require(OverviewModel.RecoverySwitch(Reading(Online() with { Enabled = false }, TimeSpan.Zero))
                == new RecoverySwitchState("已关闭", "开启自动恢复", true, RecoveryIntent.Enable));
            Require(OverviewModel.RecoverySwitch(Reading(Online(), TimeSpan.FromMinutes(1)))
                == new RecoverySwitchState("待确认", "自动恢复状态待确认", false, null));
            Require(OverviewModel.RecoverySwitch(null)
                == new RecoverySwitchState("待确认", "自动恢复状态待确认", false, null));

            // 意图逐字对应既有命令契约（--enable-recovery / --disable-recovery），不使用 toggle。
            Require(OverviewModel.OperationFor(RecoveryIntent.Enable).CommandLineArgument() == "--enable-recovery");
            Require(OverviewModel.OperationFor(RecoveryIntent.Disable).CommandLineArgument() == "--disable-recovery");

            // 恢复暂停时的下一步必须指向同名开关（动作靠近对应状态，术语一致）。
            var paused = OverviewModel.From(Reading(Online() with
            {
                State = "认证状态未知", Direct = "外网不可达或未知", Enabled = false
            }, TimeSpan.Zero));
            Require(paused.Primary == NetworkPrimaryState.RecoveryPaused);
            Require(paused.NextStep.Contains("开启自动恢复"), "恢复暂停的下一步应指向恢复开关：" + paused.NextStep);
            Require(paused.Recovery.Intent == RecoveryIntent.Enable);
        })),
        ("概览回归：附加事实各自独立呈现且不与主状态混叠", () => Sync(() =>
        {
            var online = OverviewModel.From(Reading(Online(), TimeSpan.Zero));
            Require(online.Chips.Select(chip => chip.Text).SequenceEqual(["物理出口未核验"]),
                "在线必须保留出口未核验提示");
            Require(online.Chips[0].Tone == OverviewTone.Neutral);

            var offline = OverviewModel.From(Reading(Online() with
            {
                State = "认证状态未知", Direct = "外网不可达或未知",
                DisconnectAttempts = 3, DisconnectBudgetMax = 3,
                DisconnectRequestUnconfirmed = true, Tunnel = "本机代理不可达"
            }, TimeSpan.Zero));
            Require(offline.Primary == NetworkPrimaryState.DisconnectQuotaExhausted, "额度用尽应成为主状态");
            Require(offline.Chips.Select(chip => chip.Text).SequenceEqual(["断线完成状态未确认", "代理 / 系统路径异常"]),
                "额度用尽已在主状态呈现时不得重复为标签");
            Require(offline.Chips[0].Tone == OverviewTone.Problem && offline.Chips[1].Tone == OverviewTone.Warning);

            // 自动恢复关闭由进度行与开关呈现，不重复为标签。
            var paused = OverviewModel.From(Reading(Online() with { Enabled = false }, TimeSpan.Zero));
            Require(!paused.Chips.Any(chip => chip.Text.Contains("自动恢复")) && paused.Recovery.State == "已关闭");
        })),
        ("概览回归：语义色与状态图标覆盖全部主状态", () => Sync(() =>
        {
            foreach (var state in Enum.GetValues<NetworkPrimaryState>())
                Require(ShellIcons.StatusGlyph(state).Length > 0, "缺少状态图标：" + state);

            Require(OverviewModel.ToneFor(NetworkPrimaryState.Online) == OverviewTone.Good);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.AuthenticationUnknown) == OverviewTone.Attention);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.NeedsAuthentication) == OverviewTone.Problem);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.BindingRejected) == OverviewTone.Problem);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.AuthenticatedOffline) == OverviewTone.Warning);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.DisconnectQuotaExhausted) == OverviewTone.Warning);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.RecoveryPaused) == OverviewTone.Neutral);
            Require(OverviewModel.ToneFor(NetworkPrimaryState.Unrecognized) == OverviewTone.Neutral);
        })),
        ("概览回归：技术详情按需展开且不含凭据", () => Sync(() =>
        {
            Require(!OverviewModel.From(Reading(Online(), TimeSpan.Zero)).HasTechnical, "在线默认不应有技术详情");
            var content = OverviewModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知",
                LastError = "网关登录失败：https://login.csust.edu.cn:802/eportal/portal/login?user_account=fixture&user_password=fixture-secret",
                AuthenticationStage = "Submission", AuthenticationFailure = "BindingRejected",
                AuthenticationHttpStatus = 401, CredentialRequestStarted = true,
                AuthenticationPersistentState = "BindingRejected"
            }, TimeSpan.Zero));
            Require(content.HasTechnical);
            Require(content.TechnicalDetail.Contains("认证阶段 Submission") && content.TechnicalDetail.Contains("已发起凭据请求"));
            foreach (var forbidden in new[] { "fixture-secret", "user_password", "user_account", "login.csust.edu.cn", "http" })
                Require(!content.TechnicalDetail.Contains(forbidden), "技术详情泄露了 " + forbidden);
            Require(!content.StatusExplanation.Contains("fixture-secret") && !content.NextStep.Contains("fixture-secret"));
        })),
        ("概览动作：恢复开关先重验可信状态再发送明确意图", async () =>
        {
            var launched = new List<ElevatedOperationKind>();
            var reread = Reading(Online(), TimeSpan.Zero);
            var actions = new OverviewActions(_ => Task.FromResult(reread), launched.Add);

            Require(await actions.RequestRecoveryChangeAsync(RecoveryIntent.Disable), "可信状态应发起关闭意图");
            Require(launched.SequenceEqual([ElevatedOperationKind.DisableRecovery]));
            Require(launched[^1].CommandLineArgument() == "--disable-recovery");

            reread = Reading(Online() with { Enabled = false }, TimeSpan.Zero);
            Require(await actions.RequestRecoveryChangeAsync(RecoveryIntent.Enable));
            Require(launched[^1] == ElevatedOperationKind.EnableRecovery && launched[^1].CommandLineArgument() == "--enable-recovery");

            // 连续两次相同点击绝不解释成相反操作（无 toggle 语义）。
            reread = Reading(Online(), TimeSpan.Zero);
            Require(await actions.RequestRecoveryChangeAsync(RecoveryIntent.Disable));
            Require(await actions.RequestRecoveryChangeAsync(RecoveryIntent.Disable));
            Require(launched.TakeLast(2).SequenceEqual([ElevatedOperationKind.DisableRecovery, ElevatedOperationKind.DisableRecovery]),
                "重复点击被解释成相反操作");

            // 动作前重验：过期、缺失或读取异常一律不发起（故事 41）。
            var count = launched.Count;
            reread = Reading(Online(), TimeSpan.FromMinutes(1));
            Require(!await actions.RequestRecoveryChangeAsync(RecoveryIntent.Disable), "过期状态不得发起操作");
            reread = Reading(null);
            Require(!await actions.RequestRecoveryChangeAsync(RecoveryIntent.Disable), "缺失状态不得发起操作");
            Require(launched.Count == count, "重验失败仍发起了操作");

            var failed = new OverviewActions(_ => Task.FromException<StatusReading>(new IOException("夹具读取失败")), launched.Add);
            Require(!await failed.RequestRecoveryChangeAsync(RecoveryIntent.Disable));
            var disposed = new OverviewActions(_ => Task.FromException<StatusReading>(new ObjectDisposedException("StatusPipe")), launched.Add);
            Require(!await disposed.RequestRecoveryChangeAsync(RecoveryIntent.Disable));
            Require(launched.Count == count, "读取异常仍发起了操作");
        }),
        ("概览动作：解除入口仅在可信且存在暂停时发起", async () =>
        {
            var launched = new List<ElevatedOperationKind>();
            StatusReading reading = Reading(Online() with { CredentialsBlocked = true, AuthenticationFailure = "CredentialsRejected" }, TimeSpan.Zero);
            var actions = new OverviewActions(_ => Task.FromResult(reading), launched.Add);

            Require(await actions.RequestUnblockAsync());
            Require(launched.SequenceEqual([ElevatedOperationKind.UnblockAuthentication]));
            Require(launched[^1].CommandLineArgument() == "--unblock-auth");

            reading = Reading(Online(), TimeSpan.Zero);
            Require(!await actions.RequestUnblockAsync(), "没有暂停时不得发起解除");
            reading = Reading(Online() with { CredentialsBlocked = true }, TimeSpan.FromMinutes(1));
            Require(!await actions.RequestUnblockAsync(), "状态不可信时不得发起解除");
            Require(launched.Count == 1, "不应产生额外发起");

            var failed = new OverviewActions(_ => Task.FromException<StatusReading>(new IOException("夹具读取失败")), launched.Add);
            Require(!await failed.RequestUnblockAsync());
            Require(launched.Count == 1);
        }),
        ("概览回归：提权恢复操作按明确目标改写设置且缺设置时不写入", () => Sync(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-overview-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "settings.json");
                Func<Settings?> load = () => File.Exists(path)
                    ? JsonSerializer.Deserialize<Settings>(Storage.ReadShared(path), Storage.Json) : null;
                Action<Settings> save = settings => Storage.AtomicWrite(path, JsonSerializer.Serialize(settings, Storage.Json));

                Storage.AtomicWrite(path, JsonSerializer.Serialize(
                    new Settings { InterfaceId = Guid.NewGuid().ToString(), WifiProfile = "test", Enabled = true }, Storage.Json));
                ElevatedRecovery.Apply(false, load, save);
                Require(load() is { Enabled: false }, "关闭目标未写入设置");
                ElevatedRecovery.Apply(true, load, save);
                Require(load() is { Enabled: true }, "开启目标未写入设置");

                // 缺少设置时保持既有受控失败：不创建文件、不写入（与旧托盘的“请先保存校园网设置”一致）。
                File.Delete(path);
                var failed = false;
                try { ElevatedRecovery.Apply(false, load, save); }
                catch (InvalidOperationException) { failed = true; }
                Require(failed, "缺设置时应受控失败");
                Require(!File.Exists(path), "缺设置时不得写入");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })),
        ("概览回归：控制器随状态更新且离开概览后不被改写", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-overview-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                var pipe = new StatusPipe(path);
                using var controller = new ShellController(pipe);
                var applied = new List<PageContent>();
                controller.ContentChanged += content => applied.Add(content);

                await controller.SelectAsync(ShellPage.Overview);
                Require(controller.Content is OverviewContent { Trusted: false, StatusTitle: "后台服务未就绪" },
                    "首次进入概览应按未就绪降级");

                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                await controller.RefreshStatusAsync();
                Require(controller.Content is OverviewContent { Trusted: true, Primary: NetworkPrimaryState.Online, StatusTitle: "网络在线" },
                    "概览未随状态更新");
                Require(applied.Count(content => content is OverviewContent { Trusted: true }) == 1,
                    "概览更新应按内容事件发布且不重复");

                await controller.SelectAsync(ShellPage.Logs);
                File.WriteAllText(path, JsonSerializer.Serialize(Online() with { Enabled = false }, Storage.Json));
                await controller.RefreshStatusAsync();
                Require(controller.Content is PageScaffold { Page: ShellPage.Logs }, "离开概览后仍被状态更新改写");

                await controller.SelectAsync(ShellPage.Overview);
                Require(controller.Content is OverviewContent { Trusted: true } back
                    && back.Recovery.Intent == RecoveryIntent.Enable, "返回概览未按最新状态重建");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("概览回归：轮询刷新与导航并行不冻结且结果按序应用", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-overview-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(50));
                using var controller = new ShellController(pipe);
                await controller.SelectAsync(ShellPage.Overview);
                pipe.Start();

                var started = Environment.TickCount64;
                for (var index = 0; index < 12; index++)
                {
                    var snapshot = index % 2 == 0 ? Online() : Online() with { Enabled = false };
                    Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));
                    await controller.RefreshStatusAsync();
                    await controller.SelectAsync(index % 3 == 0 ? ShellPage.Logs : ShellPage.Overview);
                }

                Require(Environment.TickCount64 - started < 10_000, "轮询与导航未在预算内完成（疑似冻结）");

                var refreshes = Task.WhenAll(
                    controller.RefreshStatusAsync(), controller.RefreshStatusAsync(), controller.RefreshStatusAsync());
                Require(await Task.WhenAny(refreshes, Task.Delay(10_000)) == refreshes, "并发刷新未在预算内完成");
                Require(controller.Status is { Fresh: true }, "状态未按最新读取应用");
                Require(controller.Content is OverviewContent, "最后一页应为概览内容");
            }
            finally { Directory.Delete(directory, recursive: true); }
        })
    ];

    private static StatusReading Reading(StatusSnapshot? snapshot, TimeSpan? age = null)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveAge = snapshot is null ? (TimeSpan?)null : age ?? TimeSpan.Zero;
        var valid = StatusSnapshotReader.IsValid(snapshot);
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, effectiveAge), valid,
            valid && NetworkDisplayMap.IsFresh(effectiveAge), effectiveAge, now);
    }

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验"
    };

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }
}
