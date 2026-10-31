using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AutoNet.Core;
using AutoNet.Service;

namespace AutoNet.AuthChannel;

/// <summary>
/// 隐身认证窗体（工单 #21 / ADR-0009）：默认屏幕外运行；WebView2 隐私会话
/// （临时用户目录，退出即弃）；自动填写账号密码与协议必选框并触发学校页面
/// 原有提交逻辑恰好一次；只读判定结果页三态；结果写入 --result 路径。
/// 验证码与不可识别结构 → NeedsHuman（人工处置与可见化属工单 #25）。
/// 异步链跑在 Application.Run 消息循环上（STA），完成后写结果、定退出码并关窗。
/// </summary>
internal sealed class AuthChannelWindow : Form, IAuthChannelBrowser
{
    private readonly ChannelOptions _options;
    private readonly string _sessionDir;
    private readonly string _startPath;
    private Microsoft.Web.WebView2.Core.CoreWebView2Environment? _environment;
    private Microsoft.Web.WebView2.WinForms.WebView2? _webView;
    private AuthChannelProxy? _proxy;
    private ChannelProcessJob? _processJob;
    private WifiLink? _proxyLink;
    private Settings? _proxySettings;
    private readonly TaskCompletionSource _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _humanCancellationTokenSource = new();
    private bool _humanAssisted;
    private bool _submissionStarted;
    private bool _runtimeInJob;
    private IDisposable? _launchLease;
    private bool _launchDenied;
    private AuthChannelConclusion _submissionConclusion = AuthChannelConclusion.Unknown;
    // 工单 #40：虚拟指针只做视觉呈现；交互检测、接管与恢复复核状态全部留在本进程内。
    private readonly AuthPagePointerPolicy _pointer = new(!ClientAreaAnimationEnabled());
    private readonly System.Diagnostics.Stopwatch _pointerClock = System.Diagnostics.Stopwatch.StartNew();
    private bool _pageOriginTrusted;
    private bool _pageFormPresent;
    private bool _takeoverLogged;
    private bool _humanVisible;
    // 工单 #40 / ADR-0019：人工等待期间允许同一会话补写更新的结果（NeedsHuman → LoggedIn）。
    private bool _resultWritten;
    private AuthChannelConclusion? _lastWritten;
    private TimeSpan? _lastOnlineCheck;

    public int ExitCode { get; private set; } = 1;
    public bool HasTrustedNetworkBoundary => _runtimeInJob && _proxy?.IsRunning == true && _processJob is not null;
    public bool IsBoundTo(Settings settings, WifiLink link) => HasTrustedNetworkBoundary && settings == _proxySettings &&
        _proxyLink is { } bound && link.State == WifiState.Ready && bound.Index == link.Index &&
        Equals(bound.Address, link.Address) && bound.DnsServers.SequenceEqual(link.DnsServers) && RuntimeInJob();
    public int BrowserProcessId => checked((int)(_webView?.CoreWebView2?.BrowserProcessId ?? 0));
    public int HelperProcessId => Environment.ProcessId;
    public string ProcessJobName => _processJob?.Name ?? "";

    /// <summary>固定门户来源与登录表单的就绪状态；导航后由页面只读核对更新（工单 #40）。</summary>
    public AuthPageReadiness PageReadiness => new(_pageOriginTrusted, _pageFormPresent);

    /// <summary>用户手动操作期间为 true；恢复必须经过 <see cref="TryResumeAutoAsync"/> 的完整复核。</summary>
    public bool UserTakeoverActive => _pointer.TakeoverActive;

    private bool RuntimeInJob()
    {
        if (_processJob is null || _environment is null || BrowserProcessId <= 0 || !_processJob.Contains(BrowserProcessId)) return false;
        return _environment.GetProcessInfos().All(info => _processJob.Contains(checked((int)info.ProcessId)));
    }

    /// <summary>最近一次运行失败的受控错误（类型名），供失败结果文件携带（工单 #25 回退链）。</summary>
    public string LastError { get; private set; } = "";

