namespace AutoNet.Tray.Avalonia;

/// <summary>页面内容基类：壳渲染的单一内容契约；具体页面在各自切片提供专属内容。</summary>
internal abstract record PageContent(ShellPage Page, string Title);

/// <summary>
/// 异步页面读取失败时的受控降级内容：只含标题、说明与固定脚注，不携带异常原文。
/// 由 <see cref="ShellController"/> 的失败回调产生，不是迁移占位。
/// </summary>
internal sealed record PageScaffold(ShellPage Page, string Title, string Description, string Note) : PageContent(Page, Title);
