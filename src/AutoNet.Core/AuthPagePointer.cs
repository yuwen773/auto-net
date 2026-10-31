using System.Text.Json;

namespace AutoNet.Core;

/// <summary>
/// 自动认证页面上由程序执行的可观察步骤（工单 #40）。步骤只描述动作，不携带账号、密码、
/// Cookie 或页面内容，因此步骤名与提示可以安全地出现在截图、日志和诊断包里。
/// </summary>
public enum AuthPageStep
{
    FillAccount,
    FillPassword,
    AcceptAgreement,
    Submit,
    AwaitResult
}

/// <summary>步骤的受控状态。只有 <see cref="Started"/> 与 <see cref="Completed"/> 代表动作真的发生。</summary>
public enum AuthPageStepState
{
    Idle,
    Started,
    Completed,
    Skipped,
    Unavailable,
    Failed,
    AwaitingResult,
    Takeover
}

/// <summary>步骤失败或跳过的受控原因；不携带异常文本、URL 或页面内容。</summary>
public enum AuthPageStepReason
{
    None,
    TargetUnavailable,
    Offscreen,
    UserValue,
    NothingToDo,
    CheckboxRejected,
    NoSubmitButton,
    FormMissing,
    UnsupportedSource,
    OverlayMissing,
    UserTakeover
}

/// <summary>目标控件在页面视口中的位置（CSS 像素）。不可用时指针不显示，只保留步骤提示。</summary>
public readonly record struct AuthPageTarget(bool Available, double X, double Y)
{
    public static readonly AuthPageTarget None = new(false, 0, 0);

    public static AuthPageTarget At(double x, double y) => new(true, x, y);
}

/// <summary>
/// 接管恢复前必须重新核对的事实（工单 #40）：网络、认证状态、页面来源、表单与共享额度。
/// 全部成立才允许恢复自动操作；空闲时间本身不构成任何一项。
/// </summary>
public readonly record struct AuthPageRecheck(
    bool Network, bool Authentication, bool PageOrigin, bool Form, bool Budget)
{
    public bool All => Network && Authentication && PageOrigin && Form && Budget;
}

/// <summary>页面来源与表单的可证实就绪状态；由浏览器宿主在导航后重新核对。</summary>
public readonly record struct AuthPageReadiness(bool OriginTrusted, bool FormPresent)
{
    public bool Ready => OriginTrusted && FormPresent;
}

/// <summary>交互检测可观察到的受控事件类型；消息本身不含按键、坐标或输入值。</summary>
public enum AuthPageInteractionKind
{
    TrustedInput,
    ViewportChanged,
    ReducedMotionOn,
    ReducedMotionOff
}

/// <summary>
/// 虚拟指针与步骤提示的投影（工单 #40）。字段只包含受控文案与目标位置，
/// 永远不包含账号、密码、Cookie 或页面文本。
/// </summary>
public sealed record AuthPagePointerView(
    AuthPageStepState State,
    AuthPageStep? Step,
    bool PointerVisible,
    double X,
    double Y,
    string StepLabel,
    string Hint,
    bool Takeover,
    bool ReducedMotion)
{
    public static readonly AuthPagePointerView Idle =
        new(AuthPageStepState.Idle, null, false, 0, 0, "", "", false, false);
}

