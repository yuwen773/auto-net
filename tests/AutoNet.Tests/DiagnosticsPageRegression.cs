using System.Text.Json;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 网络诊断页回归（迁移工单 #6 / T03）：五类诊断分列与证据边界、系统选路推断标注、
/// UU 本机探测与远程实测分列、门户改版信号与认证暂停分别呈现、受控阶段与失败分类、
/// 只读边界与不可信状态降级。受控 UI 证据由 scripts/qa-avalonia-diagnostics.ps1（diagnostics 模式）提供。
/// </summary>
internal static class DiagnosticsPageRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("诊断页回归：五类诊断分列且各自带证据边界说明", () => Sync(() =>
        {
            var content = DiagnosticsModel.From(Reading(Online(), TimeSpan.Zero));
            Require(content.Page == ShellPage.Diagnostics && content.Title == "网络诊断");
            Require(content.Trusted);

            Require(content.Categories.Select(row => row.Caption).SequenceEqual(
            [
                "校园 Wi-Fi", "校园网认证", "校园网直连", "代理 / 系统路径", "UU 远程 · 本机探测", "UU 远程 · 远程会话"
            ]), "五类诊断必须分列且 UU 拆为本机探测与远程会话两行：" + string.Join("、", content.Categories.Select(row => row.Caption)));

            var byCaption = content.Categories.ToDictionary(row => row.Caption);
            Require(byCaption["校园 Wi-Fi"].Value == "已连接");
            Require(byCaption["校园网认证"].Value == "未查询（外网检查已通过）");
            Require(byCaption["校园网直连"].Value == "外网可达");
            Require(byCaption["代理 / 系统路径"].Value == "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）");
            Require(byCaption["UU 远程 · 本机探测"].Value == "UU 服务运行，2 条 TCP 连接；会话待实测");

            // 证据边界说明（故事 19、20、23）：推断口径、物理出口口径、局部探测口径逐行声明。
            Require(byCaption["代理 / 系统路径"].Note.Contains("推断") && byCaption["代理 / 系统路径"].Note.Contains("不是物理出口验证"),
                "系统选路必须标注为推断");
            Require(byCaption["校园网直连"].Note.Contains("不等于物理出口已核验"), "直连必须声明物理出口未核验");
            Require(byCaption["UU 远程 · 本机探测"].Note.Contains("不能当作远程会话可用的证明"), "局部探测必须声明口径");
            Require(byCaption["UU 远程 · 远程会话"].Value == "未实测" && byCaption["UU 远程 · 远程会话"].Note.Contains("不发起远程会话实测"),
                "远程会话必须与局部探测分开显示");
            Require(content.Categories.All(row => row.Note.Length > 0), "每行都必须携带证据边界说明");
        })),
        ("诊断页回归：旧版服务值缺少推断标注时页面仍声明选路口径", () => Sync(() =>
        {
            // Tunnel 不含「推断」字样（旧服务或简化值）：页面注释仍必须声明推断边界（故事 19）。
            var content = DiagnosticsModel.From(Reading(Online() with { Tunnel = "系统路径可达" }, TimeSpan.Zero));
            var tunnel = content.Categories.Single(row => row.Caption == "代理 / 系统路径");
            Require(tunnel.Value == "系统路径可达");
            Require(tunnel.Note.Contains("路由样本推断") && tunnel.Note.Contains("不是物理出口验证"),
                "选路推断标注不能依赖服务端文案");

            // UU 未核验：本机探测行如实显示，远程会话行仍固定为未实测。
            var unverified = DiagnosticsModel.From(Reading(Online() with { Uu = "未核验" }, TimeSpan.Zero));
            Require(unverified.Categories.Single(row => row.Caption == "UU 远程 · 本机探测").Value == "未核验");
            Require(unverified.Categories.Single(row => row.Caption == "UU 远程 · 远程会话").Value == "未实测");
        })),
        ("诊断页回归：门户改版信号与认证暂停分别呈现且互不改写", () => Sync(() =>
        {
            // 只有认证暂停：出现暂停行，不出现结构信号行。
            var suspended = DiagnosticsModel.From(Reading(Online() with { CredentialsBlocked = true }, TimeSpan.Zero));
            Require(suspended.Signals.Select(row => row.Caption).SequenceEqual(["认证提交暂停"]));
            Require(suspended.Signals[0].Tone == OverviewTone.Problem && suspended.Signals[0].Value == "存在");
            Require(suspended.Signals[0].Note.Contains("需要人工核实后解除"), "暂停行必须带解除前提说明");

            // 只有结构信号：单独成行，且明确「不代表认证已暂停」。
            var layout = DiagnosticsModel.From(Reading(Online() with { PortalLayoutSuspected = true }, TimeSpan.Zero));
            Require(layout.Signals.Select(row => row.Caption).SequenceEqual(["门户结构疑似变化"]));
            Require(layout.Signals[0].Tone == OverviewTone.Warning);
            Require(layout.Signals[0].Note.Contains("不代表认证已暂停"), "结构信号必须与认证暂停划清界限");

            // 两者同时存在：两行并存，互不合并、互不覆盖。
            var both = DiagnosticsModel.From(Reading(Online() with { CredentialsBlocked = true, PortalLayoutSuspected = true }, TimeSpan.Zero));
            Require(both.Signals.Select(row => row.Caption).SequenceEqual(["认证提交暂停", "门户结构疑似变化"]),
                "两类信号必须同时分别呈现");

            // 都不存在：显式说明「无」，不静默隐藏。
            var none = DiagnosticsModel.From(Reading(Online(), TimeSpan.Zero));
            Require(none.Signals.Count == 1 && none.Signals[0].Caption == "认证信号" && none.Signals[0].Value == "无");
        })),
        ("诊断页回归：认证尝试只显示受控阶段与失败分类", () => Sync(() =>
        {
            var content = DiagnosticsModel.From(Reading(Online() with
            {
                CredentialsBlocked = true,
                AuthenticationStage = "Submission",
                AuthenticationFailure = "BindingRejected",
                AuthenticationHttpStatus = 401,
                CredentialRequestStarted = true,
                AuthenticationPersistentState = "BindingRejected",
                LastError = "网关登录失败：https://login.csust.edu.cn:802/eportal/portal/login?user_account=fixture&user_password=fixture-secret"
            }, TimeSpan.Zero));

            var attempt = content.Attempts.Single(row => row.Caption == "最近认证尝试");
            Require(attempt.Value.Contains("认证提交") && attempt.Value.Contains("绑定被拒绝"), "受控分类缺失：" + attempt.Value);
            Require(attempt.Value.Contains("[Submission/BindingRejected]") && attempt.Value.Contains("HTTP 401"),
                "阶段与失败分类的受控标识缺失：" + attempt.Value);
            Require(attempt.Value.Contains("凭据请求已发起"), "凭据请求事实缺失：" + attempt.Value);
            foreach (var forbidden in new[] { "http", "://", "user_password", "user_account", "fixture-secret", "login.csust.edu.cn", "LastError" })
                Require(!attempt.Value.Contains(forbidden), "认证尝试显示了 " + forbidden);
            Require(attempt.Note.Contains("受控词表"), "必须声明受控口径");

            var persistent = content.Attempts.Single(row => row.Caption == "认证持久状态");
            Require(persistent.Value == "BindingRejected", "持久状态应按受控名显示");

            // 在线且无认证尝试：不谎报「通过」。
            var idle = DiagnosticsModel.From(Reading(Online(), TimeSpan.Zero)).Attempts.Single(row => row.Caption == "最近认证尝试");
            Require(idle.Value == "尚无认证尝试记录。", "无尝试记录时不得显示受控通过结论：" + idle.Value);
        })),
        ("诊断页回归：未知阶段或失败分类按受控口径降级且不回显原始值", () => Sync(() =>
        {
            foreach (var (stage, failure) in new[] { ("MysteryStage", "BindingRejected"), ("Submission", "FutureFailure") })
            {
                var content = DiagnosticsModel.From(Reading(Online() with
                {
                    AuthenticationStage = stage, AuthenticationFailure = failure
                }, TimeSpan.Zero));
                var attempt = content.Attempts.Single(row => row.Caption == "最近认证尝试");
                Require(!attempt.Value.Contains(stage), "未知枚举值被原样回显：" + attempt.Value);
                Require(!attempt.Value.Contains(failure), "未知枚举值被原样回显：" + attempt.Value);
                Require(attempt.Value.Contains("无法识别"), "未知枚举未按受控口径降级：" + attempt.Value);
            }

            // 空白的阶段/失败分类使整个快照不通过完整性校验：整页按不可确认降级，绝不回显原始值。
            var blank = DiagnosticsModel.From(Reading(Online() with { AuthenticationStage = "" }, TimeSpan.Zero));
            Require(!blank.Trusted && blank.Attempts.Single(row => row.Caption == "最近认证尝试").Value == "不可确认",
                "空白枚举应触发整体降级");

            // 持久状态未知：显示受控「未知」，不回显。
            var unknown = DiagnosticsModel.From(Reading(Online() with { AuthenticationPersistentState = "FutureState" }, TimeSpan.Zero));
            Require(unknown.Attempts.Single(row => row.Caption == "认证持久状态").Value == "未知");
        })),
        ("诊断页回归：语义色跟随证据值且不把未确认当正常", () => Sync(() =>
        {
            var content = DiagnosticsModel.From(Reading(Online(), TimeSpan.Zero));
            var byCaption = content.Categories.ToDictionary(row => row.Caption);
            Require(byCaption["校园 Wi-Fi"].Tone == OverviewTone.Good);
            Require(byCaption["校园网认证"].Tone == OverviewTone.Good);
            Require(byCaption["校园网直连"].Tone == OverviewTone.Good);
            Require(byCaption["代理 / 系统路径"].Tone == OverviewTone.Neutral);
            Require(byCaption["UU 远程 · 本机探测"].Tone == OverviewTone.Good);

            var trouble = DiagnosticsModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知",
                Wifi = "未连接", Portal = "未知，不自动认证", Tunnel = "本机代理不可达", Uu = "UU 状态未知，等待新检测"
            }, TimeSpan.Zero));
            var rows = trouble.Categories.ToDictionary(row => row.Caption);
            Require(rows["校园 Wi-Fi"].Tone == OverviewTone.Neutral, "未连接不是已确认故障，保持中性");
            Require(rows["校园网认证"].Tone == OverviewTone.Attention, "未知认证状态用注意色");
            Require(rows["校园网直连"].Tone == OverviewTone.Attention, "外网不可达或未知用注意色");
            Require(rows["代理 / 系统路径"].Tone == OverviewTone.Warning, "代理不可达用警示色");
            Require(rows["UU 远程 · 本机探测"].Tone == OverviewTone.Attention, "UU 状态未知用注意色");

            var rejected = DiagnosticsModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网不可达或未知", Portal = "需认证"
            }, TimeSpan.Zero));
            Require(rejected.Categories.Single(row => row.Caption == "校园网认证").Tone == OverviewTone.Problem, "需认证用问题色");
            Require(rejected.Categories.Single(row => row.Caption == "校园网直连").Tone == OverviewTone.Attention);

            var failed = DiagnosticsModel.From(Reading(Online() with
            {
                State = "需要处理认证", Direct = "外网检查未通过"
            }, TimeSpan.Zero));
            Require(failed.Categories.Single(row => row.Caption == "校园网直连").Tone == OverviewTone.Problem, "检查未通过用问题色");
        })),
        ("诊断页回归：只读边界声明存在且页面不携带任何动作", () => Sync(() =>
        {
            Require(DiagnosticsModel.ReadOnlyNotice.Contains("只读") && DiagnosticsModel.ReadOnlyNotice.Contains("不提交凭据")
                && DiagnosticsModel.ReadOnlyNotice.Contains("不触发认证或断线"), "只读边界声明措辞缺失：" + DiagnosticsModel.ReadOnlyNotice);

            // 内容模型没有任何可执行意图：诊断页不提供按钮、命令或提权操作（故事 23 的构造性保证）。
            var content = DiagnosticsModel.From(Reading(Online() with { CredentialsBlocked = true }, TimeSpan.Zero));
            var properties = content.GetType().GetProperties().Select(property => property.PropertyType.Name).ToList();
            Require(!properties.Any(name => name.Contains("Intent") || name.Contains("Command") || name.Contains("Operation")),
                "诊断页内容模型不得携带动作意图");
        })),
        ("诊断页回归：缺失、过期、损坏状态不可确认且不沿用上次画面", () => Sync(() =>
        {
            var trusted = Online() with
            {
                CredentialsBlocked = true, PortalLayoutSuspected = true,
                AuthenticationStage = "Submission", AuthenticationFailure = "BindingRejected",
                AuthenticationHttpStatus = 401, AuthenticationPersistentState = "BindingRejected"
            };
            var cases = new (string Name, StatusReading Reading)[]
            {
                ("缺失", Reading(null)),
                ("过期", Reading(trusted, TimeSpan.FromMinutes(1))),
                ("未来异常", Reading(trusted, TimeSpan.FromSeconds(-30))),
                ("损坏", Reading(Online() with { Portal = null! }, TimeSpan.Zero))
            };
            foreach (var (name, reading) in cases)
            {
                var content = DiagnosticsModel.From(reading);
                Require(!content.Trusted, name + "：不应判定为可信");
                Require(content.Categories.Count == 6, name + "：降级时五类分列仍须成立");
                Require(content.Categories.All(row => row.Value == "不可确认" && row.Tone == OverviewTone.Neutral),
                    name + "：分类行未按不可确认降级");
                Require(content.Categories.All(row => !row.Value.Contains("已连接") && !row.Value.Contains("外网可达")),
                    name + "：分类行沿用了上次画面");
                Require(content.Signals.Count == 1 && content.Signals[0].Value == "不可确认",
                    name + "：认证信号未按不可确认降级");
                Require(!content.Signals.Any(row => row.Caption == "认证提交暂停" || row.Caption == "门户结构疑似变化"),
                    name + "：认证信号沿用了上次画面");
                Require(content.Attempts.All(row => row.Value == "不可确认"), name + "：认证尝试未按不可确认降级");
                Require(content.Attempts.All(row => !row.Value.Contains("BindingRejected")), name + "：认证尝试沿用了上次画面");
            }

            // 可信 → 不可信 → 可信：投影始终来自当前读取，无残留。
            var recovered = DiagnosticsModel.From(Reading(trusted, TimeSpan.Zero));
            Require(recovered.Categories.First(row => row.Caption == "校园 Wi-Fi").Value == "已连接");
            Require(recovered.Signals.Count == 2);
        })),
        ("诊断页回归：控制器随状态更新诊断页且离开后不被改写", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "autonet-diagnostics-controller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "status.json");
                var pipe = new StatusPipe(path);
                using var controller = new ShellController(pipe);
                var applied = new List<PageContent>();
                controller.ContentChanged += content => applied.Add(content);

                await controller.SelectAsync(ShellPage.Diagnostics);
                Require(controller.Content is DiagnosticsContent { Trusted: false }
                        && controller.Content is DiagnosticsContent degraded && degraded.Categories.All(row => row.Value == "不可确认"),
                    "首次进入诊断页应按未就绪降级");

                File.WriteAllText(path, JsonSerializer.Serialize(Online(), Storage.Json));
                await controller.RefreshStatusAsync();
                var online = controller.Content as DiagnosticsContent;
                Require(online is { Trusted: true } && online.Categories.First(row => row.Caption == "校园网直连").Value == "外网可达",
                    "诊断页未随状态更新");
                Require(applied.Count(content => content is DiagnosticsContent { Trusted: true }) == 1,
                    "诊断页更新应按内容事件发布且不重复");

                await controller.SelectAsync(ShellPage.Logs);
                File.WriteAllText(path, JsonSerializer.Serialize(Online() with { Direct = "外网不可达或未知" }, Storage.Json));
                await controller.RefreshStatusAsync();
                Require(controller.Content is PageScaffold { Page: ShellPage.Logs }, "离开诊断页后仍被状态更新改写");

                await controller.SelectAsync(ShellPage.Diagnostics);
                var back = controller.Content as DiagnosticsContent;
                Require(back is { Trusted: true } && back.Categories.First(row => row.Caption == "校园网直连").Value == "外网不可达或未知",
                    "返回诊断页未按最新状态重建");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),
        ("诊断页回归：内容指纹稳定且随证据值变化", () => Sync(() =>
        {
            var reading = Reading(Online(), TimeSpan.Zero);
            var first = DiagnosticsModel.From(reading);
            var again = DiagnosticsModel.From(reading);
            Require(first.ContentKey == again.ContentKey, "同一读取的指纹必须稳定（轮询不重建控件树）");

            var changed = DiagnosticsModel.From(Reading(Online() with { Direct = "外网不可达或未知" }, TimeSpan.Zero));
            Require(changed.ContentKey != first.ContentKey, "证据值变化必须改变指纹");
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
        Tunnel = "系统路径可达（路由样本推断为 Wi-Fi，出口未核验）",
        Uu = "UU 服务运行，2 条 TCP 连接；会话待实测"
    };

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }
}
