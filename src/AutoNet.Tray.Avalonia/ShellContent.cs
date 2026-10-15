namespace AutoNet.Tray.Avalonia;

/// <summary>页面内容基类：壳渲染的单一内容契约；具体页面在各自切片提供专属内容（#4 概览，其余后续工单）。</summary>
internal abstract record PageContent(ShellPage Page, string Title);

/// <summary>页面脚手架内容：标题、迁移说明与脚注；已迁移页面不再使用。</summary>
internal sealed record PageScaffold(ShellPage Page, string Title, string Description, string Note) : PageContent(Page, Title);

/// <summary>
/// 页面内容加载接缝：尚未迁移的页面（#5–#8）返回静态脚手架；概览页内容由
/// <see cref="OverviewModel"/> 按状态投影（不经此接缝）。取消、代次与销毁保护由
/// <see cref="PageLoadCoordinator"/> 统一提供。
/// </summary>
internal static class ShellContent
{
    public static Task<PageContent> LoadAsync(ShellPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = ShellPages.Info(page);
        return Task.FromResult<PageContent>(new PageScaffold(
            info.Page,
            info.Name,
            info.Description,
            "此页内容将在后续迁移中提供；当前版本提供导航、托盘与状态管道。"));
    }
}