/// <summary>
/// 虚拟指针与手动接管策略（工单 #40）。纯逻辑：时间由调用方传入，不读系统时钟、不访问页面。
///
/// 不变量：
/// - 指针只在真实动作开始时出现；失败、跳过、目标不可用只给步骤提示，绝不补演过去的轨迹。
/// - 用户受信任输入后进入接管：暂停新的自动填表与提交；至少空闲 15 秒，并且调用方重新核对
///   网络、认证状态、页面来源、表单与共享额度全部成立后，才允许恢复。
/// - 空闲本身从不等于提交授权或认证成功；本类型没有任何“已授权”或“认证成功”状态。
/// - 页面位置变化（滚动、缩放、导航）会清除过期指针；不按旧坐标重放。
/// </summary>
public sealed class AuthPagePointerPolicy
{
    /// <summary>接管后允许恢复自动操作的最短空闲时间。</summary>
    public static readonly TimeSpan ResumeIdle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 交互检测的盲区说明。页面内事件只能看到可观测的受信任输入；跨源框架、浏览器自身界面、
    /// 脚本被禁用或页面被替换时都可能漏检，不能声称拦截了浏览器中的全部人工提交。
    /// </summary>
    public const string BlindSpotNote =
        "交互检测只覆盖页面内可观测的受信任输入事件；跨源框架、浏览器自身界面、脚本被禁用或页面被替换时可能漏检，" +
        "不能据此声称拦截或计数了浏览器中的全部人工提交。";

    private TimeSpan? lastInteraction;
    private AuthPagePointerView view = AuthPagePointerView.Idle;
    private bool hintCleared;

    public AuthPagePointerPolicy(bool reducedMotion = false)
    {
        ReducedMotion = reducedMotion;
    }

    /// <summary>最近一次投影；可直接用于页面覆盖层、QA 证据或日志。</summary>
    public AuthPagePointerView View => view;

    /// <summary>是否处于手动接管；接管期间不得开始新的自动填表或提交。</summary>
    public bool TakeoverActive => lastInteraction is not null;

    /// <summary>系统或页面要求减少动画时，覆盖层显示静态状态而不是动画。</summary>
    public bool ReducedMotion { get; private set; }

    /// <summary>只有未处于接管时才允许自动操作；空闲时间本身不会清除接管。</summary>
    public bool CanAutoAct => !TakeoverActive;

    /// <summary>距最近一次受信任输入的时长；从未交互时返回 <see cref="TimeSpan.MaxValue"/>。</summary>
    public TimeSpan IdleFor(TimeSpan now) => lastInteraction is { } last ? now - last : TimeSpan.MaxValue;

    /// <summary>开始一个真实步骤：目标可用时显示指针，否则降级为步骤提示。</summary>
    public void StartStep(AuthPageStep step, AuthPageTarget target) => view = target.Available
        ? new(AuthPageStepState.Started, step, true, target.X, target.Y, Label(step), "", TakeoverActive, ReducedMotion)
        : new(AuthPageStepState.Unavailable, step, false, 0, 0, Label(step), Hint(step, AuthPageStepReason.TargetUnavailable),
            TakeoverActive, ReducedMotion);

    /// <summary>步骤完成：保留最后真实目标作为静态指示，不额外播放动画。</summary>
    public void CompleteStep(AuthPageStep step, AuthPageTarget target) => view = target.Available
        ? new(AuthPageStepState.Completed, step, true, target.X, target.Y, Label(step), "已完成：" + Label(step),
            TakeoverActive, ReducedMotion)
        : new(AuthPageStepState.Completed, step, false, 0, 0, Label(step), "已完成：" + Label(step),
            TakeoverActive, ReducedMotion);

    /// <summary>步骤被安全跳过（例如页面没有协议勾选项）：不显示指针，只说明跳过原因。</summary>
    public void SkipStep(AuthPageStep step, AuthPageStepReason reason) => view =
        new(AuthPageStepState.Skipped, step, false, 0, 0, Label(step), Hint(step, reason), TakeoverActive, ReducedMotion);

    /// <summary>步骤失败：不显示指针，不声称已点击或已成功。</summary>
    public void FailStep(AuthPageStep step, AuthPageStepReason reason) => view =
        new(AuthPageStepState.Failed, step, false, 0, 0, Label(step), Hint(step, reason), TakeoverActive, ReducedMotion);

    /// <summary>凭据已发起提交：只陈述等待结果，不推断成功。</summary>
    public void AwaitResult() => view = new(
        AuthPageStepState.AwaitingResult, AuthPageStep.AwaitResult, false, 0, 0, Label(AuthPageStep.AwaitResult),
        "已发起提交，等待认证结果。", TakeoverActive, ReducedMotion);

