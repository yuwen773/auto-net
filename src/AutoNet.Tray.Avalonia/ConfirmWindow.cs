using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoNet.Tray.Avalonia;

/// <summary>
/// 确认对话框（迁移工单 #5）：解除认证暂停等破坏性安全状态变更的用户确认点。
/// 与既有 WinForms MessageBox 同语义——警告图标、说明先于按钮、默认按钮为“否”；
/// 关闭窗口等同取消，绝不因为失焦或回车而默认确认。代码构建，不参与 XAML 编译。
/// </summary>
internal sealed class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>用户选择：true 确认，false 取消（含直接关闭窗口）。</summary>
    public Task<bool> Answer => answer.Task;

    public static ConfirmWindow Create(string heading, string body)
    {
        var dialog = new ConfirmWindow();
        var headingBlock = new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var bodyBlock = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, LineHeight = 21 };

        // 破坏性确认：默认与 Esc 都是“否”，确认键必须被显式点击。
        var cancel = new Button { Content = "取消", MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var confirm = new Button { Content = "确认解除", MinWidth = 108, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, confirm }
        };

        dialog.Title = "AutoNet";
        dialog.Icon = AppIcons.Load();
        dialog.Width = 520;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.CanResize = false;
        dialog.ShowInTaskbar = false;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children = { headingBlock, bodyBlock, buttons }
        };

        cancel.Click += (_, _) => dialog.Settle(false);
        confirm.Click += (_, _) => dialog.Settle(true);
        // 直接关闭（标题栏 ×、Alt+F4）一律按取消处理：不得把关闭解释成确认。
        dialog.Closing += (_, _) => dialog.answer.TrySetResult(false);
        return dialog;
    }

    private void Settle(bool confirmed)
    {
        if (!answer.TrySetResult(confirmed)) return;
        Close();
    }

    /// <summary>在当前界面线程上显示确认对话框并等待用户选择；调用方负责把结果交给 <see cref="UnblockAuthentication"/>。</summary>
    public static Task<bool> AskAsync(string heading, string body)
    {
        var dialog = Create(heading, body);
        dialog.Show();
        return dialog.Answer;
    }
}
