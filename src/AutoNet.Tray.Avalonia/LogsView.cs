using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 日志页视觉构建（迁移工单 #7 / T09）：日期入口与筛选、受限列表、技术诊断详情与
/// 范围/缺口说明，渲染为卡片式界面；动作按钮（刷新、复制脱敏摘要、日志目录）紧邻列表。
/// 表格采用低对比度表头与中性选中行，选中行仍由系统列表控件承担键盘语义。
/// 本类只负责构建与把选择回传给控制器；复制、打开目录等副作用在壳一侧注入。
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
        var page = new StackPanel
        {
            Spacing = ShellTheming.CardGap,
            MaxWidth = ShellTheming.PageMaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        page.Children.Add(ShellTheming.PageHeader(content.Title, ShellPages.Info(content.Page).Description));

        page.Children.Add(ToolbarCard(content, dateSelected, filterSelected, refreshRequested, copyRequested, folderRequested));

        var notes = new StackPanel { Spacing = 4, Margin = new Thickness(2, 0, 0, 0) };
        if (content.ScopeNote.Length > 0) notes.Children.Add(ShellTheming.Muted(content.ScopeNote, 11.5, 18));
        if (content.GapNote.Length > 0) notes.Children.Add(ShellTheming.Muted(content.GapNote, 11.5, 18));
        if (content.FailureNote.Length > 0) notes.Children.Add(ShellTheming.Muted(content.FailureNote, 11.5, 18));
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
        var body = new StackPanel { Spacing = 12 };

        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var dateLabel = ShellTheming.Label("日志日期", 12.5);
        dateLabel.Width = 56;
        dateLabel.VerticalAlignment = VerticalAlignment.Center;
        dateRow.Children.Add(dateLabel);
        var dates = ShellAccessibility.Named(new ComboBox { MinWidth = 230, IsEnabled = content.Dates.Count > 0 }, ShellAccessibility.LogsDates);
        foreach (var date in content.Dates) dates.Items.Add(date);
        if (content.SelectedDate is { } selected) dates.SelectedItem = selected;   // 订阅之前设置，不回传
        dates.SelectionChanged += (_, _) => dateSelected(dates.SelectedItem as string);
        dateRow.Children.Add(dates);
        body.Children.Add(dateRow);

        var filterRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var filterLabel = ShellTheming.Label("筛选", 12.5);
        filterLabel.Width = 56;
        filterLabel.VerticalAlignment = VerticalAlignment.Center;
        filterLabel.Margin = new Thickness(0, 0, 10, 0);
        filterRow.Children.Add(filterLabel);
        var filters = ShellAccessibility.Named(new ComboBox { MinWidth = 150 }, ShellAccessibility.LogsFilter);
        foreach (var filter in LogsModel.Filters) filters.Items.Add(LogsModel.FilterLabel(filter));
        filters.SelectedIndex = (int)content.Filter;                               // 订阅之前设置，不回传
        filters.SelectionChanged += (_, _) =>
        {
            var index = filters.SelectedIndex;
            if (index >= 0 && index < LogsModel.Filters.Count) filterSelected(LogsModel.Filters[index]);
        };
        filters.Margin = new Thickness(0, 0, 10, 0);
        filterRow.Children.Add(filters);

        var refresh = ShellTheming.SecondaryButton("刷新");
        refresh.Margin = new Thickness(0, 0, 8, 0);
        refresh.Click += (_, _) => refreshRequested();
        filterRow.Children.Add(refresh);
        var copy = ShellTheming.SecondaryButton("复制脱敏摘要");
        copy.Margin = new Thickness(0, 0, 8, 0);
        copy.IsEnabled = content.CopyText.Length > 0;
        copy.Click += (_, _) => copyRequested();
        filterRow.Children.Add(copy);
        var folder = ShellTheming.SecondaryButton("日志目录");
        folder.Click += (_, _) => folderRequested();
        filterRow.Children.Add(folder);
        body.Children.Add(filterRow);

        return ShellTheming.Card(body);
    }

    /// <summary>受限列表卡（故事 45）：时间 / 事件 / 说明三列，最新在前；选中行驱动详情（故事 46）。</summary>
    private static Control ListCard(LogsContent content, Action<int> rowSelected)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(ShellTheming.SectionHeader("事件列表"));

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("96,190,*"),
            Margin = new Thickness(12, 0, 12, 0)
        };
        AddColumnCell(header, "时间", 0);
        AddColumnCell(header, "事件", 1);
        AddColumnCell(header, "说明", 2);
        body.Children.Add(header);

        var list = ShellAccessibility.Named(new ListBox
        {
            MaxHeight = 360,
            IsEnabled = content.Rows.Count > 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        }, ShellAccessibility.LogsList);
        foreach (var row in content.Rows)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("96,190,*"),
                Margin = new Thickness(2, 7, 2, 7)
            };
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
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(ShellTheming.SectionHeader("技术诊断详情"));
        var detail = ShellAccessibility.Named(new TextBox
        {
            Text = content.DetailText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 200,
            MaxHeight = 420,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 12.5,
            BorderThickness = new Thickness(0)
        }, ShellAccessibility.LogsDetail);
        ShellTheming.Themed(detail, TextBox.BackgroundProperty, "SurfaceSunken");
        body.Children.Add(detail);
        return ShellTheming.Card(body);
    }

    private static void AddColumnCell(Grid grid, string text, int column)
    {
        var cell = ShellTheming.Themed(
            new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold },
            TextBlock.ForegroundProperty, "TextSubtle");
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static void AddCell(Grid grid, string text, int column)
    {
        // 行内文字显式取正文色：选中行在部分主题下会把继承的文字色改为强调色，
        // 三列内容会一起变淡；这里让“选中”只由行底色表达。
        var cell = ShellTheming.Themed(
            new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }
}