    public AuthChannelWindow(ChannelOptions options)
    {
        _options = options;
        _sessionDir = Path.Combine(Path.GetTempPath(), "autonet-authch-" + Guid.NewGuid().ToString("N"));
        _startPath = new Uri(options.StartUrl).AbsolutePath;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1024, 768);
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FormClosing += (_, _) => _humanCancellationTokenSource.Cancel();
        _ = RunChannelAsync();
    }

    public void CleanupSession()
    {
        try
        {
            // 同步清理（Main 返回前必须完成）：等 WebView2 浏览器子进程真正退出（上限 20s）再删会话目录。
            var browserCreated = _environment is not null;
            try { _webView?.Dispose(); } catch { /* 会话即弃：清理失败不影响结论 */ }
            _webView = null;
            _environment = null;
            try { if (browserCreated) _browserExited.Task.Wait(20000); } catch (AggregateException) { }
            try { _proxy?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
            _proxy = null;
            if (browserCreated && !_browserExited.Task.IsCompleted) return;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(_sessionDir)) { Directory.Delete(_sessionDir, recursive: true); }
                    return;
                }
                catch (IOException) { Thread.Sleep(1500); }
                catch (UnauthorizedAccessException) { Thread.Sleep(1500); }
            }
        }
        finally
        {
            _launchLease?.Dispose();
            _launchLease = null;
        }
    }

    private async Task RunChannelAsync()
    {
        AuthChannelConclusion conclusion;
        var runtimeError = string.Empty;
        try
        {
            conclusion = await ExecuteAsync();
        }
        catch (Exception ex)
        {
            conclusion = AuthChannelConclusion.Unknown;
            runtimeError = "error=" + ex.GetType().Name;
            Console.Error.WriteLine(runtimeError);
            LastError = runtimeError;
        }
        if (_launchDenied)
        {
            // 另一辅助进程仍持有启动锁：不写结果文件，避免覆盖它的回执。
            ExitCode = 4;
            BeginInvoke(Close);
            return;
        }
        // 工单 #40 / ADR-0019：人工路径可能已经写过 NeedsHuman；错误必须覆盖，成功只补写一次。
        PublishResult(conclusion,
            runtimeError.Length > 0 ? runtimeError : null, force: runtimeError.Length > 0);
        if (runtimeError.Length == 0)
        {
            AuthChannelLogWriter.Append("AuthChannelResult", "conclusion=" + conclusion);
        }
        if (conclusion == AuthChannelConclusion.NeedsHuman && !_humanAssisted)
        {
            // 工单 #25：验证码 / 勾选拒绝 / 结构不可识别 → 窗口自动可见化，页面保持打开
            // 等人工完成（自动提交在 NeedsHuman 判定前就已停止，每页一次提交约束不变）。
            AuthChannelLogWriter.Append("AuthChannelNeedsHuman", "window-shown=true; auto-submit=halted");
            BeginInvoke(ShowForHuman);
            if (await WaitForHumanAsync(AuthChannelConclusion.NeedsHuman) == AuthChannelConclusion.LoggedIn)
            {
                conclusion = AuthChannelConclusion.LoggedIn;
                PublishResult(conclusion);
            }
        }
        ExitCode = conclusion switch
        {
            AuthChannelConclusion.LoggedIn => 0,
            AuthChannelConclusion.NotLoggedIn => 3,
            _ => 4,
        };
        if (runtimeError.Length > 0) { ExitCode = 1; }
        BeginInvoke(Close);
    }

    private void ShowForHuman()
    {
        // 窗体由 Application.Run 启动时即处于“可见但屏幕外”状态；只在首次需要人工时搬到屏幕内，
        // 避免用户操作过程中反复居中、抢焦点或改变窗口位置。
        if (_humanVisible) return;
        _humanVisible = true;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        Location = new Point(
            Math.Max(0, area.Left + (area.Width - Width) / 2),
            Math.Max(0, area.Top + (area.Height - Height) / 2));
        TopMost = true;
        Show();
        Activate();
    }

    /// <summary>人工等待期间保持会话存活并只读观察；观察到登录成功即提前结束（ADR-0019）。</summary>
    private async Task<AuthChannelConclusion> WaitForHumanAsync(AuthChannelConclusion fallback)
    {
        if (_options.HumanWaitSeconds <= 0) return fallback;
        var wait = new AuthChannelHumanWait(
            PollHumanOutcomeAsync,
            (interval, ct) => Task.Delay(interval, ct),
            TimeProvider.System);
        return await wait.ObserveAsync(
            fallback, TimeSpan.FromSeconds(_options.HumanWaitSeconds), _humanCancellationTokenSource.Token);
    }

    /// <summary>
    /// 只读观察人工结果：绑定校园网卡做外网内容探测，最多每 10 秒一次；确认在线才报告已登录。
    /// 不提交凭据、不改变共享额度、不写认证状态。
    /// </summary>
    private async Task<AuthChannelConclusion?> PollHumanOutcomeAsync(CancellationToken ct)
    {
        try
        {
            if (_proxyLink is not { } link) return null;
            if (_lastOnlineCheck is { } last && _pointerClock.Elapsed - last < TimeSpan.FromSeconds(10)) return null;
            _lastOnlineCheck = _pointerClock.Elapsed;
            using var http = DirectNetwork.CreateClient(link);
            return await HealthChecks.InternetAsync(http, ct) ? AuthChannelConclusion.LoggedIn : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>写结果文件；同一会话可补写更新的结论，但相同结论不重复写。</summary>
    private void PublishResult(AuthChannelConclusion conclusion, string? detail = null, bool force = false)
    {
        if (!force && _resultWritten && _lastWritten == conclusion) return;
        WriteResult(_options.ResultPath, conclusion, detail ?? DetailFor(conclusion));
        _resultWritten = true;
        _lastWritten = conclusion;
    }

    internal static void WriteFailureResult(string path, string error)
    {
        var payload = JsonSerializer.Serialize(new
        {
            conclusion = "Unknown",
            detectedAt = DateTimeOffset.UtcNow.ToString("o"),
            detail = error,
        });
        File.WriteAllText(path, payload, new UTF8Encoding(false));
    }

    private string DetailFor(AuthChannelConclusion conclusion) => conclusion switch
    {
        AuthChannelConclusion.LoggedIn => "submit-once=done; outcome=logged-in",
        AuthChannelConclusion.NotLoggedIn => "submit-once=done; outcome=explicit-failure",
        AuthChannelConclusion.NeedsHuman => "submit-once=halted; outcome=needs-human",
        _ when !_submissionStarted && !HasTrustedNetworkBoundary => "error=NetworkBoundaryUnverified; submit-once=not-authorized",
        _ when !_submissionStarted => "submit-once=not-authorized; outcome=unknown",
        _ => "submit-once=done; outcome=unknown",
    };

    private static void WriteResult(string path, AuthChannelConclusion conclusion, string detail)
    {
        var payload = JsonSerializer.Serialize(new
        {
            conclusion = conclusion.ToString(),
            detectedAt = DateTimeOffset.UtcNow.ToString("o"),
            detail,
        });
        // 人工等待期间可能补写第二条结果；先写临时文件再原子替换，避免托盘读到半截。
        var temp = path + ".tmp";
        File.WriteAllText(temp, payload, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private async Task<AuthChannelConclusion> ExecuteAsync()
    {
        if (!_options.FromSettings || _options.FixtureDir is not null ||
            !Uri.TryCreate(_options.StartUrl, UriKind.Absolute, out var portal) || !Settings.IsSupportedPortal(portal))
            return AuthChannelConclusion.Unknown;
        // 托盘重启后仍运行的辅助进程用这条锁阻止第二个通道启动；进程退出即由系统释放。
        _launchLease = new SafetyStateStore(Storage.Root).TryAcquireChannelLaunch();
        if (_launchLease is null)
        {
            _launchDenied = true;
            return AuthChannelConclusion.Unknown;
        }
        var settings = Storage.LoadSettings();
        if (settings is null || !settings.Enabled || settings.AuthChannelMode == AuthChannelModes.Off || settings.PortalOrigin != _options.StartUrl)
            return AuthChannelConclusion.Unknown;
        settings.Validate();
        var link = Wifi.Read(settings);
        if (link.State != WifiState.Ready) return AuthChannelConclusion.Unknown;
        _processJob = ChannelProcessJob.CreateForCurrentProcess();
        _proxy = await AuthChannelProxy.StartAsync(link, _processJob);
        _proxySettings = settings;
        _proxyLink = link;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_humanCancellationTokenSource.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds + 120));
        var browserOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(
            $"--proxy-server=http://127.0.0.1:{_proxy.Port} --proxy-bypass-list=<-loopback> --disable-quic --disable-background-networking");
        _environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: _sessionDir, options: browserOptions);
        _webView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        await _webView.EnsureCoreWebView2Async(_environment).WaitAsync(timeout.Token);
        var core = _webView.CoreWebView2;
        _environment.BrowserProcessExited += (_, _) => _browserExited.TrySetResult();
        _runtimeInJob = RuntimeInJob();
        if (!HasTrustedNetworkBoundary) return AuthChannelConclusion.Unknown;
        _environment.ProcessInfosChanged += (_, _) => { if (!RuntimeInJob()) _runtimeInJob = false; };
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // 工单 #40：覆盖层只上报“发生过受信任输入”与位置变化，不携带按键、坐标或输入值。
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += (_, _) =>
        {
            _pageOriginTrusted = false;
            _pageFormPresent = false;
            _pointer.NoteNavigation();
        };
        core.NavigationCompleted += (_, _) => _ = VerifyPageSafelyAsync();
        // 每个文档创建时安装虚拟指针与交互检测；系统启用“减少动画”时直接以静态状态呈现。
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            AuthPagePointerScript.Install(_pointer.ReducedMotion)).WaitAsync(timeout.Token);
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, _) => navigation.TrySetResult();
        core.Navigate(_options.StartUrl);
        await navigation.Task.WaitAsync(timeout.Token);
        AuthChannelLogWriter.Append("AuthChannelStarted", "url=" + new Uri(_options.StartUrl).GetLeftPart(UriPartial.Path));

        // 填表前先核对固定门户来源与登录表单；只读，不提交。页面不成立时不继续自动操作。
        if (!await VerifyPageAsync().WaitAsync(timeout.Token)) return AuthChannelConclusion.Unknown;

        using var environment = new MonitorEnvironment();
        var workflow = new AuthChannelWorkflow(environment, this, TimeProvider.System);
        ChannelWorkflowOutcome outcome;
        do
        {
            outcome = await workflow.RunOnceAsync(timeout.Token);
            if (outcome == ChannelWorkflowOutcome.Waiting) await Task.Delay(1000, timeout.Token);
        } while (outcome == ChannelWorkflowOutcome.Waiting);
        return outcome == ChannelWorkflowOutcome.Submitted ? _submissionConclusion : AuthChannelConclusion.Unknown;
    }

    public async Task<AuthChannelConclusion> SubmitAsync(Credentials credentials, CancellationToken ct)
    {
        // 复核与提交之间用户开始操作：不填表、不提交；结果保守按未知处理，但保持会话
        // 观察人工结果（ADR-0019）。
        if (_pointer.TakeoverActive)
        {
            BeginHumanAssist();
            var observed = await WaitForHumanAsync(AuthChannelConclusion.Unknown);
            if (observed != AuthChannelConclusion.LoggedIn) return AuthChannelConclusion.Unknown;
            PublishResult(observed);
            return observed;
        }
        var core = _webView!.CoreWebView2;
        // 复核已经通过：清除页面的接管标记，开始真实动作。若用户在此之后再次操作，
        // 页面会在下一个步骤前自行停止并上报。
        await ResumePointerAsync();
        var stopped = await RunPointerSequenceAsync(core, credentials, ct);
        if (stopped is { } conclusion) return conclusion;
        _submissionConclusion = await PollOutcomeAsync(core, ct);
        return _submissionConclusion;
    }

    /// <summary>
    /// 依次执行账号、密码、协议与提交四个真实步骤；每一步都只在真实控件可用时显示指针。
    /// 返回 null 表示已发起提交，继续等待结果；返回结论表示在提交前停止。
    /// </summary>
    private async Task<AuthChannelConclusion?> RunPointerSequenceAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core, Credentials credentials, CancellationToken ct)
    {
        var account = await ExecutePointerStepAsync(
            core, AuthPageStep.FillAccount, AuthPagePointerScript.Account, credentials.Username, ct);
        if (account.Takeover) return await BeginTakeoverAsync();
        if (!account.Ok) return await StopForHumanAsync(AuthPageStep.FillAccount, account.Reason);
        NoteStep(account, AuthPageStep.FillAccount);

        var password = await ExecutePointerStepAsync(
            core, AuthPageStep.FillPassword, AuthPagePointerScript.Password, credentials.Password, ct);
        if (password.Takeover) return await BeginTakeoverAsync();
        if (!password.Ok) return await StopForHumanAsync(AuthPageStep.FillPassword, password.Reason);
        NoteStep(password, AuthPageStep.FillPassword);

        var agreement = await ExecutePointerStepAsync(
            core, AuthPageStep.AcceptAgreement, AuthPagePointerScript.Agreement, null, ct);
        if (agreement.Takeover) return await BeginTakeoverAsync();
        if (!agreement.Ok) return await StopForHumanAsync(AuthPageStep.AcceptAgreement, agreement.Reason);
        NoteStep(agreement, AuthPageStep.AcceptAgreement);

        var submit = await ExecutePointerStepAsync(
            core, AuthPageStep.Submit, AuthPagePointerScript.Submit, null, ct);
        if (submit.Takeover) return await BeginTakeoverAsync();
        if (!submit.Ok) return await StopForHumanAsync(AuthPageStep.Submit, submit.Reason);
        // 点击已发生：从这里开始按“可能已发送”计。
        _submissionStarted = true;
        _pointer.AwaitResult();
        return null;
    }

    /// <summary>把脚本结果投影回策略；跳过与失败都不显示指针，也不声称成功。</summary>
    private void NoteStep(PointerStepOutcome outcome, AuthPageStep step)
    {
        if (outcome.Skipped) _pointer.SkipStep(step, outcome.Reason);
        else _pointer.CompleteStep(step, outcome.Target);
    }

    private readonly record struct PointerStepOutcome(
        bool Ok, bool Skipped, bool Takeover, AuthPageStepReason Reason, AuthPageTarget Target);

    private async Task<PointerStepOutcome> ExecutePointerStepAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core, AuthPageStep step, string kind, string? value, CancellationToken ct)
    {
        var script = AuthPagePointerScript.Step(kind, value is null ? null : JsonSerializer.Serialize(value));
        try
        {
            var json = await core.ExecuteScriptAsync(script).WaitAsync(ct);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            var reasonText = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
            if (reasonText == "user-takeover")
                return new(false, false, true, AuthPageStepReason.UserTakeover, AuthPageTarget.None);
            var skipped = root.TryGetProperty("skipped", out var skippedElement) && skippedElement.ValueKind == JsonValueKind.True;
            var target = root.TryGetProperty("x", out var x) && root.TryGetProperty("y", out var y) &&
                x.TryGetDouble(out var dx) && y.TryGetDouble(out var dy)
                ? AuthPageTarget.At(dx, dy)
                : AuthPageTarget.None;
            return new(ok, skipped, false, AuthPagePointerPolicy.ParseReason(reasonText), target);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // 脚本结果缺失或不是对象时按受控失败处理，不把浏览器异常当成提交结果。
            return new(false, false, false, AuthPageStepReason.TargetUnavailable, AuthPageTarget.None);
        }
    }

    private async Task<AuthChannelConclusion> BeginTakeoverAsync()
    {
        _pointer.NoteInteraction(_pointerClock.Elapsed);
        BeginHumanAssist();
        var observed = await WaitForHumanAsync(AuthChannelConclusion.Unknown);
        if (observed != AuthChannelConclusion.LoggedIn) return AuthChannelConclusion.Unknown;
        PublishResult(observed);
        return observed;
    }

    private async Task<AuthChannelConclusion> StopForHumanAsync(AuthPageStep step, AuthPageStepReason reason)
    {
        _pointer.FailStep(step, reason);
        _submissionConclusion = AuthChannelConclusion.NeedsHuman;
        _humanAssisted = true;
        // 先立即写入并通知，再保持会话观察人工结果；成功时补写更新的结论（ADR-0019）。
        PublishResult(AuthChannelConclusion.NeedsHuman);
        BeginHumanAssist();
        var observed = await WaitForHumanAsync(AuthChannelConclusion.NeedsHuman);
        if (observed != AuthChannelConclusion.LoggedIn) return AuthChannelConclusion.NeedsHuman;
        _submissionConclusion = observed;
        PublishResult(observed);
        return observed;
    }

    /// <summary>显示通道窗口，让用户看到当前步骤或接管状态；已关闭时忽略。</summary>
    private void BeginHumanAssist()
    {
        try
        {
            if (IsDisposed || _humanVisible) return;
            if (IsHandleCreated) BeginInvoke(ShowForHuman);
            else ShowForHuman();
        }
        catch (Exception) { }
    }

    /// <summary>复核通过后清除页面接管标记；只在调用方已确认可以恢复时调用。</summary>
    private async Task ResumePointerAsync()
    {
        if (_webView?.CoreWebView2 is not { } core) return;
        try { await core.ExecuteScriptAsync("window.__autonet && window.__autonet.resume();"); }
        catch (Exception) { }
    }

    /// <summary>只读核对固定门户来源与登录表单；导航后与恢复自动操作前各核对一次。</summary>
    private async Task<bool> VerifyPageAsync(CancellationToken ct = default)
    {
        if (_webView?.CoreWebView2 is not { } core) return false;
        try
        {
            var json = await core.ExecuteScriptAsync(AuthPagePointerScript.Verify)
                .WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            _pageOriginTrusted = root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.True;
            _pageFormPresent = root.TryGetProperty("form", out var form) && form.ValueKind == JsonValueKind.True;
            return _pageOriginTrusted && _pageFormPresent;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            _pageOriginTrusted = false;
            _pageFormPresent = false;
            return false;
        }
    }

    private async Task VerifyPageSafelyAsync()
    {
        try { await VerifyPageAsync(); }
        catch (Exception) { }
    }

    /// <summary>页面受控消息：只接受固定门户来源的交互、位置变化与减少动画事件。</summary>
    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !Settings.IsPortalOrigin(source)) return;
            if (!AuthPagePointerScript.TryParseMessage(e.TryGetWebMessageAsString(), out var kind)) return;
            switch (kind)
            {
                case AuthPageInteractionKind.TrustedInput:
                    // 每次受信任输入都重新计时：用户再次操作必须重新等待完整的 15 秒。
                    _pointer.NoteInteraction(_pointerClock.Elapsed);
                    if (!_takeoverLogged)
                    {
                        _takeoverLogged = true;
                        AuthChannelLogWriter.Append("AuthChannelTakeover",
                            "trigger=trusted-input; auto-fill=paused; auto-submit=paused");
                        BeginHumanAssist();
                    }
                    break;
                case AuthPageInteractionKind.ViewportChanged:
                    _pointer.NoteViewportChanged();
                    break;
                case AuthPageInteractionKind.ReducedMotionOn:
                    _pointer.NoteReducedMotion(true);
                    break;
                case AuthPageInteractionKind.ReducedMotionOff:
                    _pointer.NoteReducedMotion(false);
                    break;
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// 接管空闲满 15 秒后，结合调用方重新核对的事实与页面自己的来源/表单检查决定是否恢复。
    /// 任一条件不成立都保持接管；空闲本身不构成恢复理由。
    /// </summary>
    public async Task<bool> TryResumeAutoAsync(bool network, bool authentication, bool budget, CancellationToken ct)
    {
        if (!_pointer.TakeoverActive) return true;
        // 先看空闲时间，避免用户仍在输入时每秒都执行一次页面脚本。
        if (_pointer.IdleFor(_pointerClock.Elapsed) < AuthPagePointerPolicy.ResumeIdle) return false;
        if (!await VerifyPageAsync(ct).ConfigureAwait(true)) return false;
        // 页面来源与表单只采用刚刚核对的结果；调用方不提供这两项，避免旧快照参与判断。
        var recheck = new AuthPageRecheck(network, authentication, _pageOriginTrusted, _pageFormPresent, budget);
        if (!recheck.All) return false;
        if (!_pointer.TryResume(_pointerClock.Elapsed, recheck)) return false;
        await ResumePointerAsync().ConfigureAwait(true);
        _takeoverLogged = false;
        AuthChannelLogWriter.Append("AuthChannelTakeover",
            "auto-ops=resumed; recheck=network,authentication,origin,form,budget");
        return true;
    }

    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>
    /// 读取 Windows“在 Windows 中显示动画”设置；读取失败按允许动画处理。
    /// 页面脚本同时尊重 <c>prefers-reduced-motion</c>，两者任一要求减少动画即显示静态状态。
    /// </summary>
    private static bool ClientAreaAnimationEnabled()
    {
        try
        {
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && enabled;
        }
        catch (Exception)
        {
            return true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

    private async Task<AuthChannelConclusion> PollOutcomeAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2 core, CancellationToken token)
    {
        const string outcomeScript = """
            (() => ({
              url: location.href,
              text: (document.body ? document.body.innerText : '').slice(0, 800)
            }))()
            """;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(500, token);
            var outcome = JsonDocument.Parse(await core.ExecuteScriptAsync(outcomeScript).WaitAsync(token)).RootElement;
            var url = outcome.GetProperty("url").GetString() ?? string.Empty;
            var text = outcome.GetProperty("text").GetString() ?? string.Empty;
            if (AuthChannelReceipt.ClassifyPage(url, text) == AuthChannelConclusion.NotLoggedIn)
                return AuthChannelConclusion.NotLoggedIn;
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && !string.Equals(parsed.AbsolutePath, _startPath, StringComparison.OrdinalIgnoreCase))
            {
                return AuthChannelConclusion.Unknown;
            }
        }
        return AuthChannelConclusion.Unknown;
    }
}
