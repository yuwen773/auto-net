using System.Text.Json;
using AutoNet.Core;

/// <summary>
/// 虚拟指针与手动接管回归（工单 #40）：步骤只在实际动作发生时显示指针，失败/跳过/目标不可用
/// 降级为步骤提示，减少动画显示静态状态，交互后暂停自动填表与提交，空闲满 15 秒且完整复核
/// 才恢复，已填内容不被覆盖，投影与脚本不包含凭据、Cookie 或页面敏感内容。
/// 全部为纯逻辑与脚本字符串断言；不启动浏览器、不连接门户、不读取真实凭据。
/// </summary>
internal static class AuthPagePointerRegression
{
    private const string FakeSecret = "qa-fake-pass-DO-NOT-ECHO";

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("虚拟指针：只有真实动作开始时才出现", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.Idle, "初始状态不应显示指针");

            policy.StartStep(AuthPageStep.FillPassword, AuthPageTarget.At(120, 240));
            Require(policy.View.PointerVisible && policy.View.State == AuthPageStepState.Started, "真实步骤未显示指针");
            Require(policy.View.X == 120 && policy.View.Y == 240, "指针未采用真实目标位置");
            Require(policy.View.StepLabel == "填写密码", "步骤名称不符：" + policy.View.StepLabel);

