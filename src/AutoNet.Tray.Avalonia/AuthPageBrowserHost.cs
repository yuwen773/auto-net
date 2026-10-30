using System.Security;
using AutoNet.Core;
using Microsoft.Win32;

namespace AutoNet.Tray.Avalonia;

/// <summary>浏览器宿主的受控状态；模型只依赖这些分类，不依赖具体的 WebView2 类型。</summary>
internal enum AuthPageHostStatus
{
    /// <summary>尚未尝试加载门户页面。</summary>
    NotAttached,
    /// <summary>已加载固定门户根地址。</summary>
    Attached,
    /// <summary>本机没有可用的浏览器运行时。</summary>
    RuntimeMissing,
    /// <summary>受控网络边界不可用（校园 Wi-Fi 未就绪或允许列表代理未能启动），宿主拒绝连接门户（绝不回退直连）。</summary>
    NetworkBoundaryUnavailable,
    /// <summary>宿主加载失败，可手动重试。</summary>
    Failed
}

/// <summary>
/// 浏览器宿主的可观察快照：地址、状态与受控说明。不含页面内容、异常文本或凭据。
/// </summary>
internal sealed record AuthPageHostSnapshot(
    AuthPageHostStatus Status,
    string Address,
    bool Attached,
    string Detail)
{
    public static readonly AuthPageHostSnapshot NotAttached =
        new(AuthPageHostStatus.NotAttached, AuthPageModel.PortalAddress, false, "");
}

/// <summary>
/// 认证页的浏览器宿主抽象（工单 #37 / #27 故事 20–28、70）。
///
/// 页面模型只依赖本接口，不依赖 WebView2 或其他浏览器类型，因此回归可用假宿主验证
/// 打开、关闭、切换、最小化与刷新行为，而不需要安装浏览器运行时。
///
/// 安全边界：本接口只有“加载固定门户根地址、显示/隐藏、销毁”三类能力，**没有**填表、
/// 脚本执行或提交凭据的入口。打开、关闭、切换页面或刷新状态都不得触发导航或提交；
/// 真实网络边界由 #38 接入，会话附着由 #39 接入，二者都不在本工单内声称完成。
///
/// 线程契约：<see cref="AttachAsync"/>、<see cref="SetVisible"/>、<see cref="Dispose"/> 与
/// <see cref="Surface"/> 的读取必须在界面线程（由 <see cref="AuthPageCoordinator"/> 负责调度）；
/// <see cref="Snapshot"/> 可从任意线程读取，实现必须返回不可变快照。真实 WebView2 宿主
/// （#38/#39）依赖这一约束，不得在实现里假设其他线程。
/// </summary>
internal interface IAuthPageBrowserHost : IDisposable
{
    /// <summary>宿主界面表面；模型不读取，视图把它放入宿主区域。未就绪时为 null。</summary>
    object? Surface { get; }

    /// <summary>最近一次宿主状态快照。</summary>
    AuthPageHostSnapshot Snapshot { get; }

    /// <summary>
    /// 只加载固定门户根地址。同一地址重复调用不得重新导航；调用方不得传入其他来源。
    /// 不抛出未受控异常：运行时缺失、边界未就绪与加载失败都转成受控状态。
    /// </summary>
    Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default);

    /// <summary>页面是否可见；仅影响呈现，不触发导航、提交或后台状态变更。</summary>
    void SetVisible(bool visible);
}

/// <summary>
/// 失败关闭的骨架宿主（工单 #37）：**不创建任何浏览器，也不发起网络请求**。
/// 运行时缺失与边界不可用都返回受控状态，绝不回退直连。生产装配已由
/// <see cref="AuthPageWebViewHost"/> 接替；本类只用于 QA 夹具与回归验证受控降级，
/// 生产装配不会回退到它。
/// </summary>
internal sealed class UnavailableAuthPageBrowserHost : IAuthPageBrowserHost
{
    private readonly Func<bool> runtimeProbe;
    private AuthPageHostSnapshot snapshot = AuthPageHostSnapshot.NotAttached;

    public UnavailableAuthPageBrowserHost(Func<bool> runtimeProbe)
    {
        this.runtimeProbe = runtimeProbe;
    }

    public object? Surface => null;

    public AuthPageHostSnapshot Snapshot => snapshot;

    public Task<AuthPageHostSnapshot> AttachAsync(Uri portal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Settings.IsSupportedPortal(portal))
        {
            snapshot = new(AuthPageHostStatus.Failed, AuthPageModel.PortalAddress, false,
                "认证页只接受固定门户根地址，未发起导航。");
            return Task.FromResult(snapshot);
        }

        // 文案与模型共用同一来源，避免宿主与页面各自维护近似说明。
        var status = runtimeProbe()
            ? AuthPageHostStatus.NetworkBoundaryUnavailable
            : AuthPageHostStatus.RuntimeMissing;
        snapshot = new(status, AuthPageModel.PortalAddress, false, AuthPageModel.HostCopyFor(status).Detail);
        return Task.FromResult(snapshot);
    }

    /// <summary>可见性只用于未来的真实宿主；当前实现没有可显示的浏览器表面。</summary>
    public void SetVisible(bool visible) { }

    public void Dispose() { }
}

/// <summary>
/// 浏览器宿主的生产装配（工单 #38）：真实 WebView2 宿主 + 允许列表代理。
/// <paramref name="readLink"/> 从可信状态投影读取校园网卡；读不到时宿主失败关闭，绝不回退直连。
/// </summary>
internal static class AuthPageBrowserHostFactory
{
    public static IAuthPageBrowserHost Production(Func<WifiLink?> readLink) =>
        new AuthPageWebViewHost(readLink, AuthPageBrowserRuntime.IsWebView2RuntimePresent);
}

/// <summary>
/// 只读探测 WebView2 Runtime 是否安装（不创建浏览器、不访问网络）。
/// 同时检查机器级与用户级 Evergreen 注册项；读取失败按“未安装”处理。
/// </summary>
internal static class AuthPageBrowserRuntime
{
    private const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly (RegistryHive Hive, RegistryView View)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default)
    ];

    public static bool IsWebView2RuntimePresent()
    {
        foreach (var (hive, view) in Locations)
        {
            if (ReadVersion(hive, view) is { Length: > 0 } version && version != "0.0.0.0") return true;
        }
        return false;
    }

    private static string? ReadVersion(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + ClientId)
                ?? baseKey.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + ClientId);
            return key?.GetValue("pv") as string;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
