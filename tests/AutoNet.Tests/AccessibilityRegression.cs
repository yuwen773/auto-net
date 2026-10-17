using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 可访问性与缩放回归（迁移工单 #14 / T06 / 故事 24、31、33）：
/// 可访问名称单一来源（ShellAccessibility）随状态更新、携带当前事实且不含敏感内容；
/// 图标与文字冗余表达覆盖全部语义分类（颜色不作唯一信息通道）。
/// 受控 UI 证据（键盘驱动、受控缩放与真实窗口截图）由 scripts/qa-avalonia-accessibility.ps1 提供；
/// 真实 Windows DPI 与系统键盘交互另行分列验收，不以模拟值补为通过。
/// </summary>
internal static class AccessibilityRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("可访问性回归：恢复开关可访问名称随状态更新且携带明确意图", () => Sync(() =>
        {
            var on = ShellAccessibility.RecoveryToggle(OverviewModel.RecoverySwitch(Reading(Online(), TimeSpan.Zero)));
            var off = ShellAccessibility.RecoveryToggle(OverviewModel.RecoverySwitch(Reading(Online() with { Enabled = false }, TimeSpan.Zero)));
            var unknown = ShellAccessibility.RecoveryToggle(OverviewModel.RecoverySwitch(null));

            Require(on.Contains("已开启") && on.Contains("关闭自动恢复"), "已开启名称缺状态或意图：" + on);
            Require(off.Contains("已关闭") && off.Contains("开启自动恢复"), "已关闭名称缺状态或意图：" + off);
            Require(unknown.Contains("待确认") && unknown.Contains("自动恢复状态待确认"), "待确认名称不符：" + unknown);
            Require(on != off && off != unknown && on != unknown, "名称未随状态区分");
        })),
        ("可访问性回归：解除入口名称携带当前认证事实", () => Sync(() =>
        {
            var suspended = OverviewModel.From(Reading(Online() with
            {
                CredentialsBlocked = true,
                AuthenticationFailure = "CredentialsRejected",
                AuthenticationPersistentState = "CredentialsRejected"
            }, TimeSpan.Zero));
            Require(suspended.Unblock.Visible, "夹具应产生可见的解除入口");
            var name = ShellAccessibility.Unblock(suspended);
            Require(name.Contains("认证提交已暂停"), "解除名称未携带当前认证事实：" + name);
            Require(name.EndsWith(suspended.Unblock.Label, StringComparison.Ordinal), "解除名称未携带动作标签：" + name);

            // 门户结构疑似变化时事实随之变化：名称与可见状态一致（故事 33）。
            var layoutSuspected = OverviewModel.From(Reading(Online() with
            {
                PortalLayoutSuspected = true,
                CredentialsBlocked = false
            }, TimeSpan.Zero));
            var suspectedName = ShellAccessibility.Unblock(layoutSuspected);
            Require(suspectedName != name && suspectedName.Contains("门户结构疑似变化"), "解除名称未随认证事实更新：" + suspectedName);
        })),
        ("可访问性回归：兜底开页入口名称固定声明边界并携带动作", () => Sync(() =>
        {
            var content = OverviewModel.From(Reading(Online() with
            {
                BrowserFallbackRequested = true,
                BrowserFallbackNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1)
            }, TimeSpan.Zero));
            Require(content.PortalOpen.Visible, "夹具应产生可见的兜底开页入口");
            var name = ShellAccessibility.PortalOpen(content.PortalOpen);
            Require(name.Contains("需要浏览器认证") && name.EndsWith(content.PortalOpen.Label, StringComparison.Ordinal),
                "兜底开页名称不符：" + name);
        })),
        ("可访问性回归：导出入口名称随运行状态如实反映", () => Sync(() =>
        {
            var idle = ShellAccessibility.Export(DiagnosticsExportState.Initial);
            var running = ShellAccessibility.Export(DiagnosticsExportState.Initial with
            {
                Phase = DiagnosticsExportPhase.Exporting,
                Message = "正在采集（受控夹具描述）"
            });
            Require(idle == "导出诊断包…", "空闲导出入口名称不符：" + idle);
            Require(running == "诊断包导出（进行中）", "进行中导出入口名称不符：" + running);
            Require(idle != running, "导出名称未随运行状态区分");
        })),
        ("可访问性回归：导航项可访问名称与页面可见名称一致", () => Sync(() =>
        {
            foreach (var info in ShellPages.All)
                Require(ShellAccessibility.NavItem(info.Page) == info.Name, "导航项名称与页面名不一致：" + info.Page);
        })),
        ("可访问性回归：图标与文字冗余表达覆盖全部语义分类（故事 24）", () => Sync(() =>
        {
            foreach (var page in Enum.GetValues<ShellPage>())
                Require(ShellIcons.For(page).Length > 0, "缺少导航图标：" + page);
            foreach (var state in Enum.GetValues<NetworkPrimaryState>())
                Require(ShellIcons.StatusGlyph(state).Length > 0, "缺少主状态图标：" + state);
            foreach (var kind in Enum.GetValues<OverviewAuthKind>())
                Require(ShellIcons.AuthColumnGlyph(kind).Length > 0, "缺少认证列图标：" + kind);
            foreach (var tone in Enum.GetValues<OverviewTone>())
                Require(ShellIcons.ToneGlyph(tone).Length > 0, "缺少诊断 tone 图标：" + tone);
        })),
        ("可访问性回归：可访问名称只含受控词表文本，不含凭据或地址", () => Sync(() =>
        {
            var suspended = OverviewModel.From(Reading(Online() with
            {
                CredentialsBlocked = true,
                LastError = "网关登录失败：https://login.csust.edu.cn:802/eportal/portal/login?user_password=fixture-secret",
                AuthenticationStage = "Submission",
                AuthenticationFailure = "CredentialsRejected",
                AuthenticationPersistentState = "CredentialsRejected"
            }, TimeSpan.Zero));
            var portalEntry = OverviewModel.PortalEntry(true, Online() with
            {
                BrowserFallbackRequested = true,
                BrowserFallbackNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1)
            }, null, false);
            string[] produced =
            [
                ShellAccessibility.RecoveryToggle(suspended.Recovery),
                ShellAccessibility.Unblock(suspended),
                ShellAccessibility.PortalOpen(portalEntry),
                ShellAccessibility.Export(DiagnosticsExportState.Initial),
                ShellAccessibility.NavItem(ShellPage.Overview),
                ShellAccessibility.NavItem(ShellPage.Diagnostics),
                ShellAccessibility.NavItem(ShellPage.Logs),
                ShellAccessibility.NavItem(ShellPage.Settings),
                ShellAccessibility.LogsDates, ShellAccessibility.LogsFilter,
                ShellAccessibility.LogsList, ShellAccessibility.LogsDetail,
                ShellAccessibility.StatusStrip, ShellAccessibility.TechnicalDetail
            ];
            foreach (var text in produced)
            {
                foreach (var forbidden in new[] { "http", "user_password", "user_account", "cookie", "phpsessid", "fixture-secret", ":" , "//" })
                    Require(!text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"可访问名称含受限内容（{forbidden}）：{text}");
            }
        }))
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

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
