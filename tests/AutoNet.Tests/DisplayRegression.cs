using AutoNet.Core;

internal static class DisplayRegression
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("展示映射：服务状态字符串全部有映射且未知状态不会显示为在线", () =>
        {
            foreach (var state in NetworkDisplayMap.KnownStates)
            {
                var display = NetworkDisplayMap.Map(Snapshot(state), Fresh);
                Require(display.Primary != NetworkPrimaryState.Unrecognized, $"未覆盖状态：{state}");
                Require(display.Title.Length > 0 && display.Explanation.Length > 0 && display.NextStep.Length > 0, $"文案不完整：{state}");
            }
            var unknown = NetworkDisplayMap.Map(Snapshot("某种尚未发布的状态"), Fresh);
            Require(unknown.Primary == NetworkPrimaryState.Unrecognized, "未知状态必须报告为未识别");
            Require(!unknown.IsHealthy, "未知状态绝不能呈现为健康");
            Require(unknown.Title == "未识别的服务状态");
            Require(unknown.Explanation.Contains("不会把它当作正常"));
            return Task.CompletedTask;
        }),
        ("展示映射：十一种主状态逐一可复现", () =>
        {
            var expected = new (NetworkPrimaryState Primary, StatusSnapshot? Snapshot)[]
            {
                (NetworkPrimaryState.Online, Snapshot("校园网在线") with { Direct = "外网可达" }),
                (NetworkPrimaryState.Reconnecting, Snapshot("正在重连校园 Wi-Fi")),
                (NetworkPrimaryState.NeedsAuthentication, Snapshot("需要处理认证")),
                (NetworkPrimaryState.AuthenticatedOffline, Snapshot("已认证但外网不可达")),
                (NetworkPrimaryState.AuthenticationUnknown, Snapshot("认证状态未知")),
                (NetworkPrimaryState.InterfaceUnavailable, Snapshot("网卡未就绪")),
                (NetworkPrimaryState.WaitingForAddress, Snapshot("等待网络地址")),
                (NetworkPrimaryState.Associating, Snapshot("Wi-Fi 正在连接")),
                (NetworkPrimaryState.OtherProfileConnected, Snapshot("已切换其他 Wi-Fi")),
                (NetworkPrimaryState.SettingsMissing, Snapshot("等待设置")),
                (NetworkPrimaryState.ProbeFailed, Snapshot("检测异常")),
                (NetworkPrimaryState.RecoveryPaused, Snapshot("已暂停恢复")),
                (NetworkPrimaryState.RecoveryPaused, Snapshot("认证状态未知") with { Enabled = false }),
                (NetworkPrimaryState.BindingRejected, Snapshot("需要处理认证") with { AuthenticationFailure = "BindingRejected" }),
                (NetworkPrimaryState.DisconnectQuotaExhausted, Snapshot("认证状态未知") with { DisconnectAttempts = 3, DisconnectBudgetMax = 3 }),
                (NetworkPrimaryState.DisconnectQuotaExhausted, Snapshot("已认证但外网不可达") with { DisconnectAttempts = 3, DisconnectBudgetMax = 3 }),
                (NetworkPrimaryState.Unrecognized, null)
            };
            foreach (var (primary, snapshot) in expected)
            {
                var display = NetworkDisplayMap.Map(snapshot, Fresh);
                Require(display.Primary == primary, $"期望 {primary}，实际 {display.Primary}（{snapshot?.State ?? "null"}）");
            }
            return Task.CompletedTask;
        }),
        ("展示映射：在线与认证暂停各自独立呈现", () =>
        {
            // Online but with authentication suspended must stay Online; suspension is a separate flag.
            var display = NetworkDisplayMap.Map(Snapshot("校园网在线") with { Direct = "外网可达", CredentialsBlocked = true }, Fresh);
            Require(display.Primary == NetworkPrimaryState.Online, "网络在线时主状态必须是在线");
            Require(display.Additional.HasFlag(NetworkAdditional.AuthenticationSuspended), "认证暂停必须独立呈现");
            Require(display.IsHealthy, "网络在线就是在线，不能因为认证暂停而改写主状态");

            var suspended = NetworkDisplayMap.Map(Snapshot("已暂停恢复") with { Enabled = false }, Fresh);
            Require(suspended.Additional.HasFlag(NetworkAdditional.RecoveryPaused));
            // Paused recovery while offline is the primary state, not merely a flag.
            var paused = NetworkDisplayMap.Map(Snapshot("认证状态未知") with { Enabled = false }, Fresh);
            Require(paused.Primary == NetworkPrimaryState.RecoveryPaused, "离线且暂停时主状态应为已暂停恢复");
            return Task.CompletedTask;
        }),
        ("展示映射：需要认证时文案跟随凭据阻断标记而非状态字符串", () =>
        {
            // "Needs authentication" and "credentials are blocked" are separate facts. Telling a user
            // their submission is suspended when AutoNet is about to submit is a false statement about
            // whether the machine will authenticate, so the copy has to follow the flag.
            var blocked = NetworkDisplayMap.Map(Snapshot("需要处理认证") with { CredentialsBlocked = true }, Fresh);
            Require(blocked.Primary == NetworkPrimaryState.NeedsAuthentication);
            Require(blocked.Explanation.Contains("暂停"), "凭据被阻断时应说明当前处于暂停状态");
            Require(blocked.Additional.HasFlag(NetworkAdditional.AuthenticationSuspended), "阻断时必须有认证暂停附加状态");

            var notBlocked = NetworkDisplayMap.Map(Snapshot("需要处理认证") with { CredentialsBlocked = false }, Fresh);
            Require(notBlocked.Primary == NetworkPrimaryState.NeedsAuthentication);
            Require(!notBlocked.Explanation.Contains("暂停"), "未被阻断时不得声称凭据提交已暂停");
            Require(!notBlocked.Additional.HasFlag(NetworkAdditional.AuthenticationSuspended), "未被阻断时不得出现认证暂停附加状态");
            return Task.CompletedTask;
        }),
        ("展示映射：出口未核验始终与在线并存", () =>
        {
            var display = NetworkDisplayMap.Map(Snapshot("校园网在线") with { Direct = "外网可达" }, Fresh);
            Require(display.Additional.HasFlag(NetworkAdditional.EgressUnverified), "在线不等于物理出口已核验");
            Require(display.Explanation.Contains("HTTPS 内容检查已通过"), "说明必须限定为内容检查");
            Require(!display.Explanation.Contains("出口"), "在线说明不得暗示出口已验证");
            return Task.CompletedTask;
        }),
        ("展示映射：认证未知不等于认证失败", () =>
        {
            var display = NetworkDisplayMap.Map(Snapshot("认证状态未知"), Fresh);
            Require(display.Primary == NetworkPrimaryState.AuthenticationUnknown);
            Require(display.Explanation.Contains("不等于认证失败"), "必须显式说明未知不等于失败");
            Require(!display.Title.Contains("失败") && !display.Title.Contains("错误"));
            return Task.CompletedTask;
        }),
        ("展示映射：状态过期与后台未就绪都不会呈现为健康", () =>
        {
            var stale = NetworkDisplayMap.Map(Snapshot("校园网在线") with { Direct = "外网可达" }, TimeSpan.FromSeconds(20));
            Require(!stale.IsHealthy, "过期状态不能是健康");
            Require(stale.Title == "后台状态已过期" && stale.TechnicalDetail.Contains("20 秒"));

            var missing = NetworkDisplayMap.Map(null, Fresh);
            Require(!missing.IsHealthy && missing.Title == "后台服务未就绪");
            Require(missing.TechnicalDetail == "未读取到 status.json。");
            return Task.CompletedTask;
        }),
        ("展示映射：附加状态可并存且互不覆盖", () =>
        {
            var display = NetworkDisplayMap.Map(Snapshot("认证状态未知") with
            {
                CredentialsBlocked = true,
                DisconnectAttempts = 3,
                DisconnectBudgetMax = 3,
                DisconnectRequestUnconfirmed = true,
                Tunnel = "本机代理不可达"
            }, Fresh);
            foreach (var flag in new[]
            {
                NetworkAdditional.AuthenticationSuspended, NetworkAdditional.DisconnectQuotaExhausted,
                NetworkAdditional.DisconnectUnconfirmed, NetworkAdditional.ProxyFault
            })
                Require(display.Additional.HasFlag(flag), $"缺少附加状态 {flag}");
            // The primary state still reports itself, and the rest surface alongside it.
            Require(display.Primary == NetworkPrimaryState.DisconnectQuotaExhausted);
            return Task.CompletedTask;
        }),
        ("展示映射：技术详情只含受控分类且不携带凭据", () =>
        {
            var display = NetworkDisplayMap.Map(Snapshot("需要处理认证") with
            {
                LastError = "网关登录失败：https://login.csust.edu.cn:802/eportal/portal/login?user_account=fixture&user_password=fixture-secret",
                AuthenticationStage = "Submission", AuthenticationFailure = "BindingRejected", AuthenticationHttpStatus = 401,
                CredentialRequestStarted = true, AuthenticationPersistentState = "BindingRejected"
            }, Fresh);
            Require(display.TechnicalDetail.Contains("认证阶段 Submission"));
            Require(display.TechnicalDetail.Contains("失败分类 BindingRejected"));
            Require(display.TechnicalDetail.Contains("HTTP 401"));
            Require(display.TechnicalDetail.Contains("已发起凭据请求"));
            foreach (var forbidden in new[] { "fixture-secret", "user_password", "user_account", "login.csust.edu.cn", "http" })
                Require(!display.TechnicalDetail.Contains(forbidden), $"技术详情泄露了 {forbidden}");
            Require(!display.Explanation.Contains("fixture-secret") && !display.NextStep.Contains("fixture-secret"));
            return Task.CompletedTask;
        }),
        ("展示映射：额度与冷却时间来自快照投影而非界面硬编码", () =>
        {
            // The tray must render whatever the service projected; a policy change must not need a UI edit.
            var snapshot = Snapshot("认证状态未知") with { DisconnectAttempts = 2, DisconnectBudgetMax = 7, DisconnectCooldownSeconds = 200 };
            Require(!NetworkDisplayMap.Map(snapshot, Fresh).Additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted),
                "上限来自快照：2/7 不应判定为耗尽");
            Require(NetworkDisplayMap.Map(snapshot with { DisconnectAttempts = 7 }, Fresh)
                .Additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted), "7/7 应判定为耗尽");

            // Defaults come from the policy constants, so a partial status.json still renders a sane cap.
            var partial = new StatusSnapshot();
            Require(partial.DisconnectBudgetMax == RecoveryPolicy.DisconnectAttemptCap);
            Require(partial.DisconnectCooldownSeconds == RecoveryPolicy.DisconnectCooldownSeconds);
            Require(partial.DisconnectResetAfterSeconds == RecoveryPolicy.OnlineResetSeconds);
            return Task.CompletedTask;
        })
    ];

    private static StatusSnapshot Snapshot(string state) => new()
    {
        UpdatedAt = DateTimeOffset.UtcNow, Enabled = true, State = state,
        Wifi = "已连接", Direct = "外网不可达或未知", Portal = "未知，不自动认证",
        Tunnel = "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）", Uu = "UU 服务运行；在线状态待核验"
    };

    private static void Require(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
}