    /// <summary>检测到用户受信任输入：进入接管，暂停自动填表与提交。</summary>
    public void NoteInteraction(TimeSpan now)
    {
        lastInteraction = now;
        hintCleared = false;
        view = new(AuthPageStepState.Takeover, null, false, 0, 0, "", "正在手动操作，已暂停自动填表和提交。",
            true, ReducedMotion);
    }

    /// <summary>页面位置变化：清除过期指针，改为步骤提示；不按旧坐标重放。</summary>
    public void NoteViewportChanged()
    {
        if (!view.PointerVisible) return;
        view = view with
        {
            State = AuthPageStepState.Unavailable,
            PointerVisible = false,
            X = 0,
            Y = 0,
            Hint = "页面位置已变化，已清除过期指针，改为步骤提示。"
        };
    }

    /// <summary>页面导航：清除旧页面的指针与步骤；接管状态保持不变。</summary>
    public void NoteNavigation()
    {
        // 不清除 hintCleared：导航本身不是新的用户操作，已清除的提示不应因导航重复上报。
        view = TakeoverActive
            ? new(AuthPageStepState.Takeover, null, false, 0, 0, "", "正在手动操作，已暂停自动填表和提交。", true, ReducedMotion)
            : AuthPagePointerView.Idle with { ReducedMotion = ReducedMotion };
    }

    /// <summary>系统或页面报告减少动画设置；只改变呈现方式，不改变步骤事实。</summary>
    public void NoteReducedMotion(bool reduced)
    {
        ReducedMotion = reduced;
        view = view with { ReducedMotion = reduced };
    }

    /// <summary>
    /// 接管空闲满 15 秒后是否应只清除“正在手动操作”提示。清除提示不恢复自动操作，
    /// 也不构成任何提交授权（ADR-0019）；再次交互会重新显示提示。
    /// </summary>
    public bool ShouldClearTakeoverHint(TimeSpan now) =>
        !hintCleared && view.Takeover && lastInteraction is { } last && now - last >= ResumeIdle;

    /// <summary>只清除提示投影；接管状态保持不变，自动填表与提交仍暂停。</summary>
    public void ClearTakeoverHint()
    {
        if (!view.Takeover) return;
        hintCleared = true;
        // 仍处于接管：保留 Takeover 投影，只清掉提示与指针。
        view = AuthPagePointerView.Idle with
        {
            State = AuthPageStepState.Takeover,
            Takeover = true,
            ReducedMotion = ReducedMotion
        };
    }

    /// <summary>
    /// 尝试在用户停止操作后恢复自动操作。必须同时满足：空闲至少 15 秒，且调用方重新核对的
    /// 网络、认证状态、页面来源、表单与共享额度全部成立。任一条件不满足都保持接管。
    /// </summary>
    public bool TryResume(TimeSpan now, AuthPageRecheck recheck)
    {
        if (lastInteraction is null) return true;
        if (now - lastInteraction.Value < ResumeIdle) return false;
        if (!recheck.All) return false;
        lastInteraction = null;
        hintCleared = false;
        view = AuthPagePointerView.Idle with { ReducedMotion = ReducedMotion };
        return true;
    }

    /// <summary>步骤的受控中文名称；不随页面内容变化。</summary>
    public static string Label(AuthPageStep step) => step switch
    {
        AuthPageStep.FillAccount => "填写账号",
        AuthPageStep.FillPassword => "填写密码",
        AuthPageStep.AcceptAgreement => "勾选协议",
        AuthPageStep.Submit => "提交登录",
        AuthPageStep.AwaitResult => "等待认证结果",
        _ => "自动操作"
    };

