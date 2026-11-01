using AutoNet.Core;
using AutoNet.Service;
using AutoNet.Tray.Avalonia;

/// <summary>
/// #35 额度状态可见化回归：状态快照的受控投影、四种额度状态的界面区分、旧版快照缺字段降级、
/// 未知码不渲染原始文本，以及耗尽通知进入受控词表且只提醒一次。不访问真实门户或校园网。
/// </summary>
internal static class SubmissionVisibilityRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("额度可见：切换其他 Wi-Fi 后仍显示已读取的额度事实", async () =>
        {
            foreach (var count in new[] { 1, 3 })
            {
                await WithStore(async (fixture, store) =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        var reservation = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), fixture.Clock.GetUtcNow());
                        Require(reservation.Budget is not null, "准备额度预留失败");
                        Require(store.TryTransitionSubmissionBudget(reservation.Budget!, SubmissionBudgetState.PossiblySent), "准备未知提交失败");
                        fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                    }
                    store.BlockAuthentication(new AuthenticationBlock { State = AuthenticationState.SubmissionPending, Attempts = count, Revision = Guid.NewGuid() });
                    fixture.Link = fixture.Link with { OtherProfile = true };
                    using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                    var status = await worker.RunOnceAsync(default);
                    var expected = count == 3 ? SubmissionBudgetCodes.Exhausted : SubmissionBudgetCodes.Available;
                    Require(status.SubmissionBudgetCode == expected && status.SubmissionAttempts == count &&
                        status.SubmissionAttemptMax == 3 && status.SubmissionBudgetExhausted == (count == 3),
                        "切换其他 Wi-Fi 后丢失额度事实");
                    Require(status.SubmissionRetryCode == "" && status.SubmissionNextPossibleAt is null,
                        "其他 Wi-Fi 不得推断重试授权或时刻");
                    var content = OverviewModel.From(Reading(status));
                    Require(content.Submission.Value.Contains($"{count}/3"), "概览未展示已用次数");
                    Require(!content.Submission.Value.Contains("额度与间隔条件满足") &&
                        !content.Submission.Value.Contains("等待重试"),
                        "其他 Wi-Fi 上不能给出提交授权或等待暗示：" + content.Submission.Value);
                    Require(content.Submission.Note.Contains("其他 Wi-Fi") && !content.Submission.Note.Contains("继续低频只读复检"),
                        "其他 Wi-Fi 上不能声称正在继续校园网只读复检");
                });
            }
            await WithStore(async (fixture, store) =>
            {
                store.BlockAuthentication(new AuthenticationBlock { State = AuthenticationState.SubmissionPending, Attempts = 1, Revision = Guid.NewGuid() });
                fixture.Link = fixture.Link with { OtherProfile = true };
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                var content = OverviewModel.From(Reading(status));
                Require(status.SubmissionBudgetCode == SubmissionBudgetCodes.Unknown && status.SubmissionAttempts is null,
                    "旧版待决缺少额度时不得猜测为未使用");
                Require(content.Submission.Note.Contains("其他 Wi-Fi") && !content.Submission.Note.Contains("只读复检"),
                    "其他 Wi-Fi 上的未知额度不能声称正在只读复检");
            });
        }),
        ("额度可见：额度间隔满足不表示认证已获授权", () =>
        {
            var snapshot = Budget(SubmissionBudgetCodes.Unused, 0, false, SubmissionRetryDecision.Authorized) with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.CredentialsRejected),
                Enabled = false
            };
            var content = OverviewModel.From(Reading(snapshot));
            Require(content.Submission.Value.Contains("额度与间隔条件满足"), "未限定额度与间隔条件");
            Require(content.Auth.Visible && content.Auth.Kind == OverviewAuthKind.Suspended &&
                content.Auth.Note.Contains("凭据"), "额度满足被误认为认证已获授权");
            Require(content.Recovery.State == "已关闭", "自动恢复关闭事实缺失");
            Require(content.Submission.Note.Contains("是否提交仍取决于认证状态"), "额度满足未说明仍需其他条件");
            return Task.CompletedTask;
        }),
        ("额度可见：无额度记录时快照提供未使用而非不可确认", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.Online = true;
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                Require(status.SubmissionBudgetCode == SubmissionBudgetCodes.Unused, "无记录时额度级别不符");
                Require(status.SubmissionAttempts == 0 && status.SubmissionAttemptMax == 3 && status.SubmissionBudgetExhausted == false,
                    "无记录时次数、上限或耗尽事实不符");
                var content = OverviewModel.From(Reading(status));
                Require(content.Submission.Value.Contains("尚未使用") && content.Submission.Value.Contains("0/3"),
                    "无记录时概览未显示未使用：" + content.Submission.Value);
            });
        }),
        ("额度可见：状态快照投影次数、上限、最早下次提交与耗尽", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);

                var first = await worker.RunOnceAsync(default);
                Require(first.SubmissionBudgetCode == SubmissionBudgetCodes.Available, "首次未知提交后额度级别不符");
                Require(first.SubmissionAttempts == 1 && first.SubmissionAttemptMax == 3 && first.SubmissionBudgetExhausted == false,
                    "首次提交次数、上限或耗尽事实不符");
                Require(first.SubmissionRetryCode == nameof(SubmissionRetryDecision.Waiting), "首次未知提交后未进入等待");
                Require(first.SubmissionNextPossibleAt is not null, "等待状态缺少最早下次提交时刻");

                fixture.Http.PortalResult = "-1";
                foreach (var seconds in new[] { 2, 4, 113 })
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                    await worker.RunOnceAsync(default);
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var second = await worker.RunOnceAsync(default);
                Require(second.SubmissionAttempts == 2 && second.SubmissionBudgetCode == SubmissionBudgetCodes.NearLimit,
                    "第二次提交后未显示即将用尽");
                Require(second.SubmissionRetryCode == nameof(SubmissionRetryDecision.Waiting) && second.SubmissionNextPossibleAt is not null,
                    "第二次提交后未显示等待与最早下次提交时刻");

                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                var third = await worker.RunOnceAsync(default);
                Require(third.SubmissionAttempts == 3 && third.SubmissionBudgetCode == SubmissionBudgetCodes.Exhausted,
                    "第三次提交后未显示耗尽");
                Require(third.SubmissionBudgetExhausted == true && third.SubmissionNextPossibleAt is null,
                    "耗尽事实或下次提交时刻不符");
                Require(third.SubmissionRetryCode == nameof(SubmissionRetryDecision.Exhausted), "耗尽后未停止自动提交");
                Require(third.Detail.Contains("已停止自动提交"), "耗尽后未说明停止自动提交");
                Require(third.Notification is { Code: NotificationCodes.SubmissionBudgetExhausted },
                    "耗尽通知缺失：" + third.Notification?.Code);

                // 门户明确成功：复检消解并重置额度；投影必须显示“已重置”，而不是继续沿用耗尽。
                fixture.Http.PortalResult = "1";
                fixture.Clock.Advance(TimeSpan.FromSeconds(60));
                var reset = await worker.RunOnceAsync(default);
                Require(reset.SubmissionAttempts == 0 && reset.SubmissionBudgetCode == SubmissionBudgetCodes.Reset,
                    "门户确认成功后未显示额度已重置");
                Require(reset.SubmissionBudgetExhausted == false && !reset.CredentialsBlocked, "重置后仍显示耗尽或认证暂停");
                // 同轮只读复检事件可能先占用一次性通知槽（托盘不显示复检事件）；重置通知必须补发。
                var resetNotified = reset.Notification is { Code: NotificationCodes.SubmissionBudgetReset };
                for (var i = 0; i < 3 && !resetNotified; i++)
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                    resetNotified = (await worker.RunOnceAsync(default)).Notification is
                        { Code: NotificationCodes.SubmissionBudgetReset };
                }
                Require(resetNotified, "额度已重置通知缺失");
            });
        }),
        ("额度可见：即将用尽通知在额度达到上限前送达且只提醒一次", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                for (var i = 0; i < 2; i++)
                {
                    var reservation = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), fixture.Clock.GetUtcNow());
                    Require(reservation.Budget is not null, "准备额度预留失败");
                    Require(store.TryTransitionSubmissionBudget(reservation.Budget!, SubmissionBudgetState.PossiblySent), "准备未知提交失败");
                    fixture.Clock.Advance(TimeSpan.FromSeconds(SubmissionRetryPolicy.SubmissionIntervalSeconds));
                }
                store.BlockAuthentication(new AuthenticationBlock
                { State = AuthenticationState.SubmissionPending, Attempts = 2, Revision = Guid.NewGuid() });
                // 链路不可用：只观察额度，不触发新的提交。
                fixture.Link = fixture.Link with { Connected = false, Address = null };
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var first = await worker.RunOnceAsync(default);
                Require(first.SubmissionBudgetCode == SubmissionBudgetCodes.NearLimit, "即将用尽额度级别不符");
                Require(first.Notification is { Code: NotificationCodes.SubmissionBudgetNearLimit },
                    "即将用尽通知缺失：" + first.Notification?.Code);
                var at = first.Notification!.At;
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                var second = await worker.RunOnceAsync(default);
                Require(second.Notification is { Code: NotificationCodes.SubmissionBudgetNearLimit } same && same.At == at,
                    "即将用尽通知被重复发布");
            });
        }),
        ("额度可见：第三次被明确拒绝时耗尽不覆盖认证暂停提示", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                await worker.RunOnceAsync(default);
                fixture.Http.PortalResult = "-1";
                foreach (var seconds in new[] { 2, 4, 113 })
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                    await worker.RunOnceAsync(default);
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                await worker.RunOnceAsync(default);
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"密码错误\"}";
                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                var rejected = await worker.RunOnceAsync(default);

                Require(rejected.SubmissionAttempts == 3 && rejected.SubmissionBudgetExhausted == true,
                    "第三次明确拒绝后额度事实不符");
                Require(rejected.CredentialsBlocked && rejected.AuthenticationFailure == "CredentialsRejected",
                    "明确拒绝未进入认证暂停");
                Require(rejected.Notification is not { Code: NotificationCodes.SubmissionBudgetExhausted },
                    "明确拒绝被额度耗尽通知覆盖：" + rejected.Notification?.Code);
                Require(rejected.Detail.Contains("密码错误") || rejected.Detail.Contains("凭据"), "明确拒绝未保留原因");
            });
        }),
        ("额度可见：明确拒绝不被额度耗尽或即将用尽掩盖", async () =>
        {
            // 第三次提交同时耗尽额度并收到密码错误：认证列必须说明明确拒绝，额度耗尽仍独立展示。
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                await worker.RunOnceAsync(default);
                fixture.Http.PortalResult = "-1";
                foreach (var seconds in new[] { 2, 4, 113 })
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                    await worker.RunOnceAsync(default);
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                await worker.RunOnceAsync(default);
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"密码错误\"}";
                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                var rejected = await worker.RunOnceAsync(default);

                Require(rejected.SubmissionBudgetCode == SubmissionBudgetCodes.Exhausted && rejected.SubmissionBudgetExhausted == true,
                    "第三次明确拒绝后额度事实不符");
                Require(rejected.CredentialsBlocked &&
                    rejected.AuthenticationPersistentState == nameof(AuthenticationState.CredentialsRejected),
                    "第三次明确拒绝未进入凭据拒绝暂停");
                var content = OverviewModel.From(Reading(rejected));
                Require(content.Auth.Visible && content.Auth.Kind == OverviewAuthKind.Suspended &&
                    content.Auth.Title == "认证提交已暂停", "明确拒绝时认证列缺失");
                Require(content.Auth.Note.Contains("凭据") && content.Auth.Note.Contains("密码"),
                    "认证列未说明凭据被明确拒绝：" + content.Auth.Note);
                Require(!content.Auth.Note.Contains("额度已耗尽"),
                    "认证列把明确拒绝误报为额度耗尽：" + content.Auth.Note);
                Require(content.Submission.Kind == OverviewSubmissionBudgetKind.Exhausted &&
                    content.Progress[0].Value.Contains("已耗尽"), "额度耗尽事实被明确拒绝掩盖");
                Require(content.Chips.Any(chip => chip.Text == "认证提交额度已耗尽"), "耗尽标签缺失");
                // 明确拒绝后认证状态不再是提交待决，服务不会继续提交结果复检链。
                Require(content.Submission.Note.Contains("额度已用尽") && content.Submission.Note.Contains("明确拒绝") &&
                    content.Submission.Note.Contains("没有进行中的提交结果复检") &&
                    !content.Submission.Note.Contains("继续低频只读复检") && !content.Submission.Note.Contains("继续只读复检"),
                    "明确拒绝后额度行仍虚构复检或额度唯一原因：" + content.Submission.Note);
                Require(!content.Auth.Note.Contains("继续只读复检") && !content.Auth.Note.Contains("继续低频只读复检"),
                    "明确拒绝的认证列仍声称复检：" + content.Auth.Note);
            });

            // 第三次提交被绑定拒绝：额度耗尽与绑定拒绝并存，同样不得声称仍在复检。
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                await worker.RunOnceAsync(default);
                fixture.Http.PortalResult = "-1";
                foreach (var seconds in new[] { 2, 4, 113 })
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                    await worker.RunOnceAsync(default);
                }
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                await worker.RunOnceAsync(default);
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
                fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                var binding = await worker.RunOnceAsync(default);

                Require(binding.SubmissionBudgetExhausted == true && binding.CredentialsBlocked &&
                    binding.AuthenticationPersistentState == nameof(AuthenticationState.BindingRejected),
                    "第三次绑定拒绝后额度或认证事实不符");
                var content = OverviewModel.From(Reading(binding));
                Require(content.Auth.Note.Contains("绑定") && content.Auth.Note.Contains("VLAN"),
                    "认证列未说明绑定拒绝：" + content.Auth.Note);
                Require(content.Submission.Note.Contains("额度已用尽") && content.Submission.Note.Contains("明确拒绝") &&
                    content.Submission.Note.Contains("没有进行中的提交结果复检") &&
                    !content.Submission.Note.Contains("继续低频只读复检"),
                    "第三次绑定拒绝后额度行仍虚构复检：" + content.Submission.Note);
                Require(content.Progress[0].Value.Contains("已耗尽"), "绑定拒绝后额度耗尽事实缺失");
            });

            // 第二次提交被绑定拒绝且额度即将用尽：认证列必须说明绑定拒绝，不能让“即将用尽”掩盖。
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                await worker.RunOnceAsync(default);
                fixture.Http.PortalResult = "-1";
                foreach (var seconds in new[] { 2, 4, 113 })
                {
                    fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
                    await worker.RunOnceAsync(default);
                }
                fixture.Http.LoginResult = "{\"result\":0,\"msg\":\"只能绑定指定的VLAN设备\"}";
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
                var binding = await worker.RunOnceAsync(default);

                Require(binding.SubmissionBudgetCode == SubmissionBudgetCodes.NearLimit && binding.SubmissionBudgetExhausted == false,
                    "第二次绑定拒绝后额度事实不符");
                Require(binding.CredentialsBlocked &&
                    binding.AuthenticationPersistentState == nameof(AuthenticationState.BindingRejected),
                    "绑定拒绝未进入绑定拒绝暂停");
                var content = OverviewModel.From(Reading(binding));
                Require(content.Auth.Visible && content.Auth.Note.Contains("绑定") && content.Auth.Note.Contains("VLAN"),
                    "认证列未说明绑定拒绝：" + content.Auth.Note);
                Require(!content.Auth.Note.Contains("额度即将用尽"),
                    "认证列把绑定拒绝误报为额度即将用尽：" + content.Auth.Note);
                Require(content.Submission.Kind == OverviewSubmissionBudgetKind.NearLimit &&
                    content.Progress[0].Value.Contains("即将用尽"), "额度即将用尽事实被绑定拒绝掩盖");
            });

            // 对照：结果未知且额度耗尽时，认证列仍说明额度是停止原因并提示只读复检。
            // 用“请求已发出但响应丢失”制造非结构性未知结果，避免门户改版信号另行抢占认证列。
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.PortalResult = "0";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store),
                    fixture.Clock, qaSubmissionLost: true);
                await worker.RunOnceAsync(default);
                fixture.Http.PortalResult = "-1";
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                await worker.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromSeconds(4));
                await worker.RunOnceAsync(default);
                fixture.Clock.Advance(TimeSpan.FromHours(1));
                var second = await worker.RunOnceAsync(default);
                Require(second.SubmissionAttempts == 2, "第二次结果未知提交未发生");
                fixture.Clock.Advance(TimeSpan.FromHours(1));
                var unknown = await worker.RunOnceAsync(default);

                Require(unknown.SubmissionBudgetExhausted == true && unknown.SubmissionAttempts == 3 &&
                    unknown.AuthenticationPersistentState == nameof(AuthenticationState.SubmissionPending),
                    "结果未知耗尽场景不符");
                var content = OverviewModel.From(Reading(unknown));
                Require(content.Auth.Visible && content.Auth.Note.Contains("额度已耗尽") &&
                    content.Auth.Note.Contains("只读复检"), "结果未知耗尽时认证列未说明停止原因：" + content.Auth.Note);
                Require(!content.Auth.Note.Contains("凭据") && !content.Auth.Note.Contains("绑定"),
                    "结果未知耗尽被误报为明确拒绝：" + content.Auth.Note);
                Require(content.Submission.Kind == OverviewSubmissionBudgetKind.Exhausted &&
                    content.Submission.Note.Contains("继续低频只读复检"),
                    "结果未知耗尽时额度行丢失复检说明：" + content.Submission.Note);
            });
        }),
        ("额度可见：耗尽文案只在提交待决时声称复检", () =>
        {
            // 旧版或未知认证状态：耗尽事实仍显示，但不得虚构复检或提交授权。
            var unknown = Budget(SubmissionBudgetCodes.Exhausted, 3, true, SubmissionRetryDecision.Exhausted) with
            {
                CredentialsBlocked = false,
                AuthenticationPersistentState = "Unknown",
                AuthenticationFailure = nameof(PortalFailure.None)
            };
            var content = OverviewModel.From(Reading(unknown));
            Require(content.Submission.Kind == OverviewSubmissionBudgetKind.Exhausted &&
                content.Submission.Value.Contains("3/3"), "未知认证状态下耗尽事实缺失");
            Require(content.Submission.Note.Contains("没有可证实的提交结果复检") &&
                !content.Submission.Note.Contains("继续低频只读复检"),
                "未知认证状态被说成仍在复检：" + content.Submission.Note);
            Require(!content.Auth.Visible, "未知认证状态不应显示认证列");

            // 终止性安全阻断不是提交待决：认证列与额度行都不得声称复检。
            var blocked = unknown with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SafetyBlocked),
                AuthenticationFailure = nameof(PortalFailure.LoginRejected)
            };
            var blockedContent = OverviewModel.From(Reading(blocked));
            Require(blockedContent.Auth.Visible && blockedContent.Auth.Note.Contains("人工核实") &&
                !blockedContent.Auth.Note.Contains("只读复检"), "终止性阻断的认证列声称复检：" + blockedContent.Auth.Note);
            Require(blockedContent.Submission.Note.Contains("没有可证实的提交结果复检") &&
                !blockedContent.Submission.Note.Contains("继续低频只读复检"),
                "终止性阻断的额度行声称复检：" + blockedContent.Submission.Note);

            // 需要人工验证：明确的人工处理原因优先，不声称复检。
            var interaction = unknown with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.InteractionRequired),
                AuthenticationFailure = nameof(PortalFailure.InteractionRequired)
            };
            var interactionContent = OverviewModel.From(Reading(interaction));
            Require(interactionContent.Submission.Note.Contains("明确拒绝") &&
                !interactionContent.Submission.Note.Contains("继续低频只读复检"),
                "人工验证要求被说成仍在复检：" + interactionContent.Submission.Note);
            return Task.CompletedTask;
        }),
        ("额度可见：复检文案跟随校园 Wi-Fi 与待决状态", () =>
        {
            var pending = Budget(SubmissionBudgetCodes.Exhausted, 3, true, SubmissionRetryDecision.Exhausted) with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            };

            // 提交结果待决但校园 Wi-Fi 不可用：服务会跳过复检，界面只能说明等待网络条件恢复。
            foreach (var (wifi, linkCode, label) in new[]
            {
                ("未连接", WifiLinkCodes.Disconnected, "Wi-Fi 未连接"),
                ("已关联，等待 IPv4", WifiLinkCodes.WaitingForAddress, "等待 IPv4"),
                ("网卡未就绪", WifiLinkCodes.Unavailable, "网卡未就绪"),
                ("正在连接", WifiLinkCodes.Associating, "正在连接")
            })
            {
                var waiting = OverviewModel.From(Reading(pending with { Wifi = wifi, WifiLinkCode = linkCode }));
                Require(waiting.Submission.Note.Contains("校园 Wi-Fi") &&
                    waiting.Submission.Note.Contains("待网络条件恢复") &&
                    !waiting.Submission.Note.Contains("继续低频只读复检"),
                    label + "：额度行仍声称正在复检：" + waiting.Submission.Note);
                Require(waiting.Auth.Note.Contains("校园 Wi-Fi") &&
                    waiting.Auth.Note.Contains("待网络条件恢复") &&
                    !waiting.Auth.Note.Contains("继续只读复检"),
                    label + "：认证列仍声称正在复检：" + waiting.Auth.Note);
            }

            // Wi-Fi 可用且提交待决：复检确实在运行，保留说明。
            var active = OverviewModel.From(Reading(pending));
            Require(active.Submission.Note.Contains("继续低频只读复检"),
                "Wi-Fi 可用时额度行丢失复检说明：" + active.Submission.Note);
            Require(active.Auth.Note.Contains("继续只读复检"),
                "Wi-Fi 可用时认证列丢失复检说明：" + active.Auth.Note);

            // 首次尚未提交凭据且外网在线：InternetOnline 不代表存在复检链。
            var onlineUnused = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Unused, 0, false,
                SubmissionRetryDecision.InternetOnline));
            Require(onlineUnused.Value.Contains("外网已恢复，暂停提交"), "外网在线状态码缺失：" + onlineUnused.Value);
            Require(onlineUnused.Note.Contains("没有待确认的提交结果") && !onlineUnused.Note.Contains("只读复检"),
                "无待决提交时仍声称复检：" + onlineUnused.Note);

            // 有待决提交且外网在线：复检链仍在，保留说明。
            var onlinePending = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.InternetOnline) with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            });
            Require(onlinePending.Note.Contains("继续只读复检"),
                "待决提交时外网在线丢失复检说明：" + onlinePending.Note);
            return Task.CompletedTask;
        }),
        ("额度可见：旧版快照缺字段时概览降级且不渲染未知码", () =>
        {
            const string legacy = """
                {
                  "UpdatedAt": "2026-10-29T00:00:00+00:00",
                  "State": "校园网在线",
                  "Detail": "",
                  "Enabled": true,
                  "Wifi": "已连接",
                  "Direct": "外网可达",
                  "Portal": "未查询（外网检查已通过）",
                  "Tunnel": "系统路径可达，出口未核验",
                  "Uu": "未核验",
                  "AuthenticationStage": "Unknown",
                  "AuthenticationFailure": "None",
                  "AuthenticationPersistentState": "Unknown"
                }
                """;
            var parsed = StatusSnapshotReader.Parse(legacy)!;
            Require(parsed.SubmissionBudgetCode == "" && parsed.SubmissionAttempts is null && parsed.SubmissionAttemptMax is null &&
                parsed.SubmissionBudgetExhausted is null && parsed.SubmissionNextPossibleAt is null,
                "旧版快照被赋予了新额度字段默认值");
            var content = OverviewModel.From(Reading(parsed));
            Require(content.Submission.Kind == OverviewSubmissionBudgetKind.Unknown && content.Submission.Value == "额度状态不可确认",
                "旧版快照未按不可确认降级");
            Require(content.Progress[0].Caption == "认证提交额度" && content.Progress[0].Value == "额度状态不可确认",
                "进度行未按不可确认降级");
            Require(!content.Progress[0].Note.Contains("0/3"), "旧版快照被猜测为零次额度");

            // 未知码（含伪造码）不得作为任意文本渲染，也不得改变耗尽事实。
            var forged = parsed with { SubmissionBudgetCode = "FutureBudgetCode", SubmissionRetryCode = "FutureRetryCode" };
            var forgedContent = OverviewModel.From(Reading(forged));
            Require(forgedContent.Submission.Kind == OverviewSubmissionBudgetKind.Unknown, "未知额度码未降级");
            Require(!forgedContent.Submission.Value.Contains("FutureBudgetCode") && !forgedContent.Submission.Note.Contains("FutureBudgetCode"),
                "未知额度码被渲染为文本");
            Require(!forgedContent.Submission.Value.Contains("FutureRetryCode") && !forgedContent.Submission.Note.Contains("FutureRetryCode"),
                "未知重试码被渲染为文本");
            Require(!forgedContent.Chips.Any(chip => chip.Text.Contains("Future")), "未知码进入了附加事实标签");

            var explicitUnknown = OverviewModel.From(Reading(parsed with { SubmissionBudgetCode = SubmissionBudgetCodes.Unknown }));
            Require(explicitUnknown.Submission.Kind == OverviewSubmissionBudgetKind.Unknown &&
                explicitUnknown.Submission.Note.Contains("记录缺失"), "显式 Unknown 码未按记录缺失降级");
            return Task.CompletedTask;
        }),
        ("额度可见：概览区分即将用尽、已耗尽、已重置与等待重试", () =>
        {
            var now = DateTimeOffset.Now.AddSeconds(90);
            var near = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.NearLimit, 2, false,
                SubmissionRetryDecision.Waiting, now));
            Require(near.Kind == OverviewSubmissionBudgetKind.NearLimit && near.Value.Contains("2/3") &&
                near.Value.Contains("即将用尽") && near.Value.Contains("剩余 1 次"), "即将用尽文案不符：" + near.Value);
            Require(near.Value.Contains("等待重试") && near.Note.Contains("最早下次提交：" + now.ToLocalTime().ToString("HH:mm:ss")),
                "即将用尽未同时显示等待与最早下次提交：" + near.Note);
            Require(near.Tone == OverviewTone.Attention, "即将用尽语义色不符");

            // 提交结果未知且仍待决：这是唯一存在低频只读复检链的耗尽场景。
            var exhausted = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Exhausted, 3, true,
                SubmissionRetryDecision.Exhausted) with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            });
            Require(exhausted.Kind == OverviewSubmissionBudgetKind.Exhausted && exhausted.Value.Contains("3/3") &&
                exhausted.Value.Contains("已耗尽") && exhausted.Value.Contains("已停止自动提交"), "耗尽文案不符：" + exhausted.Value);
            Require(exhausted.Note.Contains("两次额外额度") && exhausted.Note.Contains("重新开始认证") &&
                exhausted.Note.Contains("继续低频只读复检"), "耗尽未说明停止原因、复检或重置入口：" + exhausted.Note);
            Require(exhausted.Tone == OverviewTone.Warning, "耗尽语义色不符");

            var reset = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Reset, 0, false,
                SubmissionRetryDecision.Authorized));
            Require(reset.Kind == OverviewSubmissionBudgetKind.Reset && reset.Value.Contains("已重置") &&
                !reset.Value.Contains("已耗尽"), "重置文案不符：" + reset.Value);
            Require(reset.Note.Contains("重启和保存设置不会重置"), "重置说明缺少安全边界");
            Require(reset.Tone == OverviewTone.Good, "重置语义色不符");

            var waiting = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.Waiting, now));
            Require(waiting.Value.Contains("1/3") && waiting.Value.Contains("等待重试") &&
                waiting.Note.Contains("最早下次提交"), "等待重试文案不符：" + waiting.Value);

            var unused = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Unused, 0, false,
                SubmissionRetryDecision.Authorized));
            Require(unused.Value.Contains("尚未使用") && unused.Value.Contains("0/3"), "未使用文案不符：" + unused.Value);

            var inflight = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.InFlight));
            Require(inflight.Value.Contains("上次提交执行中") && inflight.Note.Contains("不重复发送凭据"), "执行中文案不符");
            var concluded = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.Concluded));
            Require(concluded.Value.Contains("自动提交已停止") && !concluded.Value.Contains("仍有剩余") &&
                concluded.Note.Contains("已明确结束") && concluded.Note.Contains("已停止自动提交"),
                "明确结束文案不符：" + concluded.Value + " / " + concluded.Note);
            var online = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.InternetOnline));
            Require(online.Value.Contains("外网已恢复，暂停提交") && online.Note.Contains("不证明门户接受"), "外网暂停文案不符");
            foreach (var code in new[] { SubmissionRetryDecision.WaitClock, SubmissionRetryDecision.InvalidTime })
            {
                var uncertain = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false, code));
                Require(uncertain.Value.Contains("时间不可信，保守延后"), "时间不可信文案不符：" + code);
            }

            var unknownRetry = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                null, retryCode: "FutureRetryCode"));
            Require(unknownRetry.Note.Contains("重试状态不可确认") && !unknownRetry.Value.Contains("FutureRetryCode") &&
                !unknownRetry.Note.Contains("FutureRetryCode"), "未知重试码被渲染为文本");

            // 耗尽事实同时进入附加事实标签；认证列说明与额度行分列，不与认证暂停混为一谈。
            var snapshot = Budget(SubmissionBudgetCodes.Exhausted, 3, true, SubmissionRetryDecision.Exhausted) with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            };
            var content = OverviewModel.From(Reading(snapshot));
            Require(content.Progress[0].Value == exhausted.Value && content.Progress[0].Note == exhausted.Note,
                "进度行未使用同一额度投影");
            Require(content.Chips.Any(chip => chip.Text == "认证提交额度已耗尽" && chip.Tone == OverviewTone.Warning),
                "耗尽未进入附加事实标签");
            Require(content.Auth.Visible && content.Auth.Title == "认证提交已暂停" &&
                content.Auth.Note.Contains("提交额度已耗尽"), "认证列未说明额度耗尽原因");
            return Task.CompletedTask;
        }),
        ("额度可见：额度级别与数值矛盾时降级而不是猜 0 次或已重置", () =>
        {
            var contradictory = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Unused, 2, false,
                SubmissionRetryDecision.Authorized));
            Require(contradictory.Kind == OverviewSubmissionBudgetKind.Unknown &&
                contradictory.Value == "额度状态不可确认" && !contradictory.Value.Contains("0/3"),
                "未使用与已用次数矛盾时仍显示 0 次：" + contradictory.Value);

            var exhaustedUnused = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Unused, 0, true,
                SubmissionRetryDecision.Authorized));
            Require(exhaustedUnused.Kind == OverviewSubmissionBudgetKind.Unknown, "未使用与耗尽矛盾时未降级");

            var badMax = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Unused, 0, false,
                SubmissionRetryDecision.Authorized) with { SubmissionAttemptMax = 0 });
            Require(badMax.Kind == OverviewSubmissionBudgetKind.Unknown, "上限非正时未降级");

            var unknownCode = OverviewModel.SubmissionBudget(Budget("FutureBudgetCode", 0, false,
                SubmissionRetryDecision.Authorized));
            Require(unknownCode.Kind == OverviewSubmissionBudgetKind.Unknown &&
                unknownCode.Value == "额度状态不可确认" && !unknownCode.Value.Contains("已重置"),
                "未知额度码被渲染为已重置：" + unknownCode.Value);

            // 每一类“级别与数值互相矛盾”的组合都必须降级，不能宣称可用或已耗尽。
            var contradictions = new (string Name, StatusSnapshot Snapshot)[]
            {
                ("耗尽却只用了 1 次", Budget(SubmissionBudgetCodes.Exhausted, 1, true, SubmissionRetryDecision.Exhausted)),
                ("可用却已用满 3 次", Budget(SubmissionBudgetCodes.Available, 3, false, SubmissionRetryDecision.Authorized)),
                ("可用却已用满且标耗尽", Budget(SubmissionBudgetCodes.Available, 3, true, SubmissionRetryDecision.Authorized)),
                ("已重置却已用 2 次", Budget(SubmissionBudgetCodes.Reset, 2, false, SubmissionRetryDecision.Authorized)),
                ("已重置却标耗尽", Budget(SubmissionBudgetCodes.Reset, 0, true, SubmissionRetryDecision.Authorized)),
                ("即将用尽却只用了 1 次", Budget(SubmissionBudgetCodes.NearLimit, 1, false, SubmissionRetryDecision.Waiting)),
                ("耗尽却未标耗尽", Budget(SubmissionBudgetCodes.Exhausted, 3, false, SubmissionRetryDecision.Exhausted)),
                ("上限与策略常量不符", Budget(SubmissionBudgetCodes.Available, 1, false, SubmissionRetryDecision.Authorized) with { SubmissionAttemptMax = 5 }),
                ("未使用却给出其他上限", Budget(SubmissionBudgetCodes.Unused, 0, false, SubmissionRetryDecision.Authorized) with { SubmissionAttemptMax = 5 })
            };
            foreach (var (name, snapshot) in contradictions)
            {
                var projection = OverviewModel.SubmissionBudget(snapshot);
                Require(projection.Kind == OverviewSubmissionBudgetKind.Unknown && projection.Value == "额度状态不可确认",
                    name + "：矛盾数值未降级：" + projection.Value);
                Require(!projection.Value.Contains("仍有剩余") && !projection.Value.Contains("已耗尽") &&
                    !projection.Value.Contains("已重置") && !projection.Value.Contains("剩余 1 次") &&
                    !projection.Note.Contains("下一次自动提交"),
                    name + "：矛盾数值仍给出可用或已耗尽暗示：" + projection.Value + " / " + projection.Note);
            }

            // 矛盾的原始耗尽事实也不得进入附加标签或认证列。
            var contradictorySnapshot = Budget(SubmissionBudgetCodes.Available, 3, true, SubmissionRetryDecision.Authorized) with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            };
            var contradictoryContent = OverviewModel.From(Reading(contradictorySnapshot));
            Require(contradictoryContent.Submission.Kind == OverviewSubmissionBudgetKind.Unknown, "矛盾额度未在概览降级");
            Require(!contradictoryContent.Chips.Any(chip => chip.Text == "认证提交额度已耗尽"),
                "矛盾额度仍在附加标签宣称已耗尽");
            Require(contradictoryContent.Auth.Visible && !contradictoryContent.Auth.Note.Contains("额度已耗尽") &&
                !contradictoryContent.Auth.Note.Contains("额度即将用尽"),
                "矛盾额度仍在认证列宣称额度原因：" + contradictoryContent.Auth.Note);

            // 终态 Concluded 且仍有次数：自动提交已永久停止，不能再说“仍有剩余”。
            var concludedAvailable = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.Available, 1, false,
                SubmissionRetryDecision.Concluded));
            Require(concludedAvailable.Value.Contains("自动提交已停止") && !concludedAvailable.Value.Contains("仍有剩余") &&
                concludedAvailable.Note.Contains("已明确结束"), "终态仍宣称可用：" + concludedAvailable.Value + " / " + concludedAvailable.Note);
            var concludedNearLimit = OverviewModel.SubmissionBudget(Budget(SubmissionBudgetCodes.NearLimit, 2, false,
                SubmissionRetryDecision.Concluded));
            Require(concludedNearLimit.Value.Contains("即将用尽") && !concludedNearLimit.Value.Contains("剩余 1 次") &&
                !concludedNearLimit.Value.Contains("仍有剩余") && concludedNearLimit.Note.Contains("已停止自动提交"),
                "终态即将用尽仍给出剩余次数暗示：" + concludedNearLimit.Value + " / " + concludedNearLimit.Note);
            return Task.CompletedTask;
        }),
        ("额度可见：耗尽通知进入受控词表且只提醒一次", () =>
        {
            Require(NotificationCodes.IsKnown(NotificationCodes.SubmissionBudgetExhausted), "耗尽通知码未进入受控词表");
            Require(!NotificationCodes.IsKnown("FutureBudgetCode"), "未知码被误认为已知");

            var directory = Path.Combine(Path.GetTempPath(), "autonet-issue35-notify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var presented = new List<NotificationAlert>();
                var router = new NotificationRouter(new NotificationConsumptionStore(Path.Combine(directory, "consumption.json")), alert =>
                {
                    presented.Add(alert);
                    return true;
                });
                router.Observe(Reading(Online()));
                var at = DateTimeOffset.UtcNow;
                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetExhausted, at)
                }));
                Require(presented.Count == 1 && presented[0].Code == NotificationCodes.SubmissionBudgetExhausted,
                    "耗尽通知未按词表提醒");
                Require(presented[0].Heading.Contains("提交额度已耗尽") && presented[0].Body.Contains("已停止自动提交"),
                    "耗尽通知文案不受控：" + presented[0].Heading + " / " + presented[0].Body);
                Require(presented[0].Warning, "耗尽通知应为警告级");
                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetExhausted, at)
                }));
                Require(presented.Count == 1, "同一耗尽事件被重复提醒");

                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification("FutureBudgetCode", at.AddSeconds(1))
                }));
                Require(presented.Count == 1, "未知通知码触发了提醒");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("额度可见：Wi-Fi 未就绪的耗尽通知不声称继续复检", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-issue35-notify-wifi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var presented = new List<NotificationAlert>();
                var router = new NotificationRouter(new NotificationConsumptionStore(Path.Combine(directory, "consumption.json")), alert =>
                {
                    presented.Add(alert);
                    return true;
                });
                router.Observe(Reading(Online()));
                var at = DateTimeOffset.UtcNow;
                router.Observe(Reading(Online() with
                {
                    Wifi = "未连接", WifiLinkCode = WifiLinkCodes.Disconnected,
                    Direct = "外网不可达或未知", Portal = "需认证",
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetExhausted, at)
                }));
                Require(presented.Count == 1, "耗尽通知未提醒");
                Require(presented[0].Body.Contains("暂不发起只读复检", StringComparison.Ordinal) &&
                    !presented[0].Body.Contains("继续只读复检", StringComparison.Ordinal),
                    "Wi-Fi 未就绪的耗尽通知仍声称继续复检：" + presented[0].Body);

                // 对照：受控码说链路就绪时服务确实执行复检，保留原词表文案；
                // 即使显示文字仍是“未连接”，判定也只接受受控码。
                router.Observe(Reading(Online() with
                {
                    Wifi = "未连接", WifiLinkCode = WifiLinkCodes.Ready,
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetExhausted, at.AddSeconds(1))
                }));
                Require(presented.Count == 2 && presented[1].Body.Contains("继续只读复检", StringComparison.Ordinal),
                    "Wi-Fi 就绪时丢失复检说明：" + (presented.Count > 1 ? presented[1].Body : "未提醒"));
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("额度可见：同轮门户结构异常不吞掉耗尽通知", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var reservation = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), fixture.Clock.GetUtcNow());
                    Require(reservation.Budget is not null, "准备额度预留失败");
                    Require(store.TryTransitionSubmissionBudget(reservation.Budget!, SubmissionBudgetState.PossiblySent), "准备未知提交失败");
                    fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                }
                store.BlockAuthentication(new AuthenticationBlock
                { State = AuthenticationState.SubmissionPending, Attempts = 3, Revision = Guid.NewGuid() });
                // 同轮：额度已耗尽 + 只读门户检查发现状态结构异常（ADR-0005 结构类失败）。
                // chkstatus 仍能解析但缺少 result 字段，检查阶段即判定为结构变化。
                fixture.Http.StatusJson = "dr1001({\"code\":200,\"v4ip\":\"10.0.0.1\"})";
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var first = await worker.RunOnceAsync(default);
                Require(first.SubmissionBudgetExhausted == true && first.CredentialsBlocked, "耗尽或暂停事实缺失");
                Require(first.PortalLayoutSuspected && first.Notification is { Code: NotificationCodes.PortalLayoutChanged },
                    "门户结构异常未先占用一次性通知槽");

                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                var second = await worker.RunOnceAsync(default);
                Require(second.Notification is { Code: NotificationCodes.SubmissionBudgetExhausted },
                    "额度耗尽通知被同轮结构异常吞掉且不再补发");
            });
        }),
        ("额度可见：Wi-Fi 不可用时服务详情与日志不声称继续复检", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var reservation = store.TryReserveSubmissionBudget(store.LoadSubmissionBudget(), fixture.Clock.GetUtcNow());
                    Require(reservation.Budget is not null, "准备额度预留失败");
                    Require(store.TryTransitionSubmissionBudget(reservation.Budget!, SubmissionBudgetState.PossiblySent), "准备未知提交失败");
                    fixture.Clock.Advance(TimeSpan.FromSeconds(120));
                }
                store.BlockAuthentication(new AuthenticationBlock
                { State = AuthenticationState.SubmissionPending, Attempts = 3, Revision = Guid.NewGuid() });
                var readyLink = fixture.Link;
                fixture.Link = readyLink with { Connected = false, Address = null };
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var status = await worker.RunOnceAsync(default);
                Require(status.SubmissionBudgetExhausted == true, "耗尽事实缺失");
                Require(status.Detail.Contains("暂不发起只读复检", StringComparison.Ordinal) &&
                    !status.Detail.Contains("已用尽；继续低频只读复检", StringComparison.Ordinal),
                    "Wi-Fi 不可用时状态详情仍声称继续复检：" + status.Detail);
                Require(fixture.Logs.Any(entry => entry.EventCode == "SubmissionBudgetExhausted" &&
                    entry.Detail.Contains("暂不发起只读复检", StringComparison.Ordinal) &&
                    !entry.Detail.Contains("自动提交；继续低频只读复检", StringComparison.Ordinal)),
                    "Wi-Fi 不可用时耗尽日志仍声称继续复检");

                // 对照：校园 Wi-Fi 就绪时复检确实在运行，保留原有说明。
                fixture.Link = readyLink;
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                var ready = await worker.RunOnceAsync(default);
                Require(ready.Detail.Contains("继续低频只读复检", StringComparison.Ordinal),
                    "Wi-Fi 就绪时丢失复检说明：" + ready.Detail);
            });
        }),
        ("额度可见：复检判据只接受链路受控码", () =>
        {
            var pending = Budget(SubmissionBudgetCodes.Exhausted, 3, true, SubmissionRetryDecision.Exhausted) with
            {
                CredentialsBlocked = true,
                AuthenticationPersistentState = nameof(AuthenticationState.SubmissionPending)
            };
            // 显示文字说“已连接”，但旧版快照没有受控码：不能声称正在复检。
            var legacy = OverviewModel.SubmissionBudget(pending with { Wifi = "已连接", WifiLinkCode = "" });
            Require(!legacy.Note.Contains("继续低频只读复检") && legacy.Note.Contains("待网络条件恢复"),
                "缺少受控码时仍声称继续复检：" + legacy.Note);
            // 显示文字说“未连接”，但受控码说就绪：以受控码为准，不读显示文字。
            var ready = OverviewModel.SubmissionBudget(pending with { Wifi = "未连接", WifiLinkCode = WifiLinkCodes.Ready });
            Require(ready.Note.Contains("继续低频只读复检"),
                "受控码就绪时仍按显示文字降级：" + ready.Note);
            return Task.CompletedTask;
        }),
        ("额度可见：额度事件进入净化摘要与认证分类", () =>
        {
            var entry = new ActivityLog
            {
                Time = DateTimeOffset.UtcNow,
                Action = "提交额度已耗尽",
                Detail = "fixture-secret-detail",
                EventCode = "SubmissionBudgetExhausted"
            };
            var summary = LogSummary.Format([entry]);
            Require(summary.Contains("事件 SubmissionBudgetExhausted", StringComparison.Ordinal),
                "净化摘要丢弃了耗尽事件：" + summary);
            Require(!summary.Contains("fixture-secret-detail", StringComparison.Ordinal), "净化摘要泄露了自由文本");
            Require(LogEventClassifier.Classify(entry) == LogEventCategory.Authentication, "耗尽事件未归入认证分类");
            return Task.CompletedTask;
        }),
        ("额度可见：即将用尽与重置通知进入受控词表且只提醒一次", () =>
        {
            Require(NotificationCodes.IsKnown(NotificationCodes.SubmissionBudgetNearLimit), "即将用尽通知码未进入受控词表");
            Require(NotificationCodes.IsKnown(NotificationCodes.SubmissionBudgetReset), "重置通知码未进入受控词表");
            var directory = Path.Combine(Path.GetTempPath(), "autonet-issue35-notify-budget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var presented = new List<NotificationAlert>();
                var router = new NotificationRouter(new NotificationConsumptionStore(Path.Combine(directory, "consumption.json")), alert =>
                {
                    presented.Add(alert);
                    return true;
                });
                router.Observe(Reading(Online()));
                var at = DateTimeOffset.UtcNow;
                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetNearLimit, at)
                }));
                Require(presented.Count == 1 && presented[0].Code == NotificationCodes.SubmissionBudgetNearLimit,
                    "即将用尽通知未按词表提醒");
                Require(presented[0].Heading.Contains("即将用尽") && presented[0].Warning, "即将用尽通知文案或级别不符");
                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetNearLimit, at)
                }));
                Require(presented.Count == 1, "同一即将用尽事件被重复提醒");

                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetReset, at.AddSeconds(1))
                }));
                Require(presented.Count == 2 && presented[1].Code == NotificationCodes.SubmissionBudgetReset,
                    "额度重置通知未按词表提醒");
                Require(presented[1].Heading.Contains("已重置") && !presented[1].Warning, "额度重置通知文案或级别不符");
                router.Observe(Reading(Online() with
                {
                    Notification = new StatusNotification(NotificationCodes.SubmissionBudgetReset, at.AddSeconds(1))
                }));
                Require(presented.Count == 2, "同一额度重置事件被重复提醒");
            }
            finally { Directory.Delete(directory, recursive: true); }
            return Task.CompletedTask;
        }),
        ("额度可见：只读预演不推进重试锚点", () =>
        {
            var start = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
            var budget = new SubmissionBudget
            {
                State = SubmissionBudgetState.PossiblySent, Attempts = 1,
                ReservedAt = start, NextPossibleAt = start.AddSeconds(SubmissionRetryPolicy.SubmissionIntervalSeconds),
                Revision = Guid.NewGuid()
            };
            var policy = new SubmissionRetryPolicy();
            policy.Evaluate(null, start, TimeSpan.Zero);
            var decision = policy.Preview(budget, start.AddSeconds(30), TimeSpan.FromSeconds(30), false, out var previewNext);
            Require(decision == SubmissionRetryDecision.Waiting && previewNext is not null, "预演未给出等待判定");
            Require(policy.NextPossibleAt is null, "预演改写了策略的公开下次提交时刻");
            // 预演不提交 observed：下一次真实判定仍会按新预算重新锚定，不会提前。
            var actual = policy.Evaluate(budget, start.AddSeconds(32), TimeSpan.FromSeconds(32), false);
            Require(actual == SubmissionRetryDecision.Waiting && policy.NextPossibleAt is { } actualNext &&
                actualNext >= previewNext, "真实判定在预演后前移了下次提交时刻");
            return Task.CompletedTask;
        }),
        ("额度可见：动作后的下次提交时刻不被展示路径前移", async () =>
        {
            await WithStore(async (fixture, store) =>
            {
                fixture.Http.LoginResult = "unparseable";
                // 让登录请求真实耗时：动作期间推进时钟，暴露“展示路径重新锚定”的副作用。
                fixture.Http.BeforeLogin = () => fixture.Clock.Advance(TimeSpan.FromSeconds(30));
                using var worker = new MonitorWorker(new AcceptanceRegression.StoredEnvironment(fixture, store), fixture.Clock);
                var first = await worker.RunOnceAsync(default);
                var firstAt = first.SubmissionNextPossibleAt;
                Require(first.SubmissionAttempts == 1 && firstAt is not null, "首次提交后缺少下次提交时刻");
                var persisted = store.LoadSubmissionBudget();
                Require(persisted is { Attempts: 1 } && firstAt >= persisted.NextPossibleAt,
                    "展示的下次提交时刻早于持久额度下限");
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
                var second = await worker.RunOnceAsync(default);
                var secondAt = second.SubmissionNextPossibleAt;
                Require(secondAt is not null && secondAt >= firstAt, "展示路径把下次提交时刻前移了");
            });
        })
    ];

    private static StatusSnapshot Budget(string budgetCode, int attempts, bool exhausted,
        SubmissionRetryDecision? retry, DateTimeOffset? next = null, string? retryCode = null) => Online() with
    {
        SubmissionBudgetCode = budgetCode,
        SubmissionRetryCode = retryCode ?? retry?.ToString() ?? "",
        SubmissionAttempts = attempts,
        SubmissionAttemptMax = 3,
        SubmissionBudgetExhausted = exhausted,
        SubmissionNextPossibleAt = next
    };

    private static StatusReading Reading(StatusSnapshot snapshot)
    {
        var valid = StatusSnapshotReader.IsValid(snapshot);
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.Zero), valid, valid, TimeSpan.Zero, DateTimeOffset.UtcNow);
    }

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        WifiLinkCode = WifiLinkCodes.Ready,
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验",
        SubmissionCooldownSeconds = SubmissionRetryPolicy.SubmissionIntervalSeconds
    };

    private static async Task WithStore(Func<MonitorFixture, SafetyStateStore, Task> test)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = Path.GetFullPath(Path.Combine(root, "AutoNet-issue35-" + Guid.NewGuid().ToString("N")));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理越界");
        Directory.CreateDirectory(directory);
        try { await test(new MonitorFixture(), new SafetyStateStore(directory)); }
        finally { Directory.Delete(directory, true); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
