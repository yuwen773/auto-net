using System.Text;
using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.AuthChannel;

/// <summary>
/// 隐身认证窗体（工单 #21 / ADR-0009）：默认屏幕外运行；WebView2 隐私会话
/// （临时用户目录，退出即弃）；自动填写账号密码与协议必选框并触发学校页面
/// 原有提交逻辑恰好一次；只读判定结果页三态；结果写入 --result 路径。
/// 验证码与不可识别结构 → NeedsHuman（人工处置与可见化属工单 #25）。
/// 异步链跑在 Application.Run 消息循环上（STA），完成后写结果、定退出码并关窗。
/// </summary>
internal sealed class AuthChannelWindow : Form
{
    private const string ProbeScript =
        "(() => { const pw = document.querySelector('input[type=password]');" +
        "return { found: !!pw, ready: !!document.body }; })()";

    private readonly ChannelOptions _options;
    private readonly CredentialSource _credential;
    private readonly string _sessionDir;
    private readonly string _startPath;
    private Microsoft.Web.WebView2.Core.CoreWebView2Environment? _environment;
    private Microsoft.Web.WebView2.WinForms.WebView2? _webView;
    private readonly TaskCompletionSource _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _humanCancellationTokenSource = new();

    public int ExitCode { get; private set; } = 1;

    /// <summary>最近一次运行失败的受控错误（类型名），供失败结果文件携带（工单 #25 回退链）。</summary>
    public string LastError { get; private set; } = "";

    public AuthChannelWindow(ChannelOptions options, CredentialSource credential)
    {
        _options = options;
        _credential = credential;
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
        // 同步清理（Main 返回前必须完成）：等 WebView2 浏览器子进程真正退出（上限 20s）再删会话目录。
        try { _webView?.Dispose(); } catch { /* 会话即弃：清理失败不影响结论 */ }
        _webView = null;
        _environment = null;
        try { _browserExited.Task.Wait(20000); } catch (AggregateException) { }
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
        WriteResult(_options.ResultPath, conclusion,
            runtimeError.Length > 0 ? runtimeError : DetailFor(conclusion));
        if (runtimeError.Length == 0)
        {
            AuthChannelLogWriter.Append("AuthChannelResult", "conclusion=" + conclusion);
        }
        if (conclusion == AuthChannelConclusion.NeedsHuman)
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

    private static string DetailFor(AuthChannelConclusion conclusion) => conclusion switch
    {
        AuthChannelConclusion.LoggedIn => "submit-once=done; outcome=logged-in",
        AuthChannelConclusion.NotLoggedIn => "submit-once=done; outcome=explicit-failure",
        AuthChannelConclusion.NeedsHuman => "submit-once=halted; outcome=needs-human",
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        _environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: _sessionDir);
        _webView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);
        await _webView.EnsureCoreWebView2Async(_environment).WaitAsync(timeout.Token);
        var core = _webView.CoreWebView2;
        _environment.BrowserProcessExited += (_, _) => _browserExited.TrySetResult();
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        if (_options.FixtureDir is not null)
        {
            core.SetVirtualHostNameToFolderMapping(
                _options.FixtureHost, _options.FixtureDir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, _) => navigation.TrySetResult();
        core.Navigate(_options.StartUrl);
        await navigation.Task.WaitAsync(timeout.Token);
        AuthChannelLogWriter.Append("AuthChannelStarted", "url=" + new Uri(_options.StartUrl).GetLeftPart(UriPartial.Path));

        var probe = await core.ExecuteScriptAsync(ProbeScript).WaitAsync(timeout.Token);
        if (!JsonDocument.Parse(probe).RootElement.GetProperty("found").GetBoolean())
        {
            return AuthChannelConclusion.NeedsHuman;
        }

        var fillResult = await core.ExecuteScriptAsync(FillScript(_credential.Username, _credential.Password))
            .WaitAsync(timeout.Token);
        var fill = JsonDocument.Parse(fillResult).RootElement;
        if (!fill.GetProperty("ok").GetBoolean())
        {
            // 勾选被页面拒绝、结构不可识别：一律停下转人工，绝不二次提交。
            return AuthChannelConclusion.NeedsHuman;
        }

        return await PollOutcomeAsync(core, timeout.Token);
    }

    private string FillScript(string username, string password)
    {
        var user = JsonSerializer.Serialize(username);
        var pass = JsonSerializer.Serialize(password);
        return """
            (() => {
              const pw = document.querySelector('input[type=password]');
              if (!pw) { return { ok: false, reason: 'no-password-input' }; }
              const form = pw.closest('form') || document;
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
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                && !string.Equals(parsed.AbsolutePath, _startPath, StringComparison.OrdinalIgnoreCase))
            {
                return AuthChannelConclusion.LoggedIn;
            }
            if (text.Contains("data-portal-error") || text.Contains("登录失败") || text.Contains("密码错误"))
            {
                return AuthChannelConclusion.NotLoggedIn;
            }
        }
        return AuthChannelConclusion.Unknown;
    }
}
