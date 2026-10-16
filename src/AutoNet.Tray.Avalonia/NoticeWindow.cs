using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>最小提示窗口：已运行提示与受控结果说明。代码构建，不参与 XAML 编译。</summary>
internal sealed class NoticeWindow : Window
{
    /// <summary>
    /// 创建提示窗口。<paramref name="width"/> 允许调用方为更长的受控说明（如解除复核失败的
    /// 失败分类摘要）留出更宽的换行空间，避免窄窗口把术语折成难读的碎行。
    /// </summary>
    public static NoticeWindow Create(string heading, string body, double width = 440)
    {
        var headingBlock = new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var bodyBlock = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, LineHeight = 20 };
        var confirm = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88 };
        var window = new NoticeWindow
        {
            Title = "AutoNet",
            Icon = AppIcons.Load(),
            Width = width,
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
