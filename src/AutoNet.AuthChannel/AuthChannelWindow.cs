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
    private const string ProbeScript =
        "(() => { const pw = document.querySelector('input[type=password]');" +
        "return { found: !!pw, ready: !!document.body }; })()";

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

    public int ExitCode { get; private set; } = 1;
    public bool HasTrustedNetworkBoundary => _runtimeInJob && _proxy?.IsRunning == true && _processJob is not null;
    public bool IsBoundTo(Settings settings, WifiLink link) => HasTrustedNetworkBoundary && settings == _proxySettings &&
        _proxyLink is { } bound && link.State == WifiState.Ready && bound.Index == link.Index &&
        Equals(bound.Address, link.Address) && bound.DnsServers.SequenceEqual(link.DnsServers) && RuntimeInJob();
    public int BrowserProcessId => checked((int)(_webView?.CoreWebView2?.BrowserProcessId ?? 0));
    public int HelperProcessId => Environment.ProcessId;
    public string ProcessJobName => _processJob?.Name ?? "";

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
        WriteResult(_options.ResultPath, conclusion,
            runtimeError.Length > 0 ? runtimeError : DetailFor(conclusion));
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
            await HumanAssistDelayAsync();
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

    /// <summary>保持门户页打开等人工完成；超时或用户关闭即退出（页面状态由学校侧会话承载）。</summary>
    private async Task HumanAssistDelayAsync()
    {
        var wait = _options.HumanWaitSeconds;
        if (wait <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(wait), _humanCancellationTokenSource.Token); }
        catch (OperationCanceledException) { /* 用户已关闭窗口 */ }
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
        File.WriteAllText(path, payload, new UTF8Encoding(false));
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
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, _) => navigation.TrySetResult();
        core.Navigate(_options.StartUrl);
        await navigation.Task.WaitAsync(timeout.Token);
        AuthChannelLogWriter.Append("AuthChannelStarted", "url=" + new Uri(_options.StartUrl).GetLeftPart(UriPartial.Path));

        var probe = await core.ExecuteScriptAsync(ProbeScript).WaitAsync(timeout.Token);
        if (!JsonDocument.Parse(probe).RootElement.GetProperty("found").GetBoolean())
        {
            return AuthChannelConclusion.Unknown;
        }

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
        var core = _webView!.CoreWebView2;
        _submissionStarted = true;
        var fillResult = await core.ExecuteScriptAsync(FillScript(credentials.Username, credentials.Password)).WaitAsync(ct);
        var fill = JsonDocument.Parse(fillResult).RootElement;
        if (!fill.GetProperty("ok").GetBoolean())
        {
            _submissionConclusion = AuthChannelConclusion.NeedsHuman;
            _humanAssisted = true;
            ShowForHuman();
            await HumanAssistDelayAsync();
            return _submissionConclusion;
        }
        _submissionConclusion = await PollOutcomeAsync(core, ct);
        return _submissionConclusion;
    }

    private string FillScript(string username, string password)
    {
        var user = JsonSerializer.Serialize(username);
        var pass = JsonSerializer.Serialize(password);
        return """
            (() => {
              if (location.protocol !== 'https:' || location.hostname.toLowerCase() !== 'login.csust.edu.cn' ||
                  (location.port && location.port !== '443')) return { ok: false, reason: 'untrusted-source' };
              const pw = document.querySelector('input[type=password]');
              if (!pw) { return { ok: false, reason: 'no-password-input' }; }
              const form = pw.closest('form') || document;
              if (form === document) return { ok: false, reason: 'no-form' };
              let target;
              try { target = new URL(form.getAttribute('action') || location.href, location.href); }
              catch { return { ok: false, reason: 'invalid-target' }; }
              if (target.protocol !== 'https:' || target.hostname.toLowerCase() !== 'login.csust.edu.cn' ||
                  (target.port && target.port !== '443') || target.username || target.password)
                  return { ok: false, reason: 'untrusted-target' };
              const user = form.querySelector('input[type=text],input[type=email],input[type=tel],input:not([type])');
              if (!user) { return { ok: false, reason: 'no-user-input' }; }
              user.value = __USER__;
              pw.value = __PASS__;
              const boxes = Array.from(form.querySelectorAll('input[type=checkbox]')).filter(b => !b.checked);
              for (const b of boxes) {
                b.checked = true;
                b.dispatchEvent(new Event('change', { bubbles: true }));
                if (!b.checked) { return { ok: false, reason: 'checkbox-rejected' }; }
              }
              const btn = form.querySelector('[type=submit],button,input[type=button]');
              if (!btn) { return { ok: false, reason: 'no-submit-button' }; }
              btn.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
              return { ok: true };
            })()
            """
            .Replace("__USER__", user)
            .Replace("__PASS__", pass);
    }

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
