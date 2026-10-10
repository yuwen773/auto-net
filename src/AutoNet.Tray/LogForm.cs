using AutoNet.Core;

namespace AutoNet.Tray;

internal sealed class LogForm : Form
{
    private readonly ComboBox date = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly RichTextBox history = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BorderStyle = BorderStyle.None,
        BackColor = Color.White, ForeColor = Color.FromArgb(35, 45, 58), Font = new Font("Microsoft YaHei UI", 10),
        AccessibleName = "中文事件日志", WordWrap = true
    };
    private readonly string logDirectory = Environment.GetEnvironmentVariable("AUTONET_QA_LOG_DIRECTORY") ?? Storage.Logs;

    public LogForm()
    {
        Text = "AutoNet · 连接日志";
        Icon = NetworkIcon.Create();
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(980, 720);
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(247, 249, 252);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(24) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(0, 0, 0, 10) };
        actions.Controls.Add(new Label { Text = "日志日期", AutoSize = true, Padding = new Padding(0, 8, 8, 0) });
        date.AccessibleName = "日志日期";
        actions.Controls.Add(date);
        var refresh = MainForm.Button("刷新"); refresh.Click += (_, _) => RefreshFiles();
        var copy = MainForm.Button("复制显示内容"); copy.Click += (_, _) =>
        {
            try { if (history.TextLength != 0) Clipboard.SetText(history.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show("剪贴板正忙，请稍后再试。", "AutoNet"); }
        };
        var folder = MainForm.Button("日志目录"); folder.Click += (_, _) => Program.OpenFolder(logDirectory);
        actions.Controls.AddRange([refresh, copy, folder]);
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(new Label { Text = "最新事件在前 · 显示所选日期最近 300 条 · 联网恢复记录不代表一定由程序修复", AutoSize = true,
            ForeColor = Color.FromArgb(65, 75, 88), Padding = new Padding(0, 0, 0, 14) }, 0, 1);
        layout.Controls.Add(history, 0, 2);
        Controls.Add(layout);
        date.SelectedIndexChanged += (_, _) => ReadHistory();
        RefreshFiles();
        Shown += (_, _) => BeginInvoke(() => CaptureForQa());
    }

    private void RefreshFiles()
    {
        try
        {
            var selected = date.SelectedItem as string;
            var files = Directory.Exists(logDirectory) ? Directory.EnumerateFiles(logDirectory, "autonet-????-??-??.jsonl")
                .Select(Path.GetFileName).OrderDescending().ToArray() : [];
            date.Items.Clear();
            foreach (var file in files) if (file is not null) date.Items.Add(file);
            if (date.Items.Count != 0) date.SelectedIndex = selected is not null && date.Items.Contains(selected) ? date.Items.IndexOf(selected) : 0;
            else history.Text = "尚无连接日志。请检查后台服务是否已安装并运行，然后点击刷新。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { history.Text = "无法读取日志目录，请检查访问权限后刷新。"; }
    }

    private void ReadHistory()
    {
        if (date.SelectedItem is not string file) return;
        try { history.Text = ActivityLog.ReadableHistory(Storage.ReadShared(Path.Combine(logDirectory, file))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { history.Text = "无法读取这一天的日志，请检查访问权限后刷新。"; }
    }

    private void CaptureForQa()
    {
        var path = Environment.GetEnvironmentVariable("AUTONET_CAPTURE_PATH");
        if (string.IsNullOrEmpty(path)) return;
        if (float.TryParse(Environment.GetEnvironmentVariable("AUTONET_QA_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var scale) && scale is >= 1 and <= 2)
            Scale(new SizeF(scale, scale));
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
        BeginInvoke(() => Application.Exit());
    }
}
