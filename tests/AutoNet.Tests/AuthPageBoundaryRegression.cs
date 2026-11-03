using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoNet.Core;
using AutoNet.Tray.Avalonia;

/// <summary>
/// 认证页网络边界回归（工单 #38）：允许列表完整匹配与两侧大小写归一、请求形态与端口约束、
/// 出站连接交给绑定网卡的连接器、代理不可用时失败关闭且不回退直连、浏览器参数不改系统代理。
/// 全部离线：代理只连环回监听器，连接器是受控实现，不访问真实门户。
/// </summary>
internal static class AuthPageBoundaryRegression
{
    private const string PortalHost = Settings.PortalHost;

    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("认证页边界：允许列表完整匹配且两侧大小写归一", () => Sync(() =>
        {
            foreach (var allowed in new[] { "login.csust.edu.cn", "LOGIN.CSUST.EDU.CN", "Login.Csust.Edu.Cn.", " login.csust.edu.cn " })
                Require(ProxyHostPolicy.IsAllowed(allowed), "允许的主机被拒绝：" + allowed);
            foreach (var denied in new[]
            {
                "login.csust.edu.cn.evil.example",   // 后缀伪装
                "x.login.csust.edu.cn",              // 子域前缀
                "csust.edu.cn",                      // 父域
                "127.0.0.1",                         // 字面量 IP
                "::1",
                "login.csust.edu.cn:443",            // 端口不是主机名的一部分
                "login..csust.edu.cn",
                "-login.csust.edu.cn",
                "login.csust.edu.cn/path",
                "login.csust.edu.cn@evil.example",
                ""
            })
                Require(!ProxyHostPolicy.IsAllowed(denied), "不允许的主机被放行：" + denied);
        })),
        ("认证页边界：明文只允许80、CONNECT只允许443且拒绝协议升级", () => Sync(() =>
        {
            Require(AuthChannelProxy.ParseRequest("CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n") ==
                AuthChannelProxy.ProxyRoute.HttpsTunnel, "允许的 CONNECT 被拒绝");
            Require(AuthChannelProxy.ParseRequest("GET http://LOGIN.CSUST.EDU.CN:80/ HTTP/1.1\r\nHost: LOGIN.CSUST.EDU.CN\r\n\r\n") ==
                AuthChannelProxy.ProxyRoute.HttpRoot, "允许的明文目标被拒绝");
            // 浏览器对默认端口可能省略 Host 头里的端口；请求行已经固定了 443。
            Require(AuthChannelProxy.ParseRequest("CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn\r\n\r\n") ==
                AuthChannelProxy.ProxyRoute.HttpsTunnel, "省略默认端口的 CONNECT 被拒绝");
            foreach (var request in new[]
            {
                "CONNECT login.csust.edu.cn:80 HTTP/1.1\r\nHost: login.csust.edu.cn:80\r\n\r\n",
                "GET http://login.csust.edu.cn:443/ HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n",
                // WebSocket 升级绕过资源过滤，必须由代理拒绝，而不是靠页面过滤。
                "GET http://login.csust.edu.cn/ HTTP/1.1\r\nHost: login.csust.edu.cn\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n",
                "CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:80\r\n\r\n",
                "CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\nUpgrade: h2c\r\n\r\n",
                "CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\nContent-Length: 4\r\n\r\n",
                "CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\nTransfer-Encoding: chunked\r\n\r\n"
            })
                Require(AuthChannelProxy.ParseRequest(request) == AuthChannelProxy.ProxyRoute.Denied,
                    "不允许的请求形态被放行：" + request.Split("\r\n")[0]);
        })),
        ("认证页边界：代理把出站连接交给绑定网卡的连接器", async () =>
        {
            var link = Link();
            var calls = new List<(string Host, int Port, WifiLink Link)>();
            var job = ChannelProcessJob.CreateForCurrentProcess();
            await using var proxy = await AuthChannelProxy.StartAsync(link, job, (host, port, bound, _) =>
            {
                calls.Add((host, port, bound));
                return Task.FromResult<Stream>(new RecordingStream());
            });
            Require(proxy.IsRunning, "代理未运行");

            using (var connect = await OpenAsync(proxy.Port))
            {
                await connect.GetStream().WriteAsync(Concat(
                    Encoding.ASCII.GetBytes("CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n"),
                    TlsClientHelloFixture.Record(PortalHost)));
                await WaitForAsync(() => calls.Count >= 1);
            }
            using (var plain = await OpenAsync(proxy.Port))
            {
                await plain.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                    "GET http://login.csust.edu.cn/ HTTP/1.1\r\nHost: login.csust.edu.cn\r\n\r\n"));
                await WaitForAsync(() => calls.Count >= 2);
            }

