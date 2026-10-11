using System.Diagnostics;
using System.Text.Json;
using AutoNet.Core;

namespace AutoNet.Tray;

internal sealed class MainForm : Form
{
    private readonly PictureBox statusIcon = new() { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(Theme.IconSize, Theme.IconSize), TabStop = false, AccessibleRole = AccessibleRole.StaticText };
    private readonly Label stateTitle = Ui.Text("正在读取状态…", 17, FontStyle.Bold, Theme.TitleText);
    private readonly Label stateExplanation = Ui.Text("", 10, FontStyle.Regular, Theme.BodyText, wrap: true);
    private readonly Label stateNext = Ui.Text("", 10, FontStyle.Regular, Theme.MutedText, wrap: true);
    private readonly Label stateTechnical = Ui.Text("", 9, FontStyle.Regular, Theme.MutedText, wrap: true);
    private readonly Panel additionalStrip = new() { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
    private readonly Dictionary<string, Label> values = [];
    private readonly Label quota = Ui.Text("", 10, FontStyle.Regular, Theme.BodyText);
    private readonly Label recovery = Ui.Text("", 10, FontStyle.Regular, Theme.BodyText);
    private readonly Label recoveryNote = Ui.Text("", 9, FontStyle.Regular, Theme.MutedText, wrap: true);
    private readonly Label lastRecovery = Ui.Text("", 10, FontStyle.Regular, Theme.BodyText);
    private readonly Label lastRecoveryNote = Ui.Text("", 9, FontStyle.Regular, Theme.MutedText, wrap: true);
    private readonly Label serviceStatus = Ui.Text("正在检测服务…", 9, FontStyle.Regular, Theme.MutedText);
    private readonly Button toggle = Ui.Button("暂停自动恢复");
    private readonly Button unblock = Ui.Button("解除认证暂停…");
    private string? currentIconKey;
    private NetworkAdditional? currentAdditional;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool AllowExit { get; set; }

    public MainForm()
    {
        Text = "AutoNet · 校园网连接守护";
        Font = Theme.Font(10);
        Icon = NetworkIcon.Create();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(760, 760);
        MinimumSize = new Size(600, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.WindowBackground;
        KeyPreview = true;

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 0,
            Padding = new Padding(Theme.PagePadding), BackColor = Theme.WindowBackground, Margin = new Padding(0)
        };

        page.Controls.Add(BuildHeader());
        page.Controls.Add(BuildHealthCard());
        page.Controls.Add(BuildRecoveryCard());
        page.Controls.Add(BuildChecksCard());
        page.Controls.Add(BuildActions());
        page.Controls.Add(Ui.Text("关闭窗口后继续守护；退出托盘不影响后台服务。", 9, FontStyle.Regular, Theme.MutedText));
        page.Controls.Add(Ui.Text("仅恢复校园网 · 主动断线有上限 · 真实认证与 UU/TUN 会话待实测验收", 9, FontStyle.Regular, Theme.MutedText));
        Controls.Add(page);

        FormClosing += (_, e) => { if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        RefreshStatus();
        Shown += (_, _) => CaptureForQa();
    }

    /// <summary>1. Brand plus Windows service state.</summary>
    private Control BuildHeader()
    {
        var card = Ui.Card();
        var stack = Ui.Stack(2);
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = Ui.Text("AutoNet  ·  校园网连接守护", 12, FontStyle.Bold, Theme.Brand);
        brand.AccessibleRole = AccessibleRole.StaticText;
        stack.Controls.Add(brand, 0, 0);
        // No AccessibleName here: it would override the Text a screen reader reads, freezing this at
        // "后台服务状态" while the visible text swaps between 运行中 / 已过期 / 未就绪.
        stack.Controls.Add(serviceStatus, 0, 1);

        var icon = new PictureBox { Image = NetworkIcon.CreateBitmap(48), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), TabStop = false, AccessibleRole = AccessibleRole.StaticText, AccessibleName = "AutoNet 标志" };
        stack.Controls.Add(icon, 1, 0);
        stack.SetRowSpan(icon, 2);

        card.Controls.Add(stack);
        card.Margin = new Padding(0, 0, 0, Theme.CardGap);
        return card;
    }

    /// <summary>2. Main network health area. Icon, title, one-line explanation, next step, optional technical detail.</summary>
    private Control BuildHealthCard()
    {
        var card = Ui.Card();
        var stack = Ui.Stack(2);
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.IconSize + 12));
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        stack.Controls.Add(statusIcon, 0, 0);
        stack.SetRowSpan(statusIcon, 5);