    /// <summary>步骤失败或跳过的受控中文提示；不包含任何凭据或页面文本。</summary>
    public static string Hint(AuthPageStep step, AuthPageStepReason reason) => reason switch
    {
        AuthPageStepReason.TargetUnavailable => "未找到" + Label(step) + "的目标控件；改为步骤提示，未继续自动提交。",
        AuthPageStepReason.Offscreen => Label(step) + "的目标不在可视区域；改为步骤提示，未继续自动提交。",
        AuthPageStepReason.UserValue => "已保留已有内容，跳过" + Label(step) + "；不会覆盖用户输入。",
        AuthPageStepReason.NothingToDo => "页面没有需要处理的" + Label(step) + "项，已跳过。",
        AuthPageStepReason.CheckboxRejected => "协议勾选未生效；已停止自动提交。",
        AuthPageStepReason.NoSubmitButton => "未找到提交按钮；未提交凭据。",
        AuthPageStepReason.FormMissing => "未找到登录表单；未继续自动填写。",
        AuthPageStepReason.UnsupportedSource => "页面来源不符合固定门户；未继续自动填写。",
        AuthPageStepReason.OverlayMissing => "页面覆盖层未就绪；为避免在无法检测手动操作时自动填表，已停止。",
        AuthPageStepReason.UserTakeover => "正在手动操作，已暂停自动填表和提交。",
        _ => "步骤未完成；未继续自动提交。"
    };

    /// <summary>把脚本返回的受控原因字符串映射为枚举；未知原因按目标不可用处理。</summary>
    public static AuthPageStepReason ParseReason(string? reason) => reason switch
    {
        "no-target" => AuthPageStepReason.TargetUnavailable,
        "offscreen" => AuthPageStepReason.Offscreen,
        "user-value" => AuthPageStepReason.UserValue,
        "nothing-to-do" => AuthPageStepReason.NothingToDo,
        "checkbox-rejected" => AuthPageStepReason.CheckboxRejected,
        "no-submit-button" => AuthPageStepReason.NoSubmitButton,
        "form-missing" => AuthPageStepReason.FormMissing,
        "untrusted-source" => AuthPageStepReason.UnsupportedSource,
        "untrusted-target" => AuthPageStepReason.UnsupportedSource,
        "overlay-missing" => AuthPageStepReason.OverlayMissing,
        "user-takeover" => AuthPageStepReason.UserTakeover,
        _ => AuthPageStepReason.TargetUnavailable
    };
}

/// <summary>
/// 虚拟指针覆盖层脚本（工单 #40）。全部为纯字符串生成，不执行、不访问网络；
/// 覆盖层只做视觉呈现：不移动系统鼠标、不拦截真实输入、不触发提交。
///
/// 覆盖层通过 <c>pointer-events:none</c> 让真实鼠标与键盘直达页面；交互检测只上报
/// “发生过受信任输入”这一事实，不携带按键、坐标或输入值。
/// </summary>
public static class AuthPagePointerScript
{
    /// <summary>受信任输入事件的上报消息；不携带任何页面内容。</summary>
    public const string MessageInteraction = "autonet:interaction";

    /// <summary>滚动、缩放或窗口尺寸变化的上报消息；用于清除过期指针。</summary>
    public const string MessageViewport = "autonet:viewport";

    /// <summary>减少动画设置变化的消息前缀，后接 <c>1</c> 或 <c>0</c>。</summary>
    public const string MessageReducedMotionPrefix = "autonet:reduced:";

    public const string Account = "account";
    public const string Password = "password";
    public const string Agreement = "agreement";
    public const string Submit = "submit";

