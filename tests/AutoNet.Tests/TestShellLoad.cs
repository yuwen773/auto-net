using AutoNet.Tray.Avalonia;

/// <summary>
/// 测试用页面加载器：替代已删除的生产迁移脚手架，只服务于不关心内容的导航/代次测试。
/// 生产接线必须使用真实加载器（日志页）。
/// </summary>
internal static class TestShellLoad
{
    public static Task<PageContent> Scaffold(ShellPage page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = ShellPages.Info(page);
        return Task.FromResult<PageContent>(new PageScaffold(page, info.Name, info.Description, "测试占位内容"));
    }
}