        // Deliberately no AccessibleName. WinForms prefers AccessibleName over Text, so a fixed value here
        // would replace the live state title — the primary status indicator would be invisible to a
        // screen-reader user, who would hear only the label "当前网络状态".
        stateTitle.AccessibleRole = AccessibleRole.StaticText;
        stack.Controls.Add(stateTitle, 1, 0);
        stack.Controls.Add(stateExplanation.Wrapping(card, Theme.IconSize + 12), 1, 1);
        stack.Controls.Add(stateNext.Wrapping(card, Theme.IconSize + 12), 1, 2);
        stack.Controls.Add(stateTechnical.Wrapping(card, Theme.IconSize + 12), 1, 3);

        additionalStrip.Margin = new Padding(0, 6, 0, 0);
        stack.Controls.Add(additionalStrip, 1, 4);

        card.Controls.Add(stack);
        card.Margin = new Padding(0, 0, 0, Theme.CardGap);
        return card;
    }

    /// <summary>3. Automatic recovery status and the active-disconnect quota, including the next allowed retry.</summary>
    private Control BuildRecoveryCard()
    {
        var card = Ui.Card();
        var stack = Ui.Stack(1);
        stack.Controls.Add(Ui.Text("自动恢复与主动断线额度", 11, FontStyle.Bold, Theme.TitleText));
        stack.Controls.Add(quota.Wrapping(card));
        stack.Controls.Add(recovery.Wrapping(card));
        stack.Controls.Add(recoveryNote.Wrapping(card));
        stack.Controls.Add(lastRecovery.Wrapping(card));
        stack.Controls.Add(lastRecoveryNote.Wrapping(card));
        card.Controls.Add(stack);
        card.Margin = new Padding(0, 0, 0, Theme.CardGap);
        return card;
    }

    /// <summary>4. Five diagnostic checks.</summary>
    private Control BuildChecksCard()
    {
        var card = Ui.Card();
        var stack = Ui.Stack(1);
        stack.Controls.Add(Ui.Text("网络诊断", 11, FontStyle.Bold, Theme.TitleText));

        var rows = Ui.Stack(2);
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        foreach (var name in new[] { "校园 Wi-Fi", "校园网认证", "校园网直连", "代理 / 系统路径", "UU 远程" })
        {
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = Ui.Text(name, 10, FontStyle.Regular, Theme.MutedText);
            caption.Anchor = AnchorStyles.Left;
            rows.Controls.Add(caption, 0, row);
            var value = Ui.Text("待检测", 10, FontStyle.Regular, Theme.BodyText).Wrapping(card, 130);
            value.Anchor = AnchorStyles.Left;
            rows.Controls.Add(value, 1, row++);
            values[name] = value;
        }
        stack.Controls.Add(rows);
        card.Controls.Add(stack);
        card.Margin = new Padding(0, 0, 0, Theme.CardGap);
        return card;
    }

    /// <summary>
    /// 5/6. Bottom action bar. Primary actions on the first row, privileged and destructive-capable
    /// actions on the second, so keyboard order matches how often each is used.
    /// </summary>
    private Control BuildActions()
    {
        var card = Ui.Card();
        var stack = Ui.Stack(1);
        var primary = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent, Margin = new Padding(0) };
        var secondary = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };

        var settings = Ui.Button("设置…", primary: true); settings.Click += (_, _) => EditSettings();
        toggle.Click += (_, _) => Toggle();
        var logs = Ui.Button("查看日志"); logs.Click += (_, _) => OpenLogs();
        var install = Ui.Button("安装服务…"); install.Click += (_, _) => InstallService();
        unblock.Click += (_, _) => UnblockAuthentication();

        primary.Controls.AddRange([settings, toggle, logs]);
        secondary.Controls.AddRange([install, unblock]);
        stack.Controls.Add(primary);
        stack.Controls.Add(secondary);
        card.Controls.Add(stack);
        return card;
    }

    public StatusSnapshot? RefreshStatus()
    {
        StatusSnapshot? status = null;
        var statusPath = Environment.GetEnvironmentVariable("AUTONET_QA_STATUS_PATH") ?? Storage.StatusPath;
        try { if (File.Exists(statusPath)) status = StatusSnapshotReader.Parse(Storage.ReadShared(statusPath)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }

        var age = status is null ? (TimeSpan?)null : DateTimeOffset.UtcNow - status.UpdatedAt;
        var fresh = StatusSnapshotReader.IsValid(status) && NetworkDisplayMap.IsFresh(age);
        var display = NetworkDisplayMap.Map(status, age);

        stateTitle.Text = display.Title;
        SetIcon(display.Primary);
        statusIcon.AccessibleName = display.Title;
        stateTitle.ForeColor = Theme.Palette(display.Primary).Fore;
        stateExplanation.Text = display.Explanation;
        stateNext.Text = display.NextStep;
        stateTechnical.Text = display.TechnicalDetail;
        stateTechnical.Visible = display.TechnicalDetail.Length > 0;
        RenderAdditional(display);

        values["校园 Wi-Fi"].Text = fresh ? status!.Wifi : "待检测";
        values["校园网认证"].Text = fresh ? status!.Portal : "未知";
        values["校园网直连"].Text = fresh ? status!.Direct : "待检测";
        values["代理 / 系统路径"].Text = fresh ? status!.Tunnel : "待检测";
        values["UU 远程"].Text = fresh ? status!.Uu : "待检测";
        foreach (var (name, value) in values) value.AccessibleName = name + "：" + value.Text;

        serviceStatus.Text = status is null ? "后台服务未就绪"
            : !StatusSnapshotReader.IsValid(status) ? "后台状态不可确认"
            : fresh ? $"后台服务运行中 · 最近检测 {status.UpdatedAt.ToLocalTime():HH:mm:ss}"
            : $"后台状态已过期 · 最后更新 {status.UpdatedAt.ToLocalTime():HH:mm:ss}";
        RenderQuota(status, fresh);
        RenderRecovery(status, fresh);

        toggle.Text = fresh && status!.Enabled == false ? "恢复自动恢复" : "暂停自动恢复";
        unblock.Enabled = fresh && status!.CredentialsBlocked;
        unblock.Text = fresh && status!.AuthenticationFailure == nameof(PortalFailure.BindingRejected)
            ? "查看认证诊断并解除…" : "解除认证暂停…";
        return fresh ? status : null;
    }

    private void RenderQuota(StatusSnapshot? status, bool fresh)
    {
        if (!fresh || status is null) { quota.Text = "主动断线额度：待检测"; recovery.Text = ""; recoveryNote.Text = ""; return; }
        var max = Math.Max(1, status.DisconnectBudgetMax);
        quota.Text = $"本次故障主动断线额度：{status.DisconnectAttempts}/{max} 次" +
            (status.DisconnectRequestUnconfirmed ? "（上次断线的完成状态未确认，已禁止继续断线）" : "");

        if (status.NextAttempt is { } next)
            recovery.Text = $"下次允许重试：{next.ToLocalTime():HH:mm:ss}（等待期间继续检测）";
        else if (status.Direct == "外网可达")
            recovery.Text = "下次允许重试：当前无需重试";
        else
            recovery.Text = "下次允许重试：正在观察，尚未排定";
        recoveryNote.Text = $"额度在连续在线 {status.DisconnectResetAfterSeconds} 秒后重置；每次断线请求至少间隔 {status.DisconnectCooldownSeconds} 秒。";
    }

    private void RenderRecovery(StatusSnapshot? status, bool fresh)
    {
        lastRecovery.Text = "";
        lastRecoveryNote.Text = "";
        if (!fresh || status is null) return;
        if (status.LastRecoveryAt is { } at)
        {
            var seconds = status.LastRecoverySeconds is { } s ? $"{s:0.##} 秒" : "耗时未知";
            lastRecovery.Text = $"最近一次恢复：{at.ToLocalTime():MM-dd HH:mm:ss} · 观测耗时 {seconds}";
            lastRecoveryNote.Text = "观测耗时从首次发现故障算起，只说明 AutoNet 观察到外网重新可达，"
                + "无法区分是程序自动恢复、人工处理还是网络自行恢复；真实端到端耗时需现场测量。";
        }
        else lastRecovery.Text = "尚无恢复记录。";
    }

    private void SetIcon(NetworkPrimaryState state)
    {
        var key = $"{state}/{Theme.IconSize}";
        if (key == currentIconKey) return;
        currentIconKey = key;
        // StatusIcon owns the bitmaps and caches them per state. Disposing one here would free an
        // instance the cache still hands out, so a later return to this state would assign a freed
        // bitmap to the PictureBox. Release happens once, centrally, when the tray exits.
        statusIcon.Image = StatusIcon.For(state, Theme.IconSize);
    }

    private void RenderAdditional(NetworkDisplay display)
    {
        var flags = display.Primary == NetworkPrimaryState.RecoveryPaused
            ? display.Additional & ~NetworkAdditional.RecoveryPaused : display.Additional;
        if (currentAdditional == flags) return;
        currentAdditional = flags;
        additionalStrip.SuspendLayout();
        // Clear only detaches controls. Dispose also releases the removed control tree's resources.
        while (additionalStrip.Controls.Count > 0) additionalStrip.Controls[0].Dispose();
        var chips = new List<(string Text, Color Fore, Color Surface)>();
        if (display.Additional.HasFlag(NetworkAdditional.RecoveryPaused) && display.Primary != NetworkPrimaryState.RecoveryPaused)
            chips.Add(("自动恢复已暂停", Theme.Warning, Theme.WarningSurface));
        if (display.Additional.HasFlag(NetworkAdditional.AuthenticationSuspended))
            chips.Add(("认证提交已暂停", Theme.Problem, Theme.ProblemSurface));
        if (display.Additional.HasFlag(NetworkAdditional.DisconnectQuotaExhausted))
            chips.Add(("主动断线额度已用尽", Theme.Warning, Theme.WarningSurface));
        if (display.Additional.HasFlag(NetworkAdditional.DisconnectUnconfirmed))
            chips.Add(("断线完成状态未确认", Theme.Problem, Theme.ProblemSurface));
        if (display.Additional.HasFlag(NetworkAdditional.ProxyFault))
            chips.Add(("代理 / 系统路径异常", Theme.Warning, Theme.WarningSurface));
        if (display.Additional.HasFlag(NetworkAdditional.EgressUnverified))
            chips.Add(("物理出口未核验", Theme.Neutral, Theme.NeutralSurface));

        if (chips.Count == 0) { additionalStrip.Visible = false; additionalStrip.ResumeLayout(); return; }
        additionalStrip.Visible = true;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var (text, fore, surface) in chips)
        {
            var chip = new Label
            {
                Text = text, AutoSize = true, Font = Theme.Font(9), ForeColor = fore, BackColor = surface,
                Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 6, 4), TabStop = false,
                AccessibleName = "附加状态：" + text, AccessibleRole = AccessibleRole.StaticText
            };
            flow.Controls.Add(chip);
        }
        additionalStrip.Controls.Add(flow);
        additionalStrip.ResumeLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Detach before releasing: the PictureBox must not outlive the bitmaps it points at.
            statusIcon.Image = null;
            StatusIcon.ReleaseAll();
        }
        base.Dispose(disposing);
    }

    public void EditSettings() => RunAction(() => Program.Elevate("--settings"));
    public void OpenLogs() => RunAction(() => { using var logs = new LogForm(); logs.ShowDialog(this); });
    public void Toggle() => RunAction(() => Program.Elevate("--toggle"));
    public void UnblockAuthentication() => RunAction(() => Program.Elevate("--unblock-auth"));
    private void InstallService() => RunAction(() =>
    {
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "install.ps1"));
        if (!File.Exists(script)) { MessageBox.Show("请使用发布包根目录中的 Install.cmd 安装后台服务。", "AutoNet"); return; }
        var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-LaunchSettings");
        Process.Start(start);
    });

    private static void RunAction(Action action)
    {
        try { action(); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }
        catch (Exception e) { MessageBox.Show("操作未完成：" + e.GetType().Name, "AutoNet", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static Button Button(string text) => Ui.Button(text);

    private void CaptureForQa() => QaCapture.Run(this);
}
