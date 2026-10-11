using AutoNet.Core;

namespace AutoNet.Tray;

/// <summary>
/// Log centre: newest-first timeline, category filter, and a separate technical detail pane.
/// Bounded by <see cref="LogTailReader.DefaultMaxRecords"/> rather than by VirtualMode — see the note
/// on the record count in <see cref="ApplyFilter"/>.
/// </summary>
internal sealed class LogForm : Form
{
    private readonly ComboBox date = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "日志日期" };
    private readonly ComboBox filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "事件筛选" };
    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Theme.Card,
        ForeColor = Theme.BodyText, Font = Theme.Font(10), GridLines = false, AccessibleName = "中文事件日志"
    };
    private readonly TextBox detail = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.BodyText,
        Font = Theme.Font(10), AccessibleName = "技术诊断详情", TabStop = true
    };
    private readonly Label summary = Ui.Text("", 9, FontStyle.Regular, Theme.MutedText);

    private readonly List<ActivityLog> loaded = [];
    private readonly List<ActivityLog> visible = [];
    private int generation;
    private bool loading;
    private bool disposed;
    /// <summary>The in-flight tail read, so the QA capture can wait for it instead of shooting an empty list.</summary>
    private Task pending = Task.CompletedTask;

    private static readonly (string Label, Func<ActivityLog, bool> Match)[] Filters =
    [
        ("全部事件", _ => true),
        ("状态变化", e => e.Action.Contains("恢复") || e.Action.Contains("异常") || e.Action.Contains("启动") || e.Action.Contains("停止")),
        ("恢复动作", e => e.Action.Contains("Wi-Fi")),
        ("认证相关", e => e.Action.Contains("认证")
            || (e.AuthenticationStage ?? nameof(PortalStage.Unknown)) != nameof(PortalStage.Unknown)
            || (e.AuthenticationFailure ?? nameof(PortalFailure.None)) != nameof(PortalFailure.None))
    ];

    public LogForm()
    {
        Text = "AutoNet · 日志中心";
        Font = Theme.Font(10);
        Icon = NetworkIcon.Create();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(1000, 700);
        // Wide enough for the toolbar's natural width (~800px). An AutoSize FlowLayoutPanel grows
        // sideways rather than wrapping, so a narrower minimum would clip the last button.
        MinimumSize = new Size(840, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.WindowBackground;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0, BackColor = Theme.WindowBackground, Padding = new Padding(Theme.PagePadding) };

        // Toolbar
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, Theme.CardGap) };
        toolbar.Controls.Add(Ui.Text("日志日期", 10, FontStyle.Regular, Theme.MutedText));
        date.SelectedIndexChanged += (_, _) => { if (!loading) LoadEntries(); };
        toolbar.Controls.Add(date);
        toolbar.Controls.Add(Ui.Text("筛选", 10, FontStyle.Regular, Theme.MutedText));
        filter.Items.AddRange([.. Filters.Select(f => f.Label)]);
        filter.SelectedIndex = 0;
        filter.SelectedIndexChanged += (_, _) => { if (!loading) ApplyFilter(); };
        toolbar.Controls.Add(filter);
        var refresh = Ui.Button("刷新"); refresh.Click += (_, _) => LoadEntries();
        var copy = Ui.Button("复制脱敏摘要"); copy.Click += (_, _) => CopySummary();
        var folder = Ui.Button("日志目录"); folder.Click += (_, _) => Program.OpenFolder(Storage.Logs);
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(copy);
        toolbar.Controls.Add(folder);
        root.Controls.Add(toolbar);

        // Timeline + detail, side by side inside one card
        var card = Ui.CardFill();
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        list.Columns.Add("时间", 80);
        list.Columns.Add("事件", 150);
        list.Columns.Add("说明", 240);
        list.SizeChanged += (_, _) => FitColumns();
        list.SelectedIndexChanged += (_, _) => ShowDetail();

        // A Fill-docked pane needs real row styles; an AutoSize stack would collapse the detail box.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(Theme.CardGap, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(Ui.Text("技术诊断详情", 11, FontStyle.Bold, Theme.TitleText), 0, 0);
        right.Controls.Add(detail, 0, 1);
        right.Controls.Add(summary, 0, 2);

        columns.Controls.Add(list, 0, 0);
        columns.Controls.Add(right, 1, 0);
        card.Controls.Add(columns);
        root.Controls.Add(card);

        Controls.Add(root);
        LoadEntries();
        Shown += (_, _) => CaptureForQa();
    }

    private static string LogDirectory => Environment.GetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY") ?? Storage.Logs;

    /// <summary>
    /// Distributes the columns across whatever width the list actually has. Fixed pixel widths would
    /// leave a dead gap when the window is enlarged or scaled, and a horizontal scrollbar — which reads as
    /// "there is more to the right" when there is not. Measured from ClientSize, so it adapts to DPI
    /// without a per-scale table.
    /// </summary>
    private void FitColumns()
    {
        if (list.Columns.Count != 3) return;
        var width = Math.Max(240, list.ClientSize.Width);
        var clock = Math.Min(96, width / 6);
        var rest = width - clock;
        list.Columns[0].Width = clock;
        list.Columns[1].Width = (int)(rest * 0.38);
        list.Columns[2].Width = Math.Max(60, rest - list.Columns[1].Width);
    }

    /// <summary>
    /// Last-write-wins. Each load takes a new generation; a read that loses the race returns without
    /// touching any control, so switching dates quickly cannot leave a stale result on screen or stack up
    /// unbounded background work. The read itself is a bounded tail read, never a whole-file load.
    /// </summary>
    private async void LoadEntries()
    {
        if (disposed) return;
        var mine = ++generation;
        try
        {
            var directory = LogDirectory;
            var files = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "autonet-????-??-??.jsonl").Select(Path.GetFileName)
                    .OfType<string>().OrderDescending().ToList()
                : new List<string>();

            loading = true;
            var previous = date.SelectedItem as string;
            date.Items.Clear();
            date.Items.AddRange([.. files]);
            loading = false;
            if (files.Count == 0)
            {
                date.SelectedIndex = -1;
                loaded.Clear();
                ApplyFilter();
                detail.Text = "该目录还没有日志文件。";
                return;
            }
            var index = previous is null ? -1 : files.IndexOf(previous);
            loading = true;
            date.SelectedIndex = index < 0 ? 0 : index;
            loading = false;
            var path = Path.Combine(directory, files[date.SelectedIndex]);

            list.Items.Clear();
            detail.Text = "正在读取…";
            var read = Task.Run(() => LogTailReader.Read(path));
            pending = read;
            var records = await read;
            if (disposed || mine != generation) return;
            loaded.Clear();
            loaded.AddRange(records);
            ApplyFilter();
        }
        catch (Exception e)
        {
            // Deliberately broad. This is an async void handler, so anything not caught here reaches the
            // WinForms message loop and takes the whole tray process down — and the likely triggers are
            // not only I/O: a log path that vanished, a malformed date in the file name, or a decoding
            // edge case all surface here. Only the type name is shown; the message could carry a path.
            if (disposed || mine != generation) return;
            loaded.Clear();
            ApplyFilter();
            detail.Text = "暂时无法读取该日志文件；可稍后刷新重试。";
            summary.Text = "读取失败：" + e.GetType().Name;
        }
        finally { loading = false; }
    }

    private void ApplyFilter()
    {
        var selected = filter.SelectedIndex;
        var predicate = selected >= 0 && selected < Filters.Length ? Filters[selected].Match : Filters[0].Match;
        visible.Clear();
        foreach (var entry in loaded) if (predicate(entry)) visible.Add(entry);
        list.BeginUpdate();
        // Ordinary items keep the bounded list compatible with DrawToBitmap captures. VirtualMode
        // supports VirtualListSize, but its rows are not rendered by this capture method.
        list.Items.Clear();
        foreach (var entry in visible)
            list.Items.Add(new ListViewItem(entry.Time.ToLocalTime().ToString("HH:mm:ss")) { SubItems = { entry.Action, Preview(entry.Detail) } });
        list.EndUpdate();
        summary.Text = $"显示 {visible.Count} 条 / 已读取 {loaded.Count} 条（单日最多 {LogTailReader.DefaultMaxRecords} 条）";
        if (visible.Count > 0) { list.Items[0].Selected = true; ShowDetail(); }
        else detail.Text = "没有符合筛选条件的事件。";
    }

    /// <summary>One-line preview of the service-authored description, trimmed to keep the row readable.</summary>
    private static string Preview(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "";
        var line = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 56 ? line : line[..56] + "…";
    }

    private void ShowDetail()
    {
        if (list.SelectedIndices.Count == 0 || list.SelectedIndices[0] >= visible.Count)
        {
            detail.Text = visible.Count == 0 ? "没有符合筛选条件的事件。" : "请选择一个事件查看技术诊断详情。";
            return;
        }
        var entry = visible[list.SelectedIndices[0]];
        if (entry.SchemaVersion >= 2)
        {
            detail.Text = entry.ToReadableText();
            return;
        }
        var lines = new List<string>
        {
            $"时间：{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"事件：{entry.Action}",
            $"Wi-Fi：{entry.Wifi ?? "未知"}",
            $"认证：{entry.Portal ?? "未知"}",
            $"直连：{entry.Direct ?? "未知"}",
            $"自动动作：{entry.Attempts ?? 0} 次　主动断线：{entry.DisconnectAttempts ?? 0} 次"
        };
        if (entry.Seconds is { } seconds) lines.Add($"观测耗时：{seconds:0.##} 秒（从首次发现故障算起，不是端到端实测）");
        if (entry.OutageSince is { } outage) lines.Add($"首次发现故障：{outage.ToLocalTime():HH:mm:ss}");
        if (entry.NextAttempt is { } next) lines.Add($"下次尝试：{next.ToLocalTime():HH:mm:ss}");
        if (entry.AuthenticationStage is { } stage && stage != nameof(PortalStage.Unknown)) lines.Add($"认证阶段：{stage}");
        if (entry.AuthenticationFailure is { } failure && failure != nameof(PortalFailure.None)) lines.Add($"失败分类：{failure}");
        if (entry.AuthenticationPersistentState is { } persistent && persistent != nameof(AuthenticationState.Ready)) lines.Add($"认证持久状态：{persistent}");
        if (entry.AuthenticationHttpStatus is { } http) lines.Add($"HTTP 状态：{http}");
        if (entry.CredentialRequestStarted == true) lines.Add("已发起凭据请求");
        lines.Add("");
        lines.Add("说明：");
        lines.Add(entry.Detail);
        lines.Add("");
        lines.Add("物理出口、UU 会话与真实认证仍待现场验收；此处记录不等于已通过验证。");
        detail.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Copies an allowlisted summary. Built by LogSummary from controlled fields only — never from the
    /// rendered text — so an old record's free-text Detail cannot reach the clipboard by accident.
    /// </summary>
    private void CopySummary()
    {
        try
        {
            if (visible.Count == 0) { MessageBox.Show("当前没有可复制的事件。", "AutoNet"); return; }
            var notice = LogSummary.Notice(loaded.Count, visible.Count);
            Clipboard.SetText(LogSummary.Format(visible) + Environment.NewLine + Environment.NewLine + notice);
            summary.Text = notice;
        }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show("剪贴板正忙，请稍后再试。", "AutoNet"); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Escape closes the window. Shown as a modal dialog with no Accept/Cancel button of its own,
        // it would otherwise be one of the few windows a keyboard-only user cannot dismiss.
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        base.Dispose(disposing);
    }

    private void CaptureForQa() => QaCapture.Run(this, () => pending);
}