            Require(calls.Count == 2, "连接器调用次数不符：" + calls.Count);
            Require(calls[0] == (PortalHost, 443, link), "CONNECT 未交给绑定网卡的连接器");
            Require(calls[1] == (PortalHost, 80, link), "明文未交给绑定网卡的连接器");
        }),
        ("认证页边界：被拒绝的主机不会触发出站连接", async () =>
        {
            var link = Link();
            var calls = 0;
            await using var proxy = await AuthChannelProxy.StartAsync(link, null, (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<Stream>(new RecordingStream());
            });
            // 没有会话进程组时连 403 之前就拒绝；这里只关心没有任何出站连接。
            await SendAsync(proxy.Port, Encoding.ASCII.GetBytes(
                "CONNECT login.csust.edu.cn.evil.example:443 HTTP/1.1\r\nHost: login.csust.edu.cn.evil.example:443\r\n\r\n"));
            Require(Volatile.Read(ref calls) == 0, "被拒绝的目标仍触发了出站连接");
        }),
        ("认证页边界：代理不可用时失败关闭且不回退直连", async () =>
        {
            var link = Link();
            var job = ChannelProcessJob.CreateForCurrentProcess();

            var failedStart = await AuthPageNetworkBoundary.PrepareAsync(
                new Uri(AuthPageModel.PortalAddress), true, () => link, () => job,
                (_, _) => throw new InvalidOperationException("代理启动失败"), default);
            Require(failedStart.Boundary is null, "代理启动失败仍返回了可用边界");
            Require(failedStart.Failure?.Status == AuthPageHostStatus.NetworkBoundaryUnavailable, "代理启动失败未降级为边界不可用");

            var stopped = await AuthChannelProxy.StartAsync(link, null);
            await stopped.DisposeAsync();
            var notRunning = await AuthPageNetworkBoundary.PrepareAsync(
                new Uri(AuthPageModel.PortalAddress), true, () => link, () => job,
                (_, _) => Task.FromResult(stopped), default);
            Require(notRunning.Boundary is null, "已停止的代理仍被当成可用边界");
            Require(notRunning.Failure?.Status == AuthPageHostStatus.NetworkBoundaryUnavailable, "已停止的代理未降级为边界不可用");
        }),
        ("认证页边界：校园网卡未就绪或运行时缺失时失败关闭", async () =>
        {
            var job = ChannelProcessJob.CreateForCurrentProcess();
            var noLink = await AuthPageNetworkBoundary.PrepareAsync(
                new Uri(AuthPageModel.PortalAddress), true, () => null, () => job,
                (_, _) => throw new InvalidOperationException("不应启动代理"), default);
            Require(noLink.Boundary is null && noLink.Failure?.Status == AuthPageHostStatus.NetworkBoundaryUnavailable,
                "缺少网卡时未失败关闭");

            var disconnected = await AuthPageNetworkBoundary.PrepareAsync(
                new Uri(AuthPageModel.PortalAddress), true,
                () => new WifiLink(false, null, null, 0, []), () => job,
                (_, _) => throw new InvalidOperationException("不应启动代理"), default);
            Require(disconnected.Boundary is null && disconnected.Failure?.Status == AuthPageHostStatus.NetworkBoundaryUnavailable,
                "网卡未就绪时未失败关闭");

            var noRuntime = await AuthPageNetworkBoundary.PrepareAsync(
                new Uri(AuthPageModel.PortalAddress), false, () => Link(), () => job,
                (_, _) => throw new InvalidOperationException("不应启动代理"), default);
            Require(noRuntime.Boundary is null && noRuntime.Failure?.Status == AuthPageHostStatus.RuntimeMissing,
                "运行时缺失未降级为受控状态");
        }),
        ("认证页边界：只接受固定门户根地址", async () =>
        {
            var job = ChannelProcessJob.CreateForCurrentProcess();
            foreach (var portal in new[]
            {
                new Uri("https://evil.example/"),
                new Uri("https://login.csust.edu.cn.evil.example/"),
                new Uri("http://login.csust.edu.cn/"),
                new Uri("https://login.csust.edu.cn/path"),
                new Uri("https://login.csust.edu.cn/?q=1")
            })
            {
                var result = await AuthPageNetworkBoundary.PrepareAsync(
                    portal, true, () => Link(), () => job,
                    (_, _) => throw new InvalidOperationException("不应启动代理"), default);
                Require(result.Boundary is null && result.Failure?.Status == AuthPageHostStatus.Failed,
                    "非固定门户未被拒绝：" + portal);
            }
        }),
        ("认证页边界：浏览器参数只作用于本 WebView且不改系统代理", () => Sync(() =>
        {
            var args = AuthPageBrowserArguments.For(43210);
            Require(args.Contains("--proxy-server=http://127.0.0.1:43210", StringComparison.Ordinal), "缺少本 WebView 的代理参数");
            Require(args.Contains("--proxy-bypass-list=<-loopback>", StringComparison.Ordinal), "回环目标可能绕过代理");
            Require(args.Contains("--disable-quic", StringComparison.Ordinal), "QUIC 可能绕过 HTTP 代理");
            Require(args.Contains("--disable-background-networking", StringComparison.Ordinal),
                "后台联网可能绕过代理");
            // 展示页与提权提交通道共用这一构造器：提交路径同样不得有非代理 UDP 出站。
            Require(args.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", StringComparison.Ordinal),
                "WebRTC 可能绕过代理");
            // 系统代理/PAC 的开关不在参数里：本实现只配置这一个 WebView。
            Require(!args.Contains("pac", StringComparison.OrdinalIgnoreCase), "参数包含系统代理/PAC 开关");
            Require(args.Split(' ').Count(part => part.StartsWith("--proxy-server=", StringComparison.Ordinal)) == 1,
                "代理参数必须且只能出现一次");
            Require(!args.Contains("--no-proxy-server", StringComparison.Ordinal), "参数包含直连开关");
            Throws<ArgumentOutOfRangeException>(() => AuthPageBrowserArguments.For(0));
            Throws<ArgumentOutOfRangeException>(() => AuthPageBrowserArguments.For(70000));

            // 参数模板只能存在于 Core：生产程序集若重新内嵌自己的参数串，就会再次漂移。
            var root = AppContext.BaseDirectory;
            Require(CountUtf16(Path.Combine(root, "AutoNet.Core.dll"), "--proxy-server") == 1,
                "Core 中代理参数模板数量不符");
            foreach (var assembly in new[] { "AutoNet.AuthChannel.dll", "AutoNet.Tray.Avalonia.dll" })
                Require(CountUtf16(Path.Combine(root, assembly), "--proxy-server") == 0,
                    assembly + " 仍内嵌自己的代理参数");
        })),
        ("认证页边界：网卡只从可信状态投影读取", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            Require(App.ReadProjectedLink(null) is null, "没有状态时仍返回网卡");
            Require(App.ReadProjectedLink(Reading(new StatusSnapshot { SettingsProjected = false }, now, trusted: true)) is null,
                "未投影设置时仍返回网卡");
            Require(App.ReadProjectedLink(Reading(new StatusSnapshot { SettingsProjected = true, InterfaceId = "", WifiProfile = "x" }, now, trusted: true)) is null,
                "接口标识无效时仍返回网卡");
            Require(App.ReadProjectedLink(Reading(new StatusSnapshot { SettingsProjected = true, InterfaceId = Guid.NewGuid().ToString(), WifiProfile = "" }, now, trusted: true)) is null,
                "配置名为空时仍返回网卡");
            Require(App.ReadProjectedLink(Reading(new StatusSnapshot { SettingsProjected = true, InterfaceId = Guid.NewGuid().ToString(), WifiProfile = "x" }, now, trusted: false)) is null,
                "不可信状态仍返回网卡");
        }))
    ];

    private static WifiLink Link() =>
        new(true, null, IPAddress.Parse("10.0.0.2"), 7, [IPAddress.Parse("10.0.0.1")]);

    private static StatusReading Reading(StatusSnapshot snapshot, DateTimeOffset now, bool trusted) =>
        new(snapshot, NetworkDisplayMap.Map(snapshot, TimeSpan.Zero), trusted, trusted, TimeSpan.Zero, now);

    private static async Task SendAsync(int port, byte[] request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        await stream.WriteAsync(request);
        try
        {
            var buffer = new byte[512];
            await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException) { }
        catch (TimeoutException) { }
    }

    private static async Task<TcpClient> OpenAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new Exception("等待代理处理请求超时");
    }

    private static byte[] Concat(byte[] first, byte[] second) => [.. first, .. second];

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>程序集内的字面量是 UTF-16LE；按出现次数核对参数只由 Core 提供。</summary>
    private static int CountUtf16(string path, string value)
    {
        var haystack = File.ReadAllBytes(path);
        var needle = Encoding.Unicode.GetBytes(value);
        var count = 0;
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
                if (haystack[start + offset] != needle[offset]) { match = false; break; }
            if (match) count++;
        }
        return count;
    }

    private static Task Sync(Action action) { action(); return Task.CompletedTask; }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    /// <summary>受控上游：记录写入并立即 EOF，足以断言代理把连接交给了绑定网卡的连接器。</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream written = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
    }
}