    /// <summary>
    /// 每个文档创建时注入的覆盖层与交互检测。不选择输入框、不读取输入值，也不包含凭据。
    /// </summary>
    public static string Install(bool reducedMotion) => """
(() => {
  if (window.__autonet) return;
  const forcedReduced = __REDUCED_BOOL__;
  const state = { takeover: false, hintCleared: false, reduced: forcedReduced, step: '', hint: '' };
  let pointer = null;
  let label = null;
  function paint() {
    if (!pointer || !label) return;
    label.textContent = state.takeover ? '正在手动操作，已暂停自动填表和提交。' : (state.hint || state.step);
    const show = state.takeover ? !state.hintCleared : !!(state.step || state.hint);
    label.style.display = show ? 'block' : 'none';
    if (state.takeover) pointer.style.opacity = '0';
  }
  function mount() {
    if (pointer) return;
    const layer = document.createElement('div');
    layer.id = '__autonet_pointer_layer';
    layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483647;overflow:hidden;';
    pointer = document.createElement('div');
    pointer.id = '__autonet_pointer';
    pointer.style.cssText = 'position:absolute;left:0;top:0;width:0;height:0;opacity:0;will-change:transform;pointer-events:none;';
    pointer.innerHTML = '<svg width="28" height="28" viewBox="0 0 28 28" aria-hidden="true"><path d="M4 2 L4 22 L10 17 L14 26 L18 24 L14 15 L22 15 Z" fill="#ffffff" stroke="#111827" stroke-width="1.5"/></svg>';
    label = document.createElement('div');
    label.id = '__autonet_step';
    label.style.cssText = 'position:absolute;left:12px;bottom:12px;max-width:calc(100% - 24px);font:13px/1.5 "Microsoft YaHei",sans-serif;color:#fff;background:rgba(17,24,39,.88);padding:8px 12px;border-radius:6px;pointer-events:none;';
    layer.appendChild(pointer);
    layer.appendChild(label);
    (document.body || document.documentElement).appendChild(layer);
    paint();
  }
  function report(message) {
    try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(message); } catch (_) {}
  }
  function interact() {
    // 再次操作重新显示“正在手动操作”提示；提示是否显示不影响接管本身。
    state.hintCleared = false;
    if (state.takeover) { paint(); report('__INTERACTION__'); return; }
    state.takeover = true;
    if (pointer) pointer.style.opacity = '0';
    paint();
    report('__INTERACTION__');
  }
  // 只监听真实指针、键盘与输入事件：本脚本自己的 click()/change 默认动作可能产生
  // isTrusted 的 submit/change 事件，监听它们会把自动提交误判成人工接管。
  // 人工勾选与选择由 pointerdown 先标记，人工提交也必然先有 pointerdown 或 keydown。
  ['pointerdown', 'keydown', 'input'].forEach(function (name) {
    document.addEventListener(name, function (event) {
      if (!event.isTrusted) return;
      const target = event.target;
      if (target && target.dataset) {
        const toggled = name === 'pointerdown' &&
          (/^(checkbox|radio)$/.test(target.type || '') || target.tagName === 'SELECT');
        if (name === 'input' || toggled) target.dataset.autonetTouched = '1';
      }
      interact();
    }, true);
  });
  window.addEventListener('scroll', function () { report('__VIEWPORT__'); }, true);
  window.addEventListener('resize', function () { report('__VIEWPORT__'); });
  const media = window.matchMedia('(prefers-reduced-motion: reduce)');
  function syncMotion() { state.reduced = forcedReduced || media.matches; }
  syncMotion();
  if (media.addEventListener) {
    media.addEventListener('change', function () {
      syncMotion();
      report('__REDUCED_MSG__' + (state.reduced ? '1' : '0'));
    });
  }
  window.__autonet = {
    get takeover() { return state.takeover; },
    step: function (labelText, x, y, hint, show) {
      state.step = labelText || '';
      state.hint = hint || '';
      if (!pointer) mount();
      if (show && isFinite(x) && isFinite(y)) {
        pointer.style.transition = state.reduced ? 'none' : 'transform 160ms ease, opacity 120ms ease';
        pointer.style.opacity = '1';
        pointer.style.transform = 'translate(' + (x - 4) + 'px,' + (y - 2) + 'px)';
      } else {
        pointer.style.opacity = '0';
      }
      paint();
    },
    // 只清除“正在手动操作”提示：接管与自动操作暂停状态保持不变（ADR-0019）。
    clearHint: function () { state.hintCleared = true; paint(); },
    resume: function () { state.takeover = false; state.hintCleared = false; paint(); }
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true });
  else mount();
})()
""".Replace("__REDUCED_BOOL__", reducedMotion ? "true" : "false")
        .Replace("__INTERACTION__", MessageInteraction)
        .Replace("__VIEWPORT__", MessageViewport)
        .Replace("__REDUCED_MSG__", MessageReducedMotionPrefix);

