using System.Net;
using System.Reflection;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页回归（迁移工单 #37）：导航单一来源、七种状态投影、打开/切页/最小化不导航或提交、
/// 普通刷新不丢输入、运行时缺失/边界未就绪/加载失败的受控提示、宿主抽象的只读边界与线程契约。
/// 全部使用假宿主与隔离状态文件；不连接真实门户、不读取真实凭据、不安装 WebView2。
/// </summary>
internal static class AuthPageRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证页回归：导航目录新增上网认证且每页恰好出现一次", () => Sync(() =>
        {
            Require(ShellPages.All.Select(info => info.Page).SequenceEqual(
                [ShellPage.Overview, ShellPage.Authentication, ShellPage.Diagnostics, ShellPage.Logs, ShellPage.Settings]),
                "导航顺序变化");
            Require(ShellPages.All.Select(info => info.Name).SequenceEqual(
                ["概览", "上网认证", "网络诊断", "日志", "设置"]), "导航名称变化");
            Require(ShellPages.All.Count(info => info.Page == ShellPage.Authentication) == 1, "上网认证页重复或缺失");
            Require(Enum.GetValues<ShellPage>().Length == ShellPages.All.Count, "存在未纳入导航的页面");
            Require(ShellPages.Info(ShellPage.Authentication).Name == "上网认证", "认证页名称不符");
            Require(ShellPages.Info(ShellPage.Authentication).Description.Contains("不提交凭据", StringComparison.Ordinal),
                "认证页说明未声明只读边界");
            Require(ShellPages.Default == ShellPage.Overview, "默认页必须为概览");
        })),
        ("认证页回归：七种状态清楚区分且优先顺序稳定", () => Sync(() =>
        {
            var online = Reading(Online());
            Require(AuthPageModel.PhaseFor(online) == AuthPagePhase.Online, "联网状态未识别");
            Require(AuthPageModel.From(online, AuthPageHostSnapshot.NotAttached).StatusTitle == "已联网", "联网标题不符");

            var authenticated = Reading(Online() with
            {
                State = "已认证但外网不可达", Direct = "外网不可达或未知", Portal = "已认证"
            });
            Require(AuthPageModel.PhaseFor(authenticated) == AuthPagePhase.Authenticated, "认证成功状态未识别");

            var unknown = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None"
            });
            Require(AuthPageModel.PhaseFor(unknown) == AuthPagePhase.OutcomeUnknown, "提交结果未知状态未识别");

            var waiting = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(60)
            });
            Require(AuthPageModel.PhaseFor(waiting) == AuthPagePhase.WaitingRetry, "等待重试状态未识别");

            var manual = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "CredentialsRejected",
                AuthenticationFailure = "CredentialsRejected"
            });
            Require(AuthPageModel.PhaseFor(manual) == AuthPagePhase.NeedsManual, "需要人工状态未识别");

            foreach (var reading in new[] { Reading(null), Reading(Online() with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) }), Reading(new StatusSnapshot { Tunnel = null! }) })
                Require(AuthPageModel.PhaseFor(reading) == AuthPagePhase.Unconfirmed, "不可信状态未降级为待确认");

            // 联网事实优先于残留的认证暂停，不能把已恢复的链路说成仍有问题。
            Require(AuthPageModel.PhaseFor(Reading(Online() with { CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending" })) ==
                AuthPagePhase.Online, "联网时仍按认证暂停投影");

            var paused = Reading(Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            });
            Require(AuthPageModel.PhaseFor(paused) == AuthPagePhase.Paused, "已暂停状态未识别");
            Require(AuthPageModel.PhaseFor(Reading(null)) == AuthPagePhase.Unconfirmed, "待确认状态未识别");

            var titles = new[]
            {
                AuthPageModel.From(online, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(authenticated, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(unknown, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(waiting, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(manual, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(paused, AuthPageHostSnapshot.NotAttached).StatusTitle,
                AuthPageModel.From(Reading(null), AuthPageHostSnapshot.NotAttached).StatusTitle
            };
            Require(titles.Distinct().Count() == 7, "七种状态标题未清楚区分：" + string.Join("、", titles));
            Require(AuthPageModel.From(online, AuthPageHostSnapshot.NotAttached).PortalAddress == AuthPageModel.PortalAddress,
                "页面未显示固定门户地址");
        })),
        ("认证页回归：结果未知准确区分只读复检与有限自动重试", () => Sync(() =>
        {
            var pending = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None"
            });
            var content = AuthPageModel.From(pending, AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.OutcomeUnknown, "待消解提交未投影为结果未知");
            Require(content.StatusExplanation.Contains("只读复检", StringComparison.Ordinal), "未说明只读复检");
            Require(content.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal), "未说明可能的有限自动重试");
            Require(content.StatusExplanation.Contains("额度", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("最短间隔", StringComparison.Ordinal), "重试条件缺少额度或间隔");
            Require(content.StatusExplanation.Contains("不会重发凭据", StringComparison.Ordinal), "未声明条件不满足时不重发");
            Require(!content.StatusExplanation.Contains("只做只读复检", StringComparison.Ordinal), "仍在声称只做只读复检");
            Require(content.NextStep.Contains("不要重复提交", StringComparison.Ordinal), "下一步缺少避免重复提交的提示");

            // 服务已明确发布“提交额度已用尽”时，按人工状态展示，不再暗示自动重试。
            var exhausted = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None",
                Detail = "已停止自动提交：首次提交后的两次额外额度已用尽；继续低频只读复检。"
            });
            var exhaustedContent = AuthPageModel.From(exhausted, AuthPageHostSnapshot.NotAttached);
            Require(exhaustedContent.Phase == AuthPagePhase.NeedsManual, "提交额度用尽未投影为人工处理");
            Require(exhaustedContent.StatusTitle == "提交额度已用尽", "提交额度用尽标题不符");
            Require(exhaustedContent.StatusExplanation.Contains("不会自动重发凭据", StringComparison.Ordinal),
                "额度用尽仍暗示自动重发");
            Require(!exhaustedContent.StatusExplanation.Contains("可能进行有限自动重试", StringComparison.Ordinal),
                "额度用尽仍承诺自动重试");
            Require(exhaustedContent.BackgroundNote.Contains("额度已用尽", StringComparison.Ordinal) &&
                !exhaustedContent.BackgroundNote.Contains("有限自动重试", StringComparison.Ordinal),
                "额度用尽的背景说明仍暗示有限自动重试");

            // 主动断线额度与提交额度是两套预算，不能互相误判。
            var disconnectOnly = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None",
                Detail = "本次故障的 3 次主动断线额度已用尽，停止主动断线；连续在线 60 秒后恢复额度。"
            });
            Require(AuthPageModel.PhaseFor(disconnectOnly) == AuthPagePhase.OutcomeUnknown,
                "主动断线额度被误判为提交额度");
        })),
        ("认证页回归：明确人工信号优先于结果未知", () => Sync(() =>
        {
            var pending = Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None"
            };

            var layout = AuthPageModel.From(Reading(pending with { PortalLayoutSuspected = true }), AuthPageHostSnapshot.NotAttached);
            Require(layout.Phase == AuthPagePhase.NeedsManual && layout.StatusTitle == "门户结构疑似变化",
                "门户结构信号未优先于结果未知");
            Require(layout.StatusExplanation.Contains("结果仍未确认", StringComparison.Ordinal), "结构变化未保留结果未知事实");

            var storage = AuthPageModel.From(Reading(pending with { SubmissionResolutionStorageFault = true }), AuthPageHostSnapshot.NotAttached);
            Require(storage.Phase == AuthPagePhase.NeedsManual && storage.StatusTitle == "认证状态需检查",
                "提交消解存储故障未优先于结果未知");
            Require(storage.BackgroundNote.Contains("存储", StringComparison.Ordinal) &&
                storage.BackgroundNote.Contains("有限自动重试", StringComparison.Ordinal),
                "存储故障的背景说明未说明有限重试仍可能发生");

            var fallback = AuthPageModel.From(Reading(pending with { BrowserFallbackRequested = true }), AuthPageHostSnapshot.NotAttached);
            Require(fallback.Phase == AuthPagePhase.NeedsManual && fallback.StatusTitle == "需要人工处理",
                "浏览器兜底请求未优先于结果未知");

            foreach (var state in new[] { "BindingRejected", "CredentialsRejected", "InteractionRequired", "SafetyBlocked" })
            {
                var terminal = AuthPageModel.From(Reading(pending with { AuthenticationPersistentState = state }), AuthPageHostSnapshot.NotAttached);
                Require(terminal.Phase == AuthPagePhase.NeedsManual && terminal.StatusTitle == "需要人工处理",
                    "终止性认证状态未投影为人工处理：" + state);
            }

            // 仅凭 CredentialsBlocked（旧版/损坏记录）也不能退回“结果未知”。
            var legacy = AuthPageModel.From(Reading(pending with { AuthenticationPersistentState = "Unknown" }), AuthPageHostSnapshot.NotAttached);
            Require(legacy.Phase == AuthPagePhase.NeedsManual, "旧版阻断记录未按人工处理降级");
        })),
        ("认证页回归：消解存储故障不承诺停止有限重试", () => Sync(() =>
        {
            var pending = Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None", SubmissionResolutionStorageFault = true
            };
            var content = AuthPageModel.From(Reading(pending), AuthPageHostSnapshot.NotAttached);
            Require(content.StatusTitle == "认证状态需检查", "存储故障未提示检查");
            Require(content.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal), "正文没有说明有限重试仍可能发生");
            Require(content.StatusExplanation.Contains("额度", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("间隔", StringComparison.Ordinal), "正文没有说明额度和间隔仍约束重试");
            Require(!content.StatusExplanation.Contains("不会据此重发凭据", StringComparison.Ordinal), "正文错误承诺不重发");
            Require(!content.BackgroundNote.Contains("不会据此重发凭据", StringComparison.Ordinal), "背景说明错误承诺不重发");
        })),
        ("认证页回归：门户结构信号与认证暂停分别呈现", () => Sync(() =>
        {
            var layout = Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                PortalLayoutSuspected = true, CredentialsBlocked = false,
                AuthenticationPersistentState = "Ready", AuthenticationFailure = "None"
            };
            var unblocked = AuthPageModel.From(Reading(layout), AuthPageHostSnapshot.NotAttached);
            Require(unblocked.StatusTitle == "门户结构疑似变化", "未展示结构疑似变化");
            Require(!unblocked.StatusExplanation.Contains("已安全暂停", StringComparison.Ordinal), "仅凭结构信号断言认证暂停");
            Require(unblocked.StatusExplanation.Contains("不代表", StringComparison.Ordinal), "未说明结构信号与暂停的区别");

            var blocked = AuthPageModel.From(Reading(layout with
            {
                CredentialsBlocked = true, AuthenticationPersistentState = "CredentialsRejected",
                AuthenticationFailure = "CredentialsRejected"
            }), AuthPageHostSnapshot.NotAttached);
            Require(blocked.StatusExplanation.Contains("已暂停", StringComparison.Ordinal), "明确认证阻断未如实显示");

            var pending = AuthPageModel.From(Reading(layout with
            {
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending"
            }), AuthPageHostSnapshot.NotAttached);
            Require(pending.StatusExplanation.Contains("结果仍未确认", StringComparison.Ordinal), "结果未知事实丢失");
            Require(!pending.StatusExplanation.Contains("已安全暂停", StringComparison.Ordinal), "待决状态误称永久暂停");
        })),
        ("认证页回归：外网恢复与不可信状态不显示结果未知", () => Sync(() =>
        {
            var onlinePending = Reading(Online() with
            {
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None"
            });
            var content = AuthPageModel.From(onlinePending, AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.Online && content.StatusTitle == "已联网",
                "外网恢复后仍投影为结果未知");
            Require(content.StatusExplanation.Contains("上次提交结果", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("不会重新提交", StringComparison.Ordinal),
                "外网恢复未说明暂停重新提交");
            Require(content.BackgroundNote.Contains("暂停提交", StringComparison.Ordinal),
                "外网恢复的背景说明未声明暂停提交");
            Require(!content.StatusTitle.Contains("未知", StringComparison.Ordinal), "外网恢复仍显示未知标题");

            var untrusted = AuthPageModel.From(Reading(null), AuthPageHostSnapshot.NotAttached);
            Require(untrusted.Phase == AuthPagePhase.Unconfirmed && untrusted.StatusTitle == "状态待确认",
                "缺失状态未降级为待确认");
        })),
        ("认证页回归：恢复开关关闭与等待自动重试准确区分", () => Sync(() =>
        {
            var pausedSnapshot = Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            };
            var paused = AuthPageModel.From(Reading(pausedSnapshot), AuthPageHostSnapshot.NotAttached);
            Require(paused.Phase == AuthPagePhase.Paused, "恢复开关关闭未投影为已暂停：" + paused.Phase);
            Require(paused.StatusTitle == "自动恢复已关闭", "暂停标题不符：" + paused.StatusTitle);
            Require(paused.StatusExplanation.Contains("不会重连", StringComparison.Ordinal) &&
                paused.StatusExplanation.Contains("自动提交", StringComparison.Ordinal),
                "暂停说明未声明不会重连或自动提交");
            Require(!paused.StatusExplanation.Contains("自动认证将在", StringComparison.Ordinal) &&
                !paused.StatusExplanation.Contains("重试节奏", StringComparison.Ordinal),
                "恢复关闭仍声称会自动认证");
            Require(paused.NextStep.Contains("概览", StringComparison.Ordinal) &&
                paused.NextStep.Contains("开启自动恢复", StringComparison.Ordinal), "暂停状态未说明如何恢复");
            Require(paused.BackgroundNote.Contains("自动恢复已关闭", StringComparison.Ordinal) &&
                !paused.BackgroundNote.Contains("按协议认证运行", StringComparison.Ordinal),
                "暂停状态的背景说明仍声称协议认证在运行");

            // 同样“门户需认证”，恢复开启时才是等待自动重试。
            var enabledSnapshot = pausedSnapshot with
            {
                Enabled = true, State = "需要处理认证", Detail = "校园网认证已失效。",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(60)
            };
            var waiting = AuthPageModel.From(Reading(enabledSnapshot), AuthPageHostSnapshot.NotAttached);
            Require(waiting.Phase == AuthPagePhase.WaitingRetry && waiting.StatusTitle == "等待重试",
                "恢复开启时未投影为等待重试");
            Require(waiting.StatusExplanation.Contains("自动认证将在", StringComparison.Ordinal),
                "等待重试未说明自动认证会继续");
            Require(waiting.NextStep.Contains("开启自动恢复", StringComparison.Ordinal) == false,
                "等待重试错误要求开启自动恢复");

            // 不可信状态不能仅凭 Enabled=false 断言已暂停。
            var stale = Reading(pausedSnapshot with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            Require(AuthPageModel.PhaseFor(stale) == AuthPagePhase.Unconfirmed, "过期状态被当作已暂停");

            // “等待设置”“检测异常”等快照的 Enabled 只是缺省 false，不能据此宣称用户关闭了恢复。
            foreach (var state in new[] { "等待设置", "检测异常" })
            {
                var other = Reading(pausedSnapshot with { State = state, Portal = "待检测", Detail = "夹具：" + state });
                Require(AuthPageModel.PhaseFor(other) != AuthPagePhase.Paused, "缺省 Enabled 被误读为恢复开关关闭：" + state);
            }
        })),
        ("认证页回归：恢复关闭与提交结果未知并存时不承诺自动重试", () => Sync(() =>
        {
            var pendingPaused = Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None", Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            };
            var content = AuthPageModel.From(Reading(pendingPaused), AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.Paused && content.StatusTitle == "自动恢复已关闭",
                "暂停与结果未知并存时未按暂停投影");
            Require(content.StatusExplanation.Contains("上次凭据提交的结果仍未确认", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("只读复检", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("不会重新提交", StringComparison.Ordinal),
                "暂停状态未如实说明待消解提交与只读复检");
            Require(!content.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal) &&
                !content.StatusExplanation.Contains("可能", StringComparison.Ordinal),
                "恢复关闭仍暗示可能自动重试");
            Require(content.NextStep.Contains("开启自动恢复", StringComparison.Ordinal), "未说明如何恢复自动处理");
            Require(content.BackgroundNote.Contains("自动恢复已关闭", StringComparison.Ordinal) &&
                content.BackgroundNote.Contains("只读复检", StringComparison.Ordinal) &&
                !content.BackgroundNote.Contains("有限自动重试", StringComparison.Ordinal),
                "暂停状态的背景说明仍承诺有限自动重试");

            // 对照：恢复开启时同一快照才允许“条件满足时的有限自动重试”。
            var pendingEnabled = pendingPaused with
            {
                Enabled = true, State = "需要处理认证", Detail = "校园网认证已失效。"
            };
            var control = AuthPageModel.From(Reading(pendingEnabled), AuthPageHostSnapshot.NotAttached);
            Require(control.Phase == AuthPagePhase.OutcomeUnknown &&
                control.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal),
                "恢复开启的结果未知未保留有限自动重试说明");
        })),
        ("认证页回归：恢复关闭与存储故障并存时不承诺自动重试", () => Sync(() =>
        {
            var snapshot = Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None", SubmissionResolutionStorageFault = true,
                Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            };
            var content = AuthPageModel.From(Reading(snapshot), AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.NeedsManual && content.StatusTitle == "认证状态需检查",
                "存储故障与暂停并存时未提示检查");
            Require(content.StatusExplanation.Contains("自动恢复当前已关闭", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("不会自动重试", StringComparison.Ordinal),
                "存储故障说明未声明恢复关闭且不会自动重试");
            Require(!content.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal),
                "恢复关闭仍承诺有限自动重试");
            Require(content.BackgroundNote.Contains("自动恢复已关闭", StringComparison.Ordinal) &&
                content.BackgroundNote.Contains("存储", StringComparison.Ordinal) &&
                !content.BackgroundNote.Contains("有限自动重试", StringComparison.Ordinal),
                "存储故障在暂停下的背景说明不准确");
        })),
        ("认证页回归：恢复关闭与额度耗尽并存时不暗示开启后自动重发", () => Sync(() =>
        {
            var snapshot = Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending",
                AuthenticationFailure = "None",
                Detail = "已停止自动提交：首次提交后的两次额外额度已用尽；继续低频只读复检。"
            };
            var content = AuthPageModel.From(Reading(snapshot), AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.NeedsManual && content.StatusTitle == "提交额度已用尽",
                "额度耗尽与暂停并存时未按额度投影");
            Require(content.StatusExplanation.Contains("自动恢复当前已关闭", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("即使开启自动恢复也不会自动重发凭据", StringComparison.Ordinal),
                "额度耗尽未说明开启恢复也不会重发");
            Require(!content.StatusExplanation.Contains("有限自动重试", StringComparison.Ordinal),
                "额度耗尽仍暗示有限自动重试");
        })),
        ("认证页回归：暂停状态下打开、切页与刷新不改变后台状态", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-auth-paused-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                WriteStatus(path, Online() with
                {
                    Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                    Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
                });
                var before = File.ReadAllBytes(path);
                var host = new FakeAuthHost();
                var coordinator = new AuthPageCoordinator(host);
                var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(25));
                using var controller = new ShellController(pipe, authPage: coordinator);
                await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Authentication);
                Require(controller.Content is AuthPageContent { Phase: AuthPagePhase.Paused },
                    "暂停状态未投影到页面");
                for (var i = 0; i < 3; i++) await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Overview);
                controller.SetWindowVisible(false);
                await controller.RefreshStatusAsync();
                controller.SetWindowVisible(true);
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(30);
                Require(host.AttachCount == 1, "打开认证页未恰好附着一次：" + host.AttachCount);
                Require(File.ReadAllBytes(path).SequenceEqual(before), "页面交互改写了后台状态文件");
                Require(controller.Status is { Snapshot.Enabled: false }, "页面交互改变了恢复开关状态");
                Require(controller.Content is AuthPageContent { Phase: AuthPagePhase.Paused },
                    "页面交互改变了认证决策投影");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("认证页回归：未保存凭据与可等待自动重试准确区分", () => Sync(() =>
        {
            // 服务在 Wi-Fi 就绪、外网不可达且没有凭据时发布受控文案；它证明后台不会发出认证请求。
            var noCredentials = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                Detail = "外网检查未通过；未保存认证信息，请在设置中保存账号密码。"
            });
            var missing = AuthPageModel.From(noCredentials, AuthPageHostSnapshot.NotAttached);
            Require(missing.Phase == AuthPagePhase.NeedsManual, "未保存凭据未投影为需要设置：" + missing.Phase);
            Require(missing.StatusTitle == "需要保存认证信息", "未保存凭据标题不符：" + missing.StatusTitle);
            Require(missing.StatusExplanation.Contains("没有可用的认证凭据", StringComparison.Ordinal) &&
                missing.StatusExplanation.Contains("不会发出认证请求", StringComparison.Ordinal),
                "未保存凭据未说明不会发出认证请求");
            Require(!missing.StatusExplanation.Contains("自动认证将在", StringComparison.Ordinal) &&
                !missing.StatusExplanation.Contains("等待", StringComparison.Ordinal),
                "未保存凭据仍让用户等待自动认证");
            Require(missing.NextStep.Contains("设置", StringComparison.Ordinal) &&
                missing.NextStep.Contains("保存账号密码", StringComparison.Ordinal), "未保存凭据未指引到设置");
            Require(missing.BackgroundNote.Contains("不会发出认证请求", StringComparison.Ordinal) &&
                !missing.BackgroundNote.Contains("按协议认证运行", StringComparison.Ordinal),
                "未保存凭据的背景说明仍声称协议认证在运行");

            // 对照：已有凭据且门户需认证时才是等待自动重试。
            var withCredentials = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                Detail = "校园网认证已失效。"
            });
            var waiting = AuthPageModel.From(withCredentials, AuthPageHostSnapshot.NotAttached);
            Require(waiting.Phase == AuthPagePhase.WaitingRetry && waiting.StatusTitle == "等待重试",
                "已有凭据时未投影为等待重试");
            Require(waiting.StatusExplanation.Contains("自动认证将在", StringComparison.Ordinal),
                "已有凭据的等待重试未说明自动认证会继续");
            Require(missing.StatusTitle != waiting.StatusTitle, "未保存凭据与等待重试标题未区分");
        })),
        ("认证页回归：暂停、等待设置与不可信快照不误判未保存凭据", () => Sync(() =>
        {
            // 恢复开关关闭时服务用暂停文案覆盖详情；不能因此宣称缺少凭据。
            var paused = AuthPageModel.From(Reading(Online() with
            {
                Enabled = false, State = "已暂停恢复", Direct = "外网不可达或未知", Portal = "需认证",
                Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            }), AuthPageHostSnapshot.NotAttached);
            Require(paused.Phase == AuthPagePhase.Paused && paused.StatusTitle == "自动恢复已关闭",
                "暂停状态被未保存凭据覆盖");
            Require(!paused.StatusExplanation.Contains("没有可用的认证凭据", StringComparison.Ordinal),
                "暂停状态猜测了凭据缺失");

            // 完全没有设置时，页面应指向设置而不是“状态待确认”。
            var settingsMissing = AuthPageModel.From(Reading(Online() with
            {
                Enabled = false, State = "等待设置", Direct = "待检测", Portal = "待检测",
                Detail = "请在托盘窗口中设置校园 Wi-Fi 和认证信息。"
            }), AuthPageHostSnapshot.NotAttached);
            Require(settingsMissing.Phase == AuthPagePhase.NeedsManual && settingsMissing.StatusTitle == "尚未保存设置",
                "缺少设置未投影为需要设置：" + settingsMissing.StatusTitle);
            Require(settingsMissing.NextStep.Contains("设置", StringComparison.Ordinal), "缺少设置未指引到设置");

            // 不可信快照即使带有相同文案也不能据此断言缺少凭据。
            var stale = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                Detail = "外网检查未通过；未保存认证信息，请在设置中保存账号密码。",
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            });
            var untrusted = AuthPageModel.From(stale, AuthPageHostSnapshot.NotAttached);
            Require(untrusted.Phase == AuthPagePhase.Unconfirmed && untrusted.StatusTitle == "状态待确认",
                "不可信快照被当作缺少凭据");
            Require(!untrusted.StatusExplanation.Contains("没有可用的认证凭据", StringComparison.Ordinal),
                "不可信快照猜测了凭据缺失");
        })),
        ("认证页回归：未保存凭据时页面交互不改变后台状态", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-auth-no-creds-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                WriteStatus(path, Online() with
                {
                    State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                    CredentialsBlocked = false, AuthenticationPersistentState = "Ready",
                    Detail = "外网检查未通过；未保存认证信息，请在设置中保存账号密码。"
                });
                var before = File.ReadAllBytes(path);
                var host = new FakeAuthHost();
                var coordinator = new AuthPageCoordinator(host);
                var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(25));
                using var controller = new ShellController(pipe, authPage: coordinator);
                await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Authentication);
                Require(controller.Content is AuthPageContent { StatusTitle: "需要保存认证信息" },
                    "未保存凭据未投影到页面");
                for (var i = 0; i < 3; i++) await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Overview);
                controller.SetWindowVisible(false);
                await controller.RefreshStatusAsync();
                controller.SetWindowVisible(true);
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(30);
                Require(host.AttachCount == 1, "打开认证页未恰好附着一次：" + host.AttachCount);
                Require(File.ReadAllBytes(path).SequenceEqual(before), "页面交互改写了后台状态文件");
                Require(controller.Status is { Snapshot.Enabled: true }, "页面交互改变了恢复开关状态");
                Require(controller.Content is AuthPageContent { StatusTitle: "需要保存认证信息" },
                    "页面交互改变了认证决策投影");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("认证页回归：打开只附着一次且只加载固定门户根地址", async () =>
        {
            var host = new FakeAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            Require(host.AttachCount == 1, "首次打开未恰好附着一次：" + host.AttachCount);
            Require(host.Addresses.Count == 1 && host.Addresses[0] == new Uri(AuthPageModel.PortalAddress),
                "宿主未加载固定门户根地址");
            Require(Settings.IsSupportedPortal(host.Addresses[0]), "宿主地址不符合固定门户约束");
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.Attached && coordinator.Current.HostAvailable,
                "附着成功后页面未投影为可用");

            // 普通状态刷新、切页与最小化都不再调用附着；重试在已附着时也不重新导航。
            for (var i = 0; i < 3; i++) coordinator.Update(Reading(Online()));
            coordinator.SetPageVisible(true);
            coordinator.SetWindowVisible(false);
            coordinator.SetWindowVisible(true);
            coordinator.SetPageVisible(false);
            await coordinator.EnsureAttachedAsync();
            await coordinator.RetryAsync();
            Require(host.AttachCount == 1, "普通刷新、切页或最小化触发了重复导航：" + host.AttachCount);
        }),
        ("认证页回归：普通刷新不重新导航且复用宿主表面", async () =>
        {
            var host = new FakeAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            var surface = coordinator.Surface;
            for (var i = 0; i < 5; i++) coordinator.Update(Reading(Online() with { Detail = "刷新 " + i }));
            Require(host.AttachCount == 1, "状态刷新触发了重新导航");
            Require(ReferenceEquals(surface, coordinator.Surface), "状态刷新替换了宿主表面");
        }),
        ("认证页回归：相同内容重复投影复用视图且切页不丢输入", () => Sync(() =>
        {
            // 验收条件 4 的真正机制在渲染门：指纹未变时跳过渲染，视图实例始终复用。
            var gate = new AuthPageRenderGate<FakeAuthView>();
            var content = AuthPageModel.From(Reading(Online()), AuthPageHostSnapshot.NotAttached);
            Require(gate.ShouldRender(content), "首次投影未触发渲染");
            var view = gate.View(() => new FakeAuthView());
            view.Render(content);
            view.Input = "已填写但未提交的测试内容";

            // 相同指纹重复投影：跳过渲染，实例与输入都不变。
            for (var i = 0; i < 3; i++)
            {
                if (gate.ShouldRender(content)) view.Render(content);
            }
            Require(view.Renders == 1, "相同内容重复投影仍重建了页面");
            Require(ReferenceEquals(view, gate.View(() => throw new Exception("相同内容不应重建视图"))),
                "相同内容重复投影替换了视图实例");
            Require(view.Input == "已填写但未提交的测试内容", "重复投影清空了已填内容");

            // 切页只清指纹、保留实例；返回后重新渲染同一实例，输入仍在。
            gate.Detach();
            Require(gate.ShouldRender(content), "切页返回后未重新渲染");
            Require(ReferenceEquals(view, gate.View(() => new FakeAuthView())), "切页返回后重建了视图实例");
            view.Render(content);
            Require(view.Input == "已填写但未提交的测试内容", "切页返回后丢失已填内容");

            // 内容变化：必须重新渲染。
            var changed = AuthPageModel.From(
                Reading(Online() with { State = "已认证但外网不可达", Direct = "外网不可达或未知", Portal = "已认证" }),
                AuthPageHostSnapshot.NotAttached);
            Require(changed.ContentKey != content.ContentKey, "夹具内容指纹未变化");
            Require(gate.ShouldRender(changed), "内容变化未触发重新渲染");
            view.Render(changed);
            Require(view.Renders == 3 && ReferenceEquals(view.Shown, changed), "内容变化后未渲染新内容");
        })),
        ("认证页回归：内嵌通道授予不被说成自动认证重试", () => Sync(() =>
        {
            // ADR-0015：通道授予只是启动意图，不是提交授权，也没有服务端已发布的重试节奏。
            var channel = Reading(Online() with
            {
                State = "认证状态未知", Wifi = "已连接", Direct = "外网不可达或未知", Portal = "未知，不自动认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready", AuthenticationFailure = "None",
                AuthChannelMode = AuthChannelModes.Automatic, AuthChannelRequested = true
            });
            var content = AuthPageModel.From(channel, AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.OutcomeUnknown, "通道授予未按结果未知投影：" + content.Phase);
            Require(content.StatusTitle == "认证结果待确认", "通道授予标题不符：" + content.StatusTitle);
            Require(!content.StatusExplanation.Contains("自动认证将在", StringComparison.Ordinal) &&
                !content.StatusExplanation.Contains("重试节奏", StringComparison.Ordinal),
                "通道授予被说成自动认证重试");
            Require(content.StatusExplanation.Contains("启动意图", StringComparison.Ordinal) &&
                content.StatusExplanation.Contains("不会提交凭据", StringComparison.Ordinal),
                "通道授予未说明启动意图与只读边界");

            var waiting = AuthPageModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready"
            }), AuthPageHostSnapshot.NotAttached);
            Require(waiting.Phase == AuthPagePhase.WaitingRetry, "对照快照未投影为等待重试");
            Require(content.StatusTitle != waiting.StatusTitle, "通道授予与等待重试未区分");
        })),
        ("认证页回归：状态投影与附着结果经界面线程调度后才生效", async () =>
        {
            var host = new FakeAuthHost();
            var pending = new Queue<Action>();
            using var coordinator = new AuthPageCoordinator(host, pending.Enqueue);
            var raised = 0;
            AuthPageContent? last = null;
            coordinator.Updated += content => { raised++; last = content; };

            var returned = coordinator.Update(Reading(Online()));
            Require(raised == 0, "状态投影未调度到界面线程就触发了 Updated");
            Require(pending.Count == 1, "状态投影未交给注入的界面线程调度器");
            Require(coordinator.Current.Phase == AuthPagePhase.Unconfirmed, "调度前内容已被改写");
            Require(returned.Phase == AuthPagePhase.Unconfirmed, "调度前返回了尚未应用的内容");

            pending.Dequeue()();
            Require(raised == 1 && last is { Phase: AuthPagePhase.Online }, "界面线程调度后未更新内容或未触发事件");
            Require(coordinator.Current.Phase == AuthPagePhase.Online, "界面线程调度后内容未应用");

            // 附着在线程池上完成时同样不能在后台线程改写状态或触发事件。
            var asyncHost = new AsyncFakeAuthHost();
            var attachQueue = new Queue<Action>();
            using var page = new AuthPageCoordinator(asyncHost, attachQueue.Enqueue);
            var attachRaised = 0;
            page.Updated += _ => attachRaised++;
            var attach = page.EnsureAttachedAsync();
            asyncHost.Complete();
            await attach;
            Require(attachRaised == 0, "附着完成未调度到界面线程就触发了事件");
            Require(attachQueue.Count > 0, "附着完成未交给界面线程调度器");
            while (attachQueue.Count > 0) attachQueue.Dequeue()();
            Require(attachRaised >= 1 && page.Current.HostStatus == AuthPageHostStatus.Attached,
                "界面线程调度后未投影附着结果");
        }),
        ("认证页回归：切页、最小化不增加导航且状态读取继续", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-auth-page-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                WriteStatus(path, Online());
                var host = new FakeAuthHost();
                var coordinator = new AuthPageCoordinator(host);
                var pipe = new StatusPipe(path, TimeSpan.FromMilliseconds(25));
                using var controller = new ShellController(pipe, authPage: coordinator);
                await controller.RefreshStatusAsync();
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(50);
                Require(host.AttachCount == 1, "打开认证页未附着一次");
                Require(controller.Content is AuthPageContent { Phase: AuthPagePhase.Online }, "认证页内容未应用");

                // 切走、最小化、恢复、切回：都不增加附着；状态读取仍可继续。
                await controller.SelectAsync(ShellPage.Overview);
                controller.SetWindowVisible(false);
                WriteStatus(path, Online() with { Detail = "读取继续" });
                await controller.RefreshStatusAsync();
                Require(controller.Status is { Fresh: true }, "最小化后状态读取停止");
                controller.SetWindowVisible(true);
                await controller.SelectAsync(ShellPage.Authentication);
                await Task.Delay(50);
                Require(host.AttachCount == 1, "切页或最小化触发了重复附着：" + host.AttachCount);
                Require(controller.Content is AuthPageContent { Phase: AuthPagePhase.Online, StatusTitle: "已联网" },
                    "切回认证页后内容未应用");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("认证页回归：浏览器运行时缺失与网络边界不可用均为受控提示", async () =>
        {
            var missing = new UnavailableAuthPageBrowserHost(() => false);
            using var missingPage = new AuthPageCoordinator(missing);
            missingPage.Update(Reading(Online()));
            await missingPage.EnsureAttachedAsync();
            Require(missing.Snapshot.Status == AuthPageHostStatus.RuntimeMissing, "运行时缺失未映射为受控状态");
            Require(missing.Surface is null, "运行时缺失仍创建了浏览器表面");
            Require(missingPage.Current.HostRetryVisible, "运行时缺失未提供手动重试入口");
            Require(missingPage.Current.HostDetail.Contains("WebView2", StringComparison.Ordinal), "运行时缺失提示缺少运行时名称");

            var boundary = new UnavailableAuthPageBrowserHost(() => true);
            using var boundaryPage = new AuthPageCoordinator(boundary);
            boundaryPage.Update(Reading(Online()));
            await boundaryPage.EnsureAttachedAsync();
            Require(boundary.Snapshot.Status == AuthPageHostStatus.NetworkBoundaryUnavailable, "边界未就绪未失败关闭");
            Require(boundary.Surface is null, "边界未就绪仍创建了浏览器表面");
            Require(boundaryPage.Current.HostDetail.Contains("不会连接门户", StringComparison.Ordinal), "边界不可用提示不符");
            Require(boundaryPage.Current.HostDetail.Contains("不会回退直连", StringComparison.Ordinal), "边界未就绪未声明不回退直连");
        }),
        ("认证页回归：宿主加载失败受控降级且只能手动重试", async () =>
        {
            var host = new FakeAuthHost { AttachThrows = true };
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            Require(host.AttachCount == 1, "首次失败未记录一次尝试");
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.Failed, "宿主失败未投影为受控失败");
            Require(coordinator.Current.HostRetryVisible, "宿主失败未提供手动重试");
            await coordinator.EnsureAttachedAsync();
            Require(host.AttachCount == 1, "失败后在轮询中自动重试了宿主");

            host.AttachThrows = false;
            await coordinator.RetryAsync();
            Require(host.AttachCount == 2, "手动重试未重新附着");
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.Attached, "手动重试后未恢复");
        }),
        ("认证页回归：附着后宿主状态变化立即重新投影", async () =>
        {
            var host = new FakeAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.Attached, "初始附着状态不符");

            host.Simulate(new(AuthPageHostStatus.RuntimeMissing, AuthPageModel.PortalAddress, false, "夹具：运行时缺失。"));
            coordinator.Update(Reading(Online()));
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.RuntimeMissing, "宿主状态变化未重新投影");
            Require(coordinator.Current.HostRetryVisible && !coordinator.Current.HostAvailable, "降级状态未提供重试或仍标记可用");

            // 受控失败覆盖不能压住之后的新宿主状态。
            var failing = new FakeAuthHost { AttachThrows = true };
            using var failedPage = new AuthPageCoordinator(failing);
            failedPage.Update(Reading(Online()));
            await failedPage.EnsureAttachedAsync();
            Require(failedPage.Current.HostStatus == AuthPageHostStatus.Failed, "首次失败未投影");
            failing.Simulate(new(AuthPageHostStatus.NetworkBoundaryUnavailable, AuthPageModel.PortalAddress, false, "夹具：边界未接入。"));
            failedPage.Update(Reading(Online()));
            Require(failedPage.Current.HostStatus == AuthPageHostStatus.NetworkBoundaryUnavailable, "旧失败投影覆盖了新宿主状态");
        }),
        ("认证页回归：后台协议认证只投影状态且不声称网页操作", () => Sync(() =>
        {
            var protocol = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                AuthChannelMode = AuthChannelModes.ManualOnly, AuthChannelRequested = false
            });
            var protocolContent = AuthPageModel.From(protocol, AuthPageHostSnapshot.NotAttached);
            Require(protocolContent.BackgroundNote.Contains("协议认证", StringComparison.Ordinal), "协议认证说明缺失");
            Require(protocolContent.BackgroundNote.Contains("不代表发生过网页操作", StringComparison.Ordinal),
                "协议认证被误称为网页操作");

            var channel = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                AuthChannelMode = AuthChannelModes.Automatic, AuthChannelRequested = true
            });
            var channelContent = AuthPageModel.From(channel, AuthPageHostSnapshot.NotAttached);
            Require(channelContent.BackgroundNote.Contains("不会另起", StringComparison.Ordinal), "通道状态未声明不另起会话");
        })),
        ("认证页回归：宿主抽象不提供填表、脚本或提交入口", () => Sync(() =>
        {
            var methods = typeof(IAuthPageBrowserHost).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(method => method.Name).ToArray();
            foreach (var forbidden in new[] { "Submit", "Navigate", "Script", "Fill" })
                Require(!methods.Any(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)),
                    "认证页宿主抽象包含提交类入口：" + forbidden);
            Require(methods.Contains(nameof(IAuthPageBrowserHost.AttachAsync)), "宿主抽象缺少受控附着入口");
            Require(methods.Contains(nameof(IAuthPageBrowserHost.Refresh)), "宿主抽象缺少会话核对入口");
            Require(methods.Contains(nameof(IAuthPageBrowserHost.SetVisible)), "宿主抽象缺少可见性入口");
        })),
        ("认证页回归：校园 Wi-Fi 未就绪时不声称继续只读复检", () => Sync(() =>
        {
            var pending = Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending", AuthenticationFailure = "None"
            };
            foreach (var wifi in new[] { "未连接", "已关联，等待 IPv4", "正在连接", "网卡未就绪" })
            {
                var content = AuthPageModel.From(Reading(pending with { Wifi = wifi }), AuthPageHostSnapshot.NotAttached);
                Require(content.Phase == AuthPagePhase.OutcomeUnknown, wifi + "：待消解提交未投影为结果未知");
                Require(content.StatusExplanation.Contains("暂不发起只读复检", StringComparison.Ordinal),
                    wifi + "：未说明复检已暂停：" + content.StatusExplanation);
                Require(!content.StatusExplanation.Contains("后台继续只读复检", StringComparison.Ordinal) &&
                    !content.BackgroundNote.Contains("后台继续只读复检", StringComparison.Ordinal),
                    wifi + "：Wi-Fi 未就绪仍声称继续只读复检");
            }

            var exhausted = AuthPageModel.From(Reading(pending with
            {
                Wifi = "未连接",
                Detail = "已停止自动提交：首次提交后的两次额外额度已用尽；继续低频只读复检。"
            }), AuthPageHostSnapshot.NotAttached);
            Require(exhausted.Phase == AuthPagePhase.NeedsManual && exhausted.StatusTitle == "提交额度已用尽",
                "额度用尽投影不符");
            Require(!exhausted.StatusExplanation.Contains("后台只做低频只读复检", StringComparison.Ordinal) &&
                !exhausted.BackgroundNote.Contains("后台自动提交额度已用尽，只做低频只读复检", StringComparison.Ordinal),
                "Wi-Fi 未就绪的额度耗尽仍声称正在复检");

            var storage = AuthPageModel.From(Reading(pending with
            {
                Wifi = "未连接", SubmissionResolutionStorageFault = true
            }), AuthPageHostSnapshot.NotAttached);
            Require(!storage.StatusExplanation.Contains("后台继续只读复检", StringComparison.Ordinal) &&
                !storage.BackgroundNote.Contains("后台继续只读复检", StringComparison.Ordinal),
                "Wi-Fi 未就绪的存储故障仍声称继续复检");

            var paused = AuthPageModel.From(Reading(Online() with
            {
                Enabled = false, State = "已暂停恢复", Wifi = "未连接", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending", AuthenticationFailure = "None",
                Detail = "自动恢复已暂停；仅检测网络，不重连或认证。"
            }), AuthPageHostSnapshot.NotAttached);
            Require(!paused.StatusExplanation.Contains("后台只做只读复检", StringComparison.Ordinal) &&
                !paused.BackgroundNote.Contains("由只读复检处理", StringComparison.Ordinal),
                "Wi-Fi 未就绪的暂停状态仍声称正在复检");

            // 对照：Wi-Fi 已连接且提交待决时，复检确实在运行。
            var active = AuthPageModel.From(Reading(pending with { Wifi = "已连接" }), AuthPageHostSnapshot.NotAttached);
            Require(active.StatusExplanation.Contains("后台继续只读复检", StringComparison.Ordinal) &&
                active.BackgroundNote.Contains("后台按共享提交额度继续只读复检", StringComparison.Ordinal),
                "Wi-Fi 就绪时丢失复检说明");
        })),
        ("认证页回归：Wi-Fi 重连退避不被说成自动认证重试", () => Sync(() =>
        {
            var reconnect = Reading(Online() with
            {
                State = "正在恢复", Wifi = "未连接", Direct = "外网不可达或未知", Portal = "未知，不自动认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready", AuthenticationFailure = "None",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(30), NextAttemptAction = "ReconnectWifi"
            });
            Require(AuthPageModel.PhaseFor(reconnect) != AuthPagePhase.WaitingRetry,
                "Wi-Fi 重连退避被当成自动认证重试");

            // 旧版状态没有动作种类：未知来源的退避时间同样不能当成认证重试。
            var legacy = Reading(Online() with
            {
                State = "正在恢复", Wifi = "未连接", Direct = "外网不可达或未知", Portal = "未知，不自动认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready", AuthenticationFailure = "None",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(30)
            });
            Require(AuthPageModel.PhaseFor(legacy) != AuthPagePhase.WaitingRetry,
                "缺少动作种类的旧状态被当成认证重试");

            var auth = Reading(Online() with
            {
                State = "认证状态未知", Wifi = "已连接", Direct = "外网不可达或未知", Portal = "未知，不自动认证",
                CredentialsBlocked = false, AuthenticationPersistentState = "Ready", AuthenticationFailure = "None",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(30), NextAttemptAction = "Authenticate"
            });
            Require(AuthPageModel.PhaseFor(auth) == AuthPagePhase.WaitingRetry,
                "明确认证退避未投影为等待重试");
        })),
        ("认证页回归：状态 Detail 缺失不使投影抛异常", () => Sync(() =>
        {
            var content = AuthPageModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                CredentialsBlocked = true, AuthenticationPersistentState = "SubmissionPending", AuthenticationFailure = "None",
                Detail = null!
            }), AuthPageHostSnapshot.NotAttached);
            Require(content.Phase == AuthPagePhase.OutcomeUnknown, "Detail 缺失时未按提交结果未知投影");
            Require(content.ContentKey.Length > 0 && content.BackgroundNote.Length > 0, "Detail 缺失时投影为空");
        })),
        ("认证页会话回归：通道会话进行中真实宿主不创建第二个浏览器", async () =>
        {
            var sessionActive = false;
            var probes = 0;
            var links = 0;
            var proxies = 0;
            using var host = new AuthPageWebViewHost(
                () => { links++; return Link(); },
                () => { probes++; return true; },
                (link, job) => { proxies++; return AuthChannelProxy.StartAsync(link, job); },
                () => sessionActive);
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            // 夹具没有原生表面：边界就绪后进入 Attaching，只有导航成功才会变成 Attached。
            Require(host.Snapshot.Status == AuthPageHostStatus.Attaching, "首次附着未进入加载中：" + host.Snapshot.Status);
            Require(proxies == 1, "首次附着未启动代理");

            // 后台内嵌通道会话开始：本页拆掉自己的代理与浏览器，只投影会话状态。
            sessionActive = true;
            coordinator.Update(Reading(Online()));
            Require(host.Snapshot.Status == AuthPageHostStatus.ChannelSessionActive,
                "通道会话进行中未投影会话状态：" + host.Snapshot.Status);
            Require(coordinator.Current is
                { HostStatus: AuthPageHostStatus.ChannelSessionActive, HostTitle: "内嵌通道会话进行中" },
                "页面未投影会话状态：" + coordinator.Current.HostTitle);
            Require(!coordinator.Current.HostAvailable && !coordinator.Current.HostRetryVisible,
                "会话状态被当成已附着或可重试失败");

            // 会话进行中再次附着：既不检查运行时、不读网卡，也不启动第二个代理。
            var (probesBefore, linksBefore, proxiesBefore) = (probes, links, proxies);
            await coordinator.RetryAsync();
            Require(host.Snapshot.Status == AuthPageHostStatus.ChannelSessionActive, "会话进行中重试改写了宿主状态");
            Require(probes == probesBefore && links == linksBefore && proxies == proxiesBefore,
                "会话进行中仍检查运行时、读取网卡或启动代理");

            // 会话结束：宿主回到未附着，协调器只恢复一次只读附着，不重复重建。
            sessionActive = false;
            coordinator.Update(Reading(Online()));
            await WaitUntilAsync(() => host.Snapshot.Status == AuthPageHostStatus.Attaching);
            Require(host.Snapshot.Status == AuthPageHostStatus.Attaching, "会话结束后未恢复只读附着：" + host.Snapshot.Status);
            Require(proxies == proxiesBefore + 1, "会话结束后未重建唯一代理：" + proxies);
            coordinator.Update(Reading(Online()));
            // 否定断言：留出足够时间让错误的重建暴露出来。
            await Task.Delay(50);
            Require(proxies == proxiesBefore + 1, "重复刷新重建了代理：" + proxies);
        }),
        ("认证页会话回归：迟到的边界准备不覆盖新会话的代理与状态", async () =>
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            AuthChannelProxy? late = null;
            AuthChannelProxy? current = null;
            using var host = new AuthPageWebViewHost(Link, () => true, async (link, job) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await gate.Task;
                    return late = await AuthChannelProxy.StartAsync(link, job);
                }
                return current = await AuthChannelProxy.StartAsync(link, job);
            }, () => false);

            var first = host.AttachAsync(new Uri(AuthPageModel.PortalAddress));
            await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
            var second = host.AttachAsync(new Uri(AuthPageModel.PortalAddress));
            await WaitUntilAsync(() => host.Snapshot.Status == AuthPageHostStatus.Attaching
                && Volatile.Read(ref calls) == 2);
            gate.SetResult(true);
            await Task.WhenAll(first, second);

            Require(late is not null && current is not null, "两次附着未各自启动代理");
            Require(!late!.IsRunning, "迟到的边界准备的代理未被拆除");
            Require(current!.IsRunning, "新会话的代理被旧尝试停止或覆盖");
            Require(host.Snapshot.Status == AuthPageHostStatus.Attaching, "迟到的尝试改写了新会话状态");
        }),
        ("认证页会话回归：受控附着失败后不因会话结束自动重试", async () =>
        {
            var sessionActive = false;
            var probes = 0;
            using var host = new AuthPageWebViewHost(
                Link,
                () => { probes++; return false; },
                (link, job) => throw new InvalidOperationException("运行时缺失时不应启动代理"),
                () => sessionActive);
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            await coordinator.EnsureAttachedAsync();
            Require(host.Snapshot.Status == AuthPageHostStatus.RuntimeMissing,
                "受控失败未进入运行时缺失：" + host.Snapshot.Status);
            Require(probes == 1, "受控失败未检查运行时");

            // 通道会话开始再结束：受控失败仍要求用户显式重试，不能被会话结束顺手重试。
            sessionActive = true;
            coordinator.Update(Reading(Online()));
            Require(host.Snapshot.Status == AuthPageHostStatus.ChannelSessionActive, "会话开始未投影会话状态");
            sessionActive = false;
            coordinator.Update(Reading(Online()));
            Require(host.Snapshot.Status == AuthPageHostStatus.NotAttached,
                "会话结束未回到未附着：" + host.Snapshot.Status);
            Require(probes == 1, "会话结束自动重试了受控失败的附着：" + probes);
        }),
        ("认证页会话回归：从未打开过的页面不因后台会话凭空附着", async () =>
        {
            var host = new SessionAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            host.SessionActive = true;
            coordinator.Update(Reading(Online()));
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.ChannelSessionActive, "未打开页面未投影会话状态");
            Require(host.AttachCount == 0, "未打开页面在会话中创建了浏览器");

            host.SessionActive = false;
            coordinator.Update(Reading(Online()));
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.NotAttached,
                "会话结束后为未打开的页面凭空附着：" + coordinator.Current.HostStatus);
            Require(host.AttachCount == 0, "会话结束后为未打开的页面启动了浏览器");

            await coordinator.EnsureAttachedAsync();
            Require(host.AttachCount == 1 && coordinator.Current.HostStatus == AuthPageHostStatus.Attached,
                "打开页面后未正常附着");
        }),
        ("认证页会话回归：会话期间切页与最小化不改变会话投影", () => Sync(() =>
        {
            var host = new SessionAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(Reading(Online()));
            host.SessionActive = true;
            coordinator.Update(Reading(Online()));
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.ChannelSessionActive, "会话状态未投影");
            var key = coordinator.Current.ContentKey;

            coordinator.SetPageVisible(false);
            coordinator.SetWindowVisible(false);
            coordinator.Update(Reading(Online()));
            coordinator.SetWindowVisible(true);
            coordinator.SetPageVisible(true);
            Require(coordinator.Current.HostStatus == AuthPageHostStatus.ChannelSessionActive,
                "切页或最小化改变了会话投影");
            Require(coordinator.Current.ContentKey == key, "切页或最小化改变了页面内容");
            Require(host.VisibleCalls == 4, "可见性没有完整传给宿主：" + host.VisibleCalls);
        })),
        ("认证页会话回归：会话状态与协议认证的说明互不冒充", async () =>
        {
            var copy = AuthPageModel.HostCopyFor(AuthPageHostStatus.ChannelSessionActive);
            Require(copy.Title == "内嵌通道会话进行中", "会话状态标题不符：" + copy.Title);
            Require(copy.Detail.Contains("不会另起", StringComparison.Ordinal), "会话状态未声明不另起会话");
            Require(!copy.Detail.Contains("已加载", StringComparison.Ordinal), "会话状态被说成已加载页面");

            var content = AuthPageModel.From(Reading(Online()),
                new(AuthPageHostStatus.ChannelSessionActive, AuthPageModel.PortalAddress, false, ""));
            Require(content.HostTitle == copy.Title && content.HostDetail == copy.Detail, "会话状态文案未走单一来源");
            Require(!content.HostAvailable && !content.HostRetryVisible, "会话状态被当成已附着或可重试失败");
            Require(content.BackgroundNote.Contains("内嵌认证通道会话进行中", StringComparison.Ordinal) &&
                !content.BackgroundNote.Contains("按协议认证运行", StringComparison.Ordinal),
                "通道会话进行中仍声称后台按协议认证运行：" + content.BackgroundNote);

            // 协议载体：后台按协议认证运行，页面只投影状态，不声称发生过网页操作。
            var protocolReading = Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证",
                AuthChannelMode = AuthChannelModes.ManualOnly, AuthChannelRequested = false
            });
            var protocol = AuthPageModel.From(protocolReading, AuthPageHostSnapshot.NotAttached);
            Require(protocol.BackgroundNote.Contains("协议认证", StringComparison.Ordinal) &&
                protocol.BackgroundNote.Contains("不代表发生过网页操作", StringComparison.Ordinal),
                "协议认证说明缺失或被改成网页操作");

            // 两条载体的附着与投影差异：协议认证下页面照常附着固定门户地址，但仍只投影状态。
            var host = new FakeAuthHost();
            using var coordinator = new AuthPageCoordinator(host);
            coordinator.Update(protocolReading);
            await coordinator.EnsureAttachedAsync();
            Require(host.AttachCount == 1 && coordinator.Current.HostStatus == AuthPageHostStatus.Attached,
                "协议认证下页面未附着固定门户地址");
            Require(host.Addresses is [{ } address] && address.ToString() == AuthPageModel.PortalAddress,
                "协议认证下页面附着了其他地址");
            Require(coordinator.Current.BackgroundNote.Contains("不代表发生过网页操作", StringComparison.Ordinal) &&
                !coordinator.Current.BackgroundNote.Contains("内嵌认证通道会话进行中", StringComparison.Ordinal),
                "协议载体被说成通道会话或网页操作：" + coordinator.Current.BackgroundNote);
        })
    ];

    private static StatusReading Reading(StatusSnapshot? snapshot, TimeSpan? age = null)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveAge = snapshot is null ? (TimeSpan?)null : age ?? now - snapshot.UpdatedAt;
        var valid = StatusSnapshotReader.IsValid(snapshot);
        return new StatusReading(snapshot, NetworkDisplayMap.Map(snapshot, effectiveAge), valid,
            valid && NetworkDisplayMap.IsFresh(effectiveAge), effectiveAge, now);
    }

    /// <summary>已就绪的校园网卡夹具：只含文档地址，不触碰真实网卡。</summary>
    private static WifiLink Link() =>
        new(true, null, IPAddress.Parse("10.0.0.2"), 7, [IPAddress.Parse("10.0.0.1")]);

    private static StatusSnapshot Online() => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow,
        Enabled = true,
        State = "校园网在线",
        Wifi = "已连接",
        Direct = "外网可达",
        Portal = "未查询（外网检查已通过）",
        Tunnel = "系统路径可达，出口未核验",
        Uu = "未核验",
        AuthChannelMode = AuthChannelModes.ManualOnly
    };

    private static void WriteStatus(string path, StatusSnapshot snapshot) =>
        Storage.AtomicWrite(path, JsonSerializer.Serialize(snapshot, Storage.Json));

    private sealed class FakeAuthHost : IAuthPageBrowserHost
    {
        private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;

        public object? Surface { get; } = new object();

        public AuthPageHostSnapshot Snapshot => snapshot;

        public bool AttachThrows { get; set; }

        public int AttachCount { get; private set; }

        public List<Uri> Addresses { get; } = [];

        public void Simulate(AuthPageHostSnapshot value) => snapshot = value;

        public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default)
        {
            AttachCount++;
            if (AttachThrows)
            {
                snapshot = new(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false, "夹具：宿主加载失败。");
                throw new InvalidOperationException("夹具：宿主加载失败。");
            }
            Addresses.Add(portal);
            snapshot = new(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true, "夹具：已附着固定门户地址。");
            return Task.FromResult(snapshot);
        }

        public void SetVisible(bool visible) { }

        /// <summary>本夹具没有后台会话：核对不改变宿主状态。</summary>
        public void Refresh() { }

        public void Dispose() { }
    }

    /// <summary>
    /// 模拟后台内嵌认证通道的假宿主（工单 #39）：会话开始时丢弃本页的附着状态，只保留
    /// “内嵌通道会话进行中”；会话结束时回到未附着，由协调器决定是否恢复只读附着。
    /// </summary>
    private sealed class SessionAuthHost : IAuthPageBrowserHost
    {
        private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;

        public bool SessionActive { get; set; }

        public int AttachCount { get; private set; }

        public int VisibleCalls { get; private set; }

        public object? Surface { get; } = new object();

        public AuthPageHostSnapshot Snapshot => snapshot;

        public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default)
        {
            AttachCount++;
            snapshot = SessionActive ? SessionSnapshot() : AttachedSnapshot();
            return Task.FromResult(snapshot);
        }

        public void Refresh()
        {
            if (SessionActive) snapshot = SessionSnapshot();
            else if (snapshot.Status == AuthPageHostStatus.ChannelSessionActive) snapshot = AuthPageHostSnapshot.NotAttached;
        }

        public void SetVisible(bool visible) => VisibleCalls++;

        public void Dispose() { }

        private static AuthPageHostSnapshot SessionSnapshot() => AuthPageHostSnapshot.ChannelSession();

        private static AuthPageHostSnapshot AttachedSnapshot() => new(
            AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true, "夹具：已附着固定门户地址。");
    }

    /// <summary>附着在线程池上异步完成的假宿主：用于验证协调器把结果调度回界面线程。</summary>
    private sealed class AsyncFakeAuthHost : IAuthPageBrowserHost
    {
        private readonly TaskCompletionSource<AuthPageHostSnapshot> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;

        public object? Surface { get; } = new object();

        public AuthPageHostSnapshot Snapshot => snapshot;

        public void Complete()
        {
            snapshot = new(AuthPageHostStatus.Attached, AuthPageModel.PortalAddress, true, "夹具：异步附着完成。");
            completion.SetResult(snapshot);
        }

        public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default) =>
            completion.Task;

        public void SetVisible(bool visible) { }

        public void Refresh() { }

        public void Dispose() { }
    }

    /// <summary>渲染门的假视图：只记录渲染次数与最后一次内容，并保留“已填输入”。</summary>
    private sealed class FakeAuthView
    {
        public int Renders { get; private set; }

        public AuthPageContent? Shown { get; private set; }

        public string Input { get; set; } = "";

        public void Render(AuthPageContent content)
        {
            Renders++;
            Shown = content;
        }
    }

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    /// <summary>
    /// 等待条件成立（最多约 2 秒）：会话结束后的恢复附着是 fire-and-forget，
    /// 不能用固定睡眠赌时序，否则测试会随机器负载抖动。
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
    }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }
}
