using System.Globalization;

namespace AutoNet.Core;

/// <summary>
/// 认证浏览器参数（工单 #38）：只给单个 WebView2 传代理，**不**改动系统代理。
/// 代理指向应用自带的环回允许列表代理；<c>&lt;-loopback&gt;</c> 保证回环目标也不会绕过代理。
/// QUIC、后台联网与 WebRTC 的非代理 UDP 都会绕开 HTTP 代理，必须一并关闭。
///
/// 展示页与提权提交通道共用这一构造器：两边参数一旦分叉，提交路径就可能出现
/// 不经过允许列表代理的 UDP 出站，因此不在调用点手写参数串。
/// </summary>
public static class AuthPageBrowserArguments
{
    public static string For(int proxyPort)
    {
        if (proxyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(proxyPort));
        return "--proxy-server=http://127.0.0.1:" + proxyPort.ToString(CultureInfo.InvariantCulture)
            + " --proxy-bypass-list=<-loopback>"
            + " --disable-quic"
            + " --disable-background-networking"
            + " --force-webrtc-ip-handling-policy=disable_non_proxied_udp";
    }
}