    /// <summary>导航完成后核对固定门户来源与登录表单；只读，不填表、不提交。</summary>
    public const string Verify = """
(() => {
  const trusted = location.protocol === 'https:' &&
    location.hostname.toLowerCase() === 'login.csust.edu.cn' &&
    (!location.port || location.port === '443');
  const password = document.querySelector('input[type=password]');
  const form = password ? password.closest('form') : null;
  return { origin: trusted, form: !!form };
})()
""";

    /// <summary>
    /// 单个步骤脚本：定位真实控件、更新覆盖层，然后执行动作。<paramref name="value"/> 只用于
    /// 账号与密码步骤，且由调用方先做 JSON 编码；覆盖层只显示固定步骤名，从不回显该值。
    /// </summary>
    public static string Step(string kind, string? value = null)
    {
        if (kind is not (Account or Password or Agreement or Submit))
            throw new ArgumentException("未知的认证页步骤。", nameof(kind));
        var template = """
(() => {
  const S = window.__autonet;
  if (!S) return { ok: false, reason: 'overlay-missing' };
  if (S.takeover) return { ok: false, reason: 'user-takeover' };
  const trusted = location.protocol === 'https:' &&
    location.hostname.toLowerCase() === 'login.csust.edu.cn' &&
    (!location.port || location.port === '443');
  if (!trusted) {
    S.step('', 0, 0, '页面来源不符合固定门户，已停止自动填写', false);
    return { ok: false, reason: 'untrusted-source' };
  }
  const kind = __KIND__;
  const password = document.querySelector('input[type=password]');
  const form = password ? password.closest('form') : null;
  if (!form) {
    S.step('', 0, 0, '未找到登录表单，已停止自动填写', false);
    return { ok: false, reason: 'form-missing' };
  }
  let target = null;
  if (kind === 'account') target = form.querySelector('input[type=text],input[type=email],input[type=tel],input:not([type])');
  else if (kind === 'password') target = password;
  else if (kind === 'agreement') target = form.querySelector('input[type=checkbox]');
  else target = form.querySelector('[type=submit],button,input[type=button]');
  const label = __LABEL__;
  if (!target) {
    if (kind === 'agreement') {
      S.step(label, 0, 0, '页面没有需要勾选的协议项，已跳过', false);
      return { ok: true, skipped: true, reason: 'nothing-to-do' };
    }
    S.step('', 0, 0, __MISSING__, false);
    return { ok: false, reason: kind === 'submit' ? 'no-submit-button' : 'no-target' };
  }
  const rect = target.getBoundingClientRect();
  const visible = rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < innerHeight;
  if (!visible) {
    S.step('', 0, 0, __OFFSCREEN__, false);
    return { ok: false, reason: 'offscreen' };
  }
  const x = rect.left + rect.width / 2;
  const y = rect.top + rect.height / 2;
  S.step(label, x, y, '', true);
  if (kind === 'agreement') {
    const all = Array.from(form.querySelectorAll('input[type=checkbox]'));
    const boxes = all.filter(function (box) { return !box.checked && box.dataset.autonetTouched !== '1'; });
    if (boxes.length === 0) {
      const kept = all.filter(function (box) { return !box.checked && box.dataset.autonetTouched === '1'; });
      if (kept.length > 0) {
        S.step(label, x, y, '已保留用户取消的协议项，跳过勾选', true);
        return { ok: true, skipped: true, reason: 'user-value', x: x, y: y };
      }
      S.step(label, x, y, '已完成：协议项原本已勾选', true);
      return { ok: true, skipped: true, reason: 'nothing-to-do', x: x, y: y };
    }
    for (let i = 0; i < boxes.length; i++) {
      boxes[i].checked = true;
      boxes[i].dispatchEvent(new Event('change', { bubbles: true }));
      if (!boxes[i].checked) {
        S.step(label, 0, 0, '协议勾选未生效，已停止自动提交', false);
        return { ok: false, reason: 'checkbox-rejected' };
      }
    }
    S.step(label, x, y, '已完成：勾选协议', true);
    return { ok: true, x: x, y: y };
  }
  if (kind === 'submit') {
    let action;
    try { action = new URL(form.getAttribute('action') || location.href, location.href); }
    catch (_) {
      S.step(label, 0, 0, '提交目标无法确认，已停止自动提交', false);
      return { ok: false, reason: 'untrusted-target' };
    }
    if (action.protocol !== 'https:' || action.hostname.toLowerCase() !== 'login.csust.edu.cn' ||
        (action.port && action.port !== '443') || action.username || action.password) {
      S.step(label, 0, 0, '提交目标不是固定门户，已停止自动提交', false);
      return { ok: false, reason: 'untrusted-target' };
    }
    target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    S.step(label, 0, 0, '已发起提交，等待认证结果', false);
    return { ok: true };
  }
  if (target.value || target.dataset.autonetTouched === '1') {
    S.step(label, x, y, '已保留已有内容，跳过填写', true);
    return { ok: true, skipped: true, reason: 'user-value', x: x, y: y };
  }
  target.value = __VALUE__;
  target.dispatchEvent(new Event('input', { bubbles: true }));
  target.dispatchEvent(new Event('change', { bubbles: true }));
  S.step(label, x, y, '已完成：' + label, true);
  return { ok: true, x: x, y: y };
})()
""";
        return template
            .Replace("__KIND__", JsonSerializer.Serialize(kind))
            .Replace("__LABEL__", JsonSerializer.Serialize(LabelFor(kind)))
            .Replace("__MISSING__", JsonSerializer.Serialize(MissingHintFor(kind)))
            .Replace("__OFFSCREEN__", JsonSerializer.Serialize(LabelFor(kind) + "的目标不在可视区域，改为步骤提示"))
            .Replace("__VALUE__", value ?? "''");
    }

