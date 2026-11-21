using AutoNet.Core;
using AutoNet.Tray.Avalonia;

internal static class QaBootstrapRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("QA 装配回归：生产模式原样透传，不创建 QA 宿主", () =>
        {
            var hostCalls = 0;
            var production = Production(() => { hostCalls++; return new FakeAuthHost(); });
            var composition = QaBootstrap.Compose(QaEnvironment.Inactive, production);

            Require(hostCalls == 1, "生产模式未创建生产宿主");
            Require(composition.AuthHost is FakeAuthHost, "生产模式替换了宿主");
            Require(ReferenceEquals(composition.BrowserLaunch, production.BrowserLaunch), "生产模式替换了浏览器启动");
            Require(ReferenceEquals(composition.PickTarget, production.PickTarget), "生产模式替换了导出目标选择");
            Require(composition.PresentedSink is null && composition.AttachNotifications, "生产模式未接真实通知");
            return Task.CompletedTask;
        }),
        ("QA 装配回归：日志与通知模式替换为受控空操作", () =>
        {
            var logs = QaBootstrap.Compose(new QaEnvironment(QaMode.Logs, "qa"), Production());
            Require(logs.CopyHandler is not null && logs.FolderHandler is not null,
                "日志 QA 未替换复制或目录入口");

            var notifications = QaBootstrap.Compose(new QaEnvironment(QaMode.Notifications, "qa"), Production());
            Require(notifications.PresentedSink is { Count: 0 } && !notifications.AttachNotifications,
                "通知 QA 未使用受控呈现或仍订阅真实管道");
            return Task.CompletedTask;
        }),
        ("QA 装配回归：非认证页 QA 失败关闭宿主并隔离真实动作", () =>
        {
            var diagnostics = QaBootstrap.Compose(new QaEnvironment(QaMode.Diagnostics, "qa"), Production());
            Require(diagnostics.AuthHost is UnavailableAuthPageBrowserHost,
                "非认证页 QA 未使用失败关闭宿主");
            Require(diagnostics.CopyHandler is null && diagnostics.FolderHandler is null,
                "非日志 QA 误替换了复制或目录入口");

            var accessibility = QaBootstrap.Compose(new QaEnvironment(QaMode.Accessibility, "qa"), Production());
            Require(accessibility.AuthHost is UnavailableAuthPageBrowserHost,
                "可访问性 QA 未使用失败关闭宿主");
            Require(accessibility.AuthChannelPresent is null && accessibility.AuthChannelFallback is null,
                "可访问性 QA 仍呈现真实通知或回退开页");
            Require(accessibility.CopyHandler is not null && accessibility.FolderHandler is not null,
                "可访问性 QA 未替换复制或目录入口");
            return Task.CompletedTask;
        })
    ];

    private static QaCompositionContext Production(Func<IAuthPageBrowserHost>? authHost = null) => new(
        BrowserLaunch: _ => null,
        AuthHost: authHost ?? (() => new FakeAuthHost()),
        Launcher: _ => { },
        LoadProfiles: () => [],
        SaveSettings: (_, _) => Task.FromResult(new HandoffResult(HandoffOutcome.Rejected)),
        CopyHandler: null,
        FolderHandler: null,
        PickTarget: () => Task.FromResult<string?>(null),
        ExportLaunch: _ => Task.FromResult<DiagnosticsExportLaunch?>(null),
        AuthChannelStart: _ => null,
        AuthChannelPresent: null,
        AuthChannelFallback: null,
        PortalOpenManual: () => { });

    private sealed class FakeAuthHost : IAuthPageBrowserHost
    {
        public object? Surface => null;

        public AuthPageHostSnapshot Snapshot => AuthPageHostSnapshot.NotAttached;

        public event Action<AuthPageHostSnapshot>? StateChanged { add { } remove { } }

        public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public void Refresh() { }

        public void SetVisible(bool visible) { }

        public void Dispose() { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
