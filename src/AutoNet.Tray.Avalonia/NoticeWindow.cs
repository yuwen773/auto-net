using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>最小提示窗口：已运行提示与管理员操作占位。代码构建，不参与 XAML 编译。</summary>
internal sealed class NoticeWindow : Window
{
    public static NoticeWindow Create(string heading, string body)
    {
        var headingBlock = new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var bodyBlock = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var confirm = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88 };
        var window = new NoticeWindow
        {
            Title = "AutoNet",
            Icon = AppIcons.Load(),
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12,
                Children = { headingBlock, bodyBlock, confirm }
            }
        };
        confirm.Click += (_, _) => window.Close();
        return window;
    }
}
