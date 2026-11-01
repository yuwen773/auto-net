using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

namespace AutoNet.Core;

// Loopback-only proxy. Its connector is the existing adapter-bound network boundary.
internal sealed class AuthChannelProxy : IAsyncDisposable
{
    /// <summary>
    /// 出站连接工厂。生产实现固定为 <see cref="DirectNetwork.ConnectAsync"/>（IP_UNICAST_IF + 源地址绑定），
    /// 回归注入受控实现来断言代理确实把连接交给绑定网卡的路径；它只接收主机与端口，不提供
    /// “把允许域名映射到本地地址”的口子。
    /// </summary>
    internal delegate Task<Stream> ProxyConnector(string host, int port, WifiLink link, CancellationToken ct);

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly WifiLink link;
    private readonly ChannelProcessJob? job;
    private readonly ProxyConnector connector;
    private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
    private readonly SemaphoreSlim slots = new(32);
    private readonly Task acceptLoop;
    private int verifiedClients;
    private int disposed;

    private AuthChannelProxy(TcpListener listener, WifiLink link, ChannelProcessJob? job, ProxyConnector connector)
    {
        this.listener = listener;
        this.link = link;
        this.job = job;
        this.connector = connector;
        // 托盘界面线程与代理共用进程：接受循环和隧道 I/O 必须跑在线程池上，
        // 否则每个 await 的续体都会回到 Avalonia 界面线程，页面加载会拖住整个窗口。
        acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsRunning => !stopping.IsCancellationRequested && !acceptLoop.IsCompleted;
    internal int VerifiedClientCount => Volatile.Read(ref verifiedClients);

    internal static Task<AuthChannelProxy> StartAsync(WifiLink link, ChannelProcessJob? job = null, ProxyConnector? connector = null)
    {
        if (link.State != WifiState.Ready) throw new InvalidOperationException("校园 Wi-Fi 未就绪。");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new AuthChannelProxy(listener, link, job, connector ?? DirectNetwork.ConnectAsync));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                var task = ServeAsync(client);
                clients[client] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(client, out var ignored); slots.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stream = client.GetStream();
                var owner = await ProxyClientIdentity.FindOwnerAsync(client, timeout.Token);
                if (owner is null || job?.Contains(owner.Value) != true)
                {
                    await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
                    return;
                }
                Interlocked.Increment(ref verifiedClients);
                var header = await ReadHeaderAsync(stream, timeout.Token);
                var route = ParseRequest(header);
                if (route == ProxyRoute.HttpRoot)
                {
                    using var httpUpstream = await connector(ProxyHostPolicy.PortalHost, 80, link, timeout.Token);
                    // Host 与连接目标都取自同一常量，避免两处主机名漂移。
                    var request = Encoding.ASCII.GetBytes(
                        "GET / HTTP/1.1\r\nHost: " + ProxyHostPolicy.PortalHost + "\r\nConnection: close\r\n\r\n");
                    await httpUpstream.WriteAsync(request, timeout.Token);
                    await httpUpstream.CopyToAsync(stream, stopping.Token);
                    return;
                }
                if (route != ProxyRoute.HttpsTunnel)
                {
                    await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), timeout.Token);
                    return;
                }
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), timeout.Token);
                var recordHeader = new byte[5];
                await stream.ReadExactlyAsync(recordHeader, timeout.Token);
                var length = (recordHeader[3] << 8) | recordHeader[4];
                if (recordHeader[0] != 22 || recordHeader[1] != 3 || length is < 4 or > 16384) return;
                var hello = new byte[length];
                await stream.ReadExactlyAsync(hello, timeout.Token);
                if (!IsAllowedClientHello(hello)) return;
                using var upstream = await connector(ProxyHostPolicy.PortalHost, 443, link, timeout.Token);
                await upstream.WriteAsync(recordHeader, timeout.Token);
                await upstream.WriteAsync(hello, timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                using var tunnel = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                var toPortal = stream.CopyToAsync(upstream, tunnel.Token);
                var fromPortal = upstream.CopyToAsync(stream, tunnel.Token);
                await Task.WhenAny(toPortal, fromPortal);
                tunnel.Cancel();
                try { await Task.WhenAll(toPortal, fromPortal); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    internal enum ProxyRoute { Denied, HttpRoot, HttpsTunnel }

    internal static bool IsAllowedClientHello(ReadOnlySpan<byte> hello)
    {
        if (hello.Length < 42 || hello[0] != 1 || ((hello[1] << 16) | (hello[2] << 8) | hello[3]) != hello.Length - 4 || hello[4] != 3)
            return false;
        var offset = 38;
        if (offset >= hello.Length) return false;
        var sessionLength = hello[offset++];
        offset += sessionLength;
        if (offset + 2 > hello.Length) return false;
        var cipherLength = (hello[offset] << 8) | hello[offset + 1];
        offset += 2;
        if (cipherLength < 2 || (cipherLength & 1) != 0 || offset + cipherLength >= hello.Length) return false;
        offset += cipherLength;
        var compressionLength = hello[offset++];
        offset += compressionLength;
        if (compressionLength == 0 || offset + 2 > hello.Length) return false;
        var extensionsLength = (hello[offset] << 8) | hello[offset + 1];
        offset += 2;
        if (offset + extensionsLength != hello.Length) return false;
        var found = false;
        while (offset < hello.Length)
        {
            if (offset + 4 > hello.Length) return false;
            var type = (hello[offset] << 8) | hello[offset + 1];
            var size = (hello[offset + 2] << 8) | hello[offset + 3];
            offset += 4;
            if (offset + size > hello.Length) return false;
            if (type == 0)
            {
                if (found || size < 5) return false;
                var listSize = (hello[offset] << 8) | hello[offset + 1];
                var nameSize = (hello[offset + 3] << 8) | hello[offset + 4];
                // SNI 是唯一在隧道内仍可核验的目标证据；两侧都按同一策略规范化。
                if (hello[offset + 2] != 0 || listSize != size - 2 || nameSize != size - 5 ||
                    !ProxyHostPolicy.IsAllowed(Encoding.ASCII.GetString(hello.Slice(offset + 5, nameSize))))
                    return false;
                found = true;
            }
            offset += size;
        }
        return found;
    }

    internal static ProxyRoute ParseRequest(string header)
    {
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        var hosts = lines.Skip(1).Where(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parts.Length != 3 || parts[2] != "HTTP/1.1" || hosts.Length != 1) return ProxyRoute.Denied;
        // 隧道与明文请求都不得携带请求体或协议升级：那会把固定门户代理变成通用转发器，
        // 也是 WebSocket 这类绕过资源过滤的路径必须由代理覆盖的原因。
        if (lines.Skip(1).Any(line => line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))) return ProxyRoute.Denied;
        var hostHeader = hosts[0][5..].Trim();
        if (parts[0] == "CONNECT")
        {
            if (!TryParseAuthority(parts[1], out var authorityHost, out var port) || port != 443 ||
                !ProxyHostPolicy.IsAllowed(authorityHost) || !HostHeaderMatches(hostHeader, 443)) return ProxyRoute.Denied;
            return ProxyRoute.HttpsTunnel;
        }
        if (parts[0] == "GET" && Uri.TryCreate(parts[1], UriKind.Absolute, out var target) && target.Scheme == "http" &&
            target.Port == 80 && target.UserInfo == "" && target.AbsolutePath == "/" && target.Query == "" &&
            target.Fragment == "" && ProxyHostPolicy.IsAllowed(target.Host) && HostHeaderMatches(hostHeader, 80))
            return ProxyRoute.HttpRoot;
        return ProxyRoute.Denied;
    }

    /// <summary>只接受 <c>host:port</c> 形式；IPv6 字面量、缺端口和多余冒号都拒绝。</summary>
    private static bool TryParseAuthority(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1) return false;
        if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        host = value[..colon];
        return host.Length > 0 && !host.Contains(':');
    }

    /// <summary>
    /// Host 头必须与请求目标一致。端口可以省略（浏览器对默认端口常省略），省略时按请求行
    /// 已校验的端口处理；一旦显式写出，就必须与请求行一致——明文写到 443 是 TLS 降级信号。
    /// </summary>
    private static bool HostHeaderMatches(string header, int port)
    {
        if (header.Length == 0) return false;
        var colon = header.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(header[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var headerPort) ||
                headerPort != port) return false;
            header = header[..colon];
        }
        return ProxyHostPolicy.IsAllowed(header);
    }

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new InvalidDataException("Incomplete proxy request.");
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("Proxy request too large.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopping.Cancel();
        listener.Stop();
        await acceptLoop;
        var active = clients.ToArray();
        foreach (var client in active) client.Key.Dispose();
        await Task.WhenAll(active.Select(pair => pair.Value));
        stopping.Dispose();
    }
}
