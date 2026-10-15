namespace AutoNet.Tray.Avalonia;

/// <summary>页面脚手架内容：标题、迁移说明与脚注；后续工单在原位填充真实内容。</summary>
internal sealed record PageScaffold(ShellPage Page, string Title, string Description, string Note);

/// <summary>
/// 页面内容加载接缝：后续工单（#4 概览 / #6 诊断 / #7 日志 / #8 设置）在此接入真实异步读取。
/// 迁移中页面为静态脚手架；取消、代次与销毁保护由 <see cref="PageLoadCoordinator"/> 统一提供。
/// </summary>
internal static class ShellContent
{
    public static Task<PageScaffold> LoadAsync(ShellPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = ShellPages.Info(page);
        return Task.FromResult(new PageScaffold(
            info.Page,
            info.Name,
            info.Description,
            "此页内容将在后续迁移中提供；当前版本提供导航、托盘与状态管道。"));
    }
}