    /// <summary>把覆盖层消息解析为受控事件类型；其他消息一律忽略。</summary>
    public static bool TryParseMessage(string? message, out AuthPageInteractionKind kind)
    {
        kind = default;
        if (message == MessageInteraction) { kind = AuthPageInteractionKind.TrustedInput; return true; }
        if (message == MessageViewport) { kind = AuthPageInteractionKind.ViewportChanged; return true; }
        if (message is not null && message.StartsWith(MessageReducedMotionPrefix, StringComparison.Ordinal))
        {
            kind = message.EndsWith('1') ? AuthPageInteractionKind.ReducedMotionOn : AuthPageInteractionKind.ReducedMotionOff;
            return true;
        }
        return false;
    }

    private static string LabelFor(string kind) => kind switch
    {
        Account => AuthPagePointerPolicy.Label(AuthPageStep.FillAccount),
        Password => AuthPagePointerPolicy.Label(AuthPageStep.FillPassword),
        Agreement => AuthPagePointerPolicy.Label(AuthPageStep.AcceptAgreement),
        _ => AuthPagePointerPolicy.Label(AuthPageStep.Submit)
    };

    private static string MissingHintFor(string kind) => kind switch
    {
        Account => "未找到账号输入框，已停止自动填写",
        Password => "未找到密码输入框，已停止自动填写",
        Submit => "未找到提交按钮，未提交凭据",
        _ => "未找到协议勾选项，已跳过"
    };
}
