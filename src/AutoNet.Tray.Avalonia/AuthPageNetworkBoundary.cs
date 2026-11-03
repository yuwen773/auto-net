using AutoNet.Core;

namespace AutoNet.Tray.Avalonia;

/// <summary>准备完成的网络边界：已启动的环回允许列表代理。</summary>
internal sealed record AuthPageBoundary(AuthChannelProxy Proxy);

/// <summary>
/// 认证页网络边界（工单 #38）：先确认门户来源、浏览器运行时与校园网卡，再启动环回允许列表代理。
/// 任一步失败都返回受控失败状态——调用方只能失败，**绝不**回退直连。
/// 代理把出站连接交给 <see cref="DirectNetwork.ConnectAsync"/>（IP_UNICAST_IF + 源地址绑定），
/// 因此连接不经过系统默认路由上的 TUN。
/// </summary>
internal static class AuthPageNetworkBoundary
{
    public static async Task<(AuthPageHostSnapshot? Failure, AuthPageBoundary? Boundary)> PrepareAsync(
        Uri portal,
        bool runtimePresent,
        Func<WifiLink?> readLink,
        Func<ChannelProcessJob> ensureJob,
        Func<WifiLink, ChannelProcessJob, Task<AuthChannelProxy>> startProxy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Settings.IsSupportedPortal(portal))
            return (Failure(AuthPageHostStatus.Failed, "认证页只接受固定门户根地址，未发起导航。"), null);
        if (!runtimePresent)
            return (Failure(AuthPageHostStatus.RuntimeMissing,
                AuthPageModel.HostCopyFor(AuthPageHostStatus.RuntimeMissing).Detail), null);

        WifiLink? link;
        try { link = readLink(); }
        catch (Exception) { link = null; }
        if (link is not { State: WifiState.Ready })
            return (Failure(AuthPageHostStatus.NetworkBoundaryUnavailable,
                "校园 Wi-Fi 未就绪或未取得地址；认证页不会连接门户，也不会回退直连。"), null);

        AuthChannelProxy? proxy = null;
        try
        {
            var job = ensureJob();
            proxy = await startProxy(link, job).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!proxy.IsRunning) throw new InvalidOperationException("允许列表代理未运行。");
            return (null, new AuthPageBoundary(proxy));
        }
        // 取消不是边界失败：让协调器按“取消”语义复位，下一次打开仍可附着。
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (proxy is not null)
            {
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            throw;
        }
        catch (Exception)
        {
            if (proxy is not null)
            {
                try { await proxy.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            return (Failure(AuthPageHostStatus.NetworkBoundaryUnavailable,
                "本地允许列表代理未能启动；认证页不会连接门户，也不会回退直连。"), null);
        }
    }

    private static AuthPageHostSnapshot Failure(AuthPageHostStatus status, string detail) =>
        new(status, AuthPageModel.PortalAddress, false, detail);
}
