using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 日志页视觉构建（迁移工单 #7 / T09）：日期入口与筛选、受限列表、技术诊断详情与
/// 范围/缺口说明，渲染为 Fluent 卡片式界面；动作按钮（刷新、复制脱敏摘要、日志目录）
/// 紧邻列表（动作靠近对应内容）。本类只负责构建与把选择回传给控制器；
/// 复制、打开目录等副作用在壳一侧注入（QA 模式下替换为证据记录）。
/// 初始选中项都在事件订阅之前设置，程序化构建不回传选择。
/// </summary>
internal static class LogsView
{
    public static Control Build(
        LogsContent content,
        Action<string?> dateSelected,
        Action<LogsFilter> filterSelected,
        Action refreshRequested,
        Action copyRequested,
        Action folderRequested,
        Action<int> rowSelected)
    {
        var page = new StackPanel { Spacing = 14, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = content.Title, FontSize = 26, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = ShellPages.Info(content.Page).Description,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.75
                }
            }
        });

        page.Children.Add(ToolbarCard(content, dateSelected, filterSelected, refreshRequested, copyRequested, folderRequested));

        var notes = new StackPanel { Spacing = 2 };
        if (content.ScopeNote.Length > 0) notes.Children.Add(Muted(content.ScopeNote, 12));
        if (content.GapNote.Length > 0) notes.Children.Add(Muted(content.GapNote, 12));
        if (content.FailureNote.Length > 0) notes.Children.Add(Muted(content.FailureNote, 12));
        if (notes.Children.Count > 0) page.Children.Add(notes);

        page.Children.Add(ListCard(content, rowSelected));
        page.Children.Add(DetailCard(content));
        return page;
    }

    /// <summary>工具栏卡：日期入口（故事 44）、筛选与三个既有动作入口（刷新 / 复制脱敏摘要 / 日志目录）。</summary>
    private static Control ToolbarCard(
        LogsContent content,
        Action<string?> dateSelected,
        Action<LogsFilter> filterSelected,
        Action refreshRequested,
        Action copyRequested,
        Action folderRequested)
    {
        var body = new StackPanel { Spacing = 10 };

        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        dateRow.Children.Add(Muted("日志日期", 13, VerticalAlignment.Center));
        var dates = ShellAccessibility.Named(new ComboBox { MinWidth = 230, IsEnabled = content.Dates.Count > 0 }, ShellAccessibility.LogsDates);
        foreach (var date in content.Dates) dates.Items.Add(date);
        if (content.SelectedDate is { } selected) dates.SelectedItem = selected;   // 订阅之前设置，不回传
        dates.SelectionChanged += (_, _) => dateSelected(dates.SelectedItem as string);
        dateRow.Children.Add(dates);
        body.Children.Add(dateRow);

        var filterRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterRow.Children.Add(Muted("筛选", 13, VerticalAlignment.Center));
        var filters = ShellAccessibility.Named(new ComboBox { MinWidth = 150 }, ShellAccessibility.LogsFilter);
        foreach (var filter in LogsModel.Filters) filters.Items.Add(LogsModel.FilterLabel(filter));
        filters.SelectedIndex = (int)content.Filter;                               // 订阅之前设置，不回传
        filters.SelectionChanged += (_, _) =>
        {
            var index = filters.SelectedIndex;
            if (index >= 0 && index < LogsModel.Filters.Count) filterSelected(LogsModel.Filters[index]);
        };
        filterRow.Children.Add(filters);

        var refresh = new Button { Content = "刷新" };
        refresh.Click += (_, _) => refreshRequested();
        filterRow.Children.Add(refresh);
        var copy = new Button { Content = "复制脱敏摘要", IsEnabled = content.CopyText.Length > 0 };
        copy.Click += (_, _) => copyRequested();
        filterRow.Children.Add(copy);
        var folder = new Button { Content = "日志目录" };
        folder.Click += (_, _) => folderRequested();
        filterRow.Children.Add(folder);
        body.Children.Add(filterRow);

        return ShellTheming.Card(body);
    }

    /// <summary>受限列表卡（故事 45）：时间 / 事件 / 说明三列，最新在前；选中行驱动详情（故事 46）。</summary>
    private static Control ListCard(LogsContent content, Action<int> rowSelected)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Header("事件列表"));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("96,190,*") };
        AddColumnCell(header, "时间", 0);
        AddColumnCell(header, "事件", 1);
        AddColumnCell(header, "说明", 2);
        body.Children.Add(header);

        var list = ShellAccessibility.Named(new ListBox
        {
            MaxHeight = 360,
            IsEnabled = content.Rows.Count > 0
        }, ShellAccessibility.LogsList);
        foreach (var row in content.Rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("96,190,*"), Margin = new Thickness(0, 2) };
            AddCell(grid, row.Time, 0);
            AddCell(grid, row.Action, 1);
            AddCell(grid, row.Preview, 2);
            list.Items.Add(grid);
        }

        if (content.SelectedIndex >= 0 && content.SelectedIndex < content.Rows.Count)
            list.SelectedIndex = content.SelectedIndex;                            // 订阅之前设置，不回传
        list.SelectionChanged += (_, _) => rowSelected(list.SelectedIndex);
        body.Children.Add(list);

        return ShellTheming.Card(body);
    }

    /// <summary>详情卡（故事 46）：选中记录的受控详情；只读，失败或空态有固定说明。</summary>
    private static Control DetailCard(LogsContent content)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Header("技术诊断详情"));
        body.Children.Add(ShellAccessibility.Named(new TextBox
        {
            Text = content.DetailText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            MaxHeight = 420,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 12.5
        }, ShellAccessibility.LogsDetail));
        return ShellTheming.Card(body);
    }

    private static void AddColumnCell(Grid grid, string text, int column)
    {
        var cell = ShellTheming.Themed(
            new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold },
            TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static void AddCell(Grid grid, string text, int column)
    {
        var cell = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static TextBlock Header(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) };

    private static TextBlock Muted(string text, double size, VerticalAlignment alignment = VerticalAlignment.Top) => ShellTheming.Themed(
        new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size + 6, VerticalAlignment = alignment },
        TextBlock.ForegroundProperty, "TextMuted");
}