            policy.CompleteStep(AuthPageStep.FillPassword, AuthPageTarget.At(120, 240));
            Require(policy.View.State == AuthPageStepState.Completed && policy.View.PointerVisible,
                "完成后未保留最后真实目标作为静态指示");
            Require(policy.View.Hint.Contains("已完成", StringComparison.Ordinal), "完成提示不符：" + policy.View.Hint);
            Require(!policy.View.Hint.Contains("成功", StringComparison.Ordinal), "完成步骤被说成认证成功");
        })),
        ("虚拟指针：目标不可用、跳过与失败只给步骤提示", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            policy.StartStep(AuthPageStep.Submit, AuthPageTarget.None);
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.Unavailable, "目标不可用仍显示指针");
            Require(policy.View.Hint.Contains("目标控件", StringComparison.Ordinal), "目标不可用缺少步骤提示");

            policy.FailStep(AuthPageStep.Submit, AuthPageStepReason.NoSubmitButton);
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.Failed, "失败仍显示指针");
            Require(policy.View.Hint.Contains("未找到提交按钮", StringComparison.Ordinal), "失败提示不准确：" + policy.View.Hint);
            Require(!policy.View.Hint.Contains("成功", StringComparison.Ordinal), "失败被说成成功");

            policy.SkipStep(AuthPageStep.AcceptAgreement, AuthPageStepReason.NothingToDo);
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.Skipped, "跳过仍显示指针");
            Require(policy.View.Hint.Contains("已跳过", StringComparison.Ordinal), "跳过缺少提示");

            policy.AwaitResult();
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.AwaitingResult, "等待结果仍显示指针");
            Require(policy.View.Hint.Contains("等待认证结果", StringComparison.Ordinal), "等待结果提示不符");
            Require(!policy.View.Hint.Contains("认证成功", StringComparison.Ordinal), "等待结果被说成认证成功");
        })),
        ("虚拟指针：减少动画时显示静态状态", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy(reducedMotion: true);
            policy.StartStep(AuthPageStep.FillAccount, AuthPageTarget.At(10, 20));
            Require(policy.View.ReducedMotion && policy.ReducedMotion, "减少动画未进入静态状态");
            Require(policy.View.PointerVisible, "减少动画时仍应显示静态指针位置");

            policy.NoteReducedMotion(false);
            Require(!policy.View.ReducedMotion, "减少动画关闭后未更新投影");

            var reduced = AuthPagePointerScript.Install(true);
            Require(reduced.Contains("const forcedReduced = true;", StringComparison.Ordinal), "脚本未采用系统减少动画设置");
            Require(reduced.Contains("prefers-reduced-motion", StringComparison.Ordinal), "脚本未尊重页面减少动画偏好");
            Require(reduced.Contains("'none'", StringComparison.Ordinal), "减少动画时仍启用过渡动画");
        })),
        ("虚拟指针：页面变化清除过期指针且不补演", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            policy.StartStep(AuthPageStep.FillAccount, AuthPageTarget.At(30, 40));
            policy.NoteViewportChanged();
            Require(!policy.View.PointerVisible && policy.View.State == AuthPageStepState.Unavailable,
                "页面位置变化后仍显示过期指针");
            Require(policy.View.Hint.Contains("页面位置已变化", StringComparison.Ordinal), "位置变化缺少受控提示");
            Require(policy.View.X == 0 && policy.View.Y == 0, "位置变化后仍保留旧坐标");

            policy.NoteNavigation();
            Require(policy.View.State == AuthPageStepState.Idle && !policy.View.PointerVisible, "导航后未清除旧页面步骤");
            Require(policy.View.Step is null && policy.View.Hint.Length == 0, "导航后仍保留旧页面提示");
        })),
        ("虚拟指针：投影不包含账号密码或页面内容", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            policy.StartStep(AuthPageStep.FillPassword, AuthPageTarget.At(1, 2));
            policy.CompleteStep(AuthPageStep.FillPassword, AuthPageTarget.At(1, 2));
            policy.FailStep(AuthPageStep.Submit, AuthPageStepReason.NoSubmitButton);
            policy.NoteInteraction(TimeSpan.Zero);

            foreach (var text in new[] { policy.View.StepLabel, policy.View.Hint })
            {
                Require(!text.Contains(FakeSecret, StringComparison.Ordinal), "指针投影包含密码");
                Require(!text.Contains("qa-fake-user", StringComparison.Ordinal), "指针投影包含账号");
                Require(!text.Contains("cookie", StringComparison.OrdinalIgnoreCase), "指针投影包含 Cookie");
            }

            var install = AuthPagePointerScript.Install(false);
            foreach (var forbidden in new[] { "password", "username", "cookie", FakeSecret })
                Require(!install.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    "覆盖层脚本包含敏感字段：" + forbidden);
        })),
        ("虚拟指针：覆盖层不移动系统鼠标且不拦截输入", () => Sync(() =>
        {
            var install = AuthPagePointerScript.Install(false);
            Require(install.Contains("pointer-events:none", StringComparison.Ordinal), "覆盖层未声明不拦截真实输入");
            Require(install.Contains("event.isTrusted", StringComparison.Ordinal), "交互检测未使用受信任事件");
            Require(!install.Contains("SetCursorPos", StringComparison.Ordinal), "覆盖层试图移动系统鼠标");
            Require(!install.Contains("dispatchMouseEvent", StringComparison.Ordinal), "覆盖层使用浏览器级模拟输入");
            Require(!install.Contains("elementFromPoint", StringComparison.Ordinal), "覆盖层通过命中测试拦截输入");
            // 自动提交的 click()/change 默认动作可能产生受信任的 submit/change 事件；
            // 监听它们会把程序自己的提交误判为人工接管，因此只监听指针、键盘与输入。
            Require(install.Contains("['pointerdown', 'keydown', 'input']", StringComparison.Ordinal),
                "交互检测监听了 submit 或 change，可能把程序自己的提交误判为人工接管");

            var submit = AuthPagePointerScript.Step(AuthPagePointerScript.Submit);
            Require(submit.Contains("dispatchEvent(new MouseEvent('click'", StringComparison.Ordinal),
                "提交步骤未使用页面内事件");
            Require(!submit.Contains("SetCursorPos", StringComparison.Ordinal), "提交步骤试图移动系统鼠标");

            foreach (var kind in new[]
                     {
                         AuthPagePointerScript.Account, AuthPagePointerScript.Password,
                         AuthPagePointerScript.Agreement, AuthPagePointerScript.Submit
                     })
                Require(AuthPagePointerScript.Step(kind).Contains("if (S.takeover)", StringComparison.Ordinal),
                    "步骤未在用户接管时停止：" + kind);
        })),
        ("手动接管：交互后暂停自动填表和提交", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            policy.StartStep(AuthPageStep.Submit, AuthPageTarget.At(5, 6));
            policy.NoteInteraction(TimeSpan.FromSeconds(10));
            Require(policy.TakeoverActive && !policy.CanAutoAct, "交互后未进入接管");
            Require(policy.View.State == AuthPageStepState.Takeover && !policy.View.PointerVisible, "接管未隐藏指针");
            Require(policy.View.Hint.Contains("正在手动操作", StringComparison.Ordinal), "接管提示不符");
            Require(policy.View.Hint.Contains("暂停", StringComparison.Ordinal), "接管提示未说明已暂停");
        })),
        ("手动接管：空闲满 15 秒后仍需完整复核才恢复", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            var start = TimeSpan.FromSeconds(5);
            policy.NoteInteraction(start);
            var all = new AuthPageRecheck(true, true, true, true, true);

            Require(!policy.TryResume(start + TimeSpan.FromSeconds(14.9), all), "不足 15 秒就恢复自动操作");
            Require(policy.TakeoverActive, "不足 15 秒时接管已被清除");
            foreach (var partial in new[]
                     {
                         all with { Network = false }, all with { Authentication = false },
                         all with { PageOrigin = false }, all with { Form = false }, all with { Budget = false }
                     })
                Require(!policy.TryResume(start + TimeSpan.FromSeconds(16), partial), "复核事实不全仍恢复自动操作");
            Require(policy.TakeoverActive, "复核失败后接管已被清除");

            Require(policy.TryResume(start + TimeSpan.FromSeconds(15), all), "空闲满 15 秒且复核齐全仍未恢复");
            Require(!policy.TakeoverActive && policy.CanAutoAct, "恢复后仍处于接管");
        })),
        ("手动接管：再次交互重置等待且空闲本身不授权", () => Sync(() =>
        {
            var policy = new AuthPagePointerPolicy();
            var start = TimeSpan.FromSeconds(5);
            var all = new AuthPageRecheck(true, true, true, true, true);
            policy.NoteInteraction(start);
            policy.NoteInteraction(start + TimeSpan.FromSeconds(10));
            Require(!policy.TryResume(start + TimeSpan.FromSeconds(20), all), "再次交互后仍按旧时间恢复");
            Require(policy.TryResume(start + TimeSpan.FromSeconds(25), all), "再次交互后未重新等待 15 秒");

            // 空闲本身不是授权：即使过了一小时，复核事实不成立也不能恢复。
            var again = new AuthPagePointerPolicy();
            again.NoteInteraction(TimeSpan.Zero);
            Require(!again.TryResume(TimeSpan.FromHours(1), new AuthPageRecheck(false, false, false, false, false)),
                "空闲时间被当成提交授权");
            Require(again.TakeoverActive, "复核不成立时接管被清除");

            // 策略不暴露“已授权/已成功”状态，避免把空闲或动画当成认证结论。
            Require(typeof(AuthPagePointerPolicy).GetProperties()
                    .All(property => !property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) &&
                        !property.Name.Contains("Success", StringComparison.OrdinalIgnoreCase)),
                "策略暴露了空闲即授权或动画即成功的状态");
        })),
        ("手动接管：已填内容不被覆盖", () => Sync(() =>
        {
            var password = AuthPagePointerScript.Step(AuthPagePointerScript.Password, JsonSerializer.Serialize(FakeSecret));
            Require(password.Contains("if (target.value || target.dataset.autonetTouched === '1')", StringComparison.Ordinal),
                "填写脚本未检查已有内容或用户改动");
            Require(password.Contains("reason: 'user-value'", StringComparison.Ordinal), "填写脚本未保留用户输入");
            Require(!password.Contains("target.value = ''", StringComparison.Ordinal), "填写脚本清空了已有内容");

            var agreement = AuthPagePointerScript.Step(AuthPagePointerScript.Agreement);
            Require(agreement.Contains("autonetTouched", StringComparison.Ordinal), "协议步骤未保留用户取消的勾选");
            Require(agreement.Contains("reason: 'user-value'", StringComparison.Ordinal), "协议步骤未按用户输入跳过");

            var hint = AuthPagePointerPolicy.Hint(AuthPageStep.FillPassword, AuthPageStepReason.UserValue);
            Require(hint.Contains("不会覆盖用户输入", StringComparison.Ordinal), "跳过提示未说明不覆盖用户输入");
            Require(!hint.Contains(FakeSecret, StringComparison.Ordinal), "跳过提示回显了密码");

            Require(AuthPagePointerPolicy.ParseReason("overlay-missing") == AuthPageStepReason.OverlayMissing,
                "覆盖层缺失未按受控原因归类");
            Require(AuthPagePointerPolicy.Hint(AuthPageStep.Submit, AuthPageStepReason.OverlayMissing)
                .Contains("覆盖层", StringComparison.Ordinal), "覆盖层缺失缺少步骤提示");
        })),
        ("手动接管：交互检测盲区已明确记录", () => Sync(() =>
        {
            var note = AuthPagePointerPolicy.BlindSpotNote;
            Require(note.Contains("跨源框架", StringComparison.Ordinal), "盲区未说明跨源框架");
            Require(note.Contains("浏览器自身界面", StringComparison.Ordinal), "盲区未说明浏览器自身界面");
            Require(note.Contains("全部人工提交", StringComparison.Ordinal), "盲区未说明不能声称拦截全部人工提交");
            Require(note.Contains("不能", StringComparison.Ordinal), "盲区说明语气过强");
        })),
        ("手动接管：覆盖层消息只接受受控事件", () => Sync(() =>
        {
            Require(AuthPagePointerScript.TryParseMessage(AuthPagePointerScript.MessageInteraction, out var interaction) &&
                interaction == AuthPageInteractionKind.TrustedInput, "交互消息未识别");
            Require(AuthPagePointerScript.TryParseMessage(AuthPagePointerScript.MessageViewport, out var viewport) &&
                viewport == AuthPageInteractionKind.ViewportChanged, "位置变化消息未识别");
            Require(AuthPagePointerScript.TryParseMessage("autonet:reduced:1", out var on) &&
                on == AuthPageInteractionKind.ReducedMotionOn, "减少动画开启消息未识别");
            Require(AuthPagePointerScript.TryParseMessage("autonet:reduced:0", out var off) &&
                off == AuthPageInteractionKind.ReducedMotionOff, "减少动画关闭消息未识别");
            Require(!AuthPagePointerScript.TryParseMessage("qa-fake-user", out _), "任意页面文本被当成受控事件");
            Require(!AuthPagePointerScript.TryParseMessage(null, out _), "空消息被当成受控事件");

            Throws<ArgumentException>(() => AuthPagePointerScript.Step("password-copy"));
            Require(AuthPagePointerPolicy.ParseReason("checkbox-rejected") == AuthPageStepReason.CheckboxRejected,
                "受控原因映射不符");
            Require(AuthPagePointerPolicy.ParseReason("qa-fake-user") == AuthPageStepReason.TargetUnavailable,
                "未知原因未保守归类");
        }))
    ];

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Require(bool condition, string message = "断言失败")
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("未抛出预期异常：" + typeof(T).Name);
    }
}
