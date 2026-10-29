using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

namespace AutoNet.Core;

// Loopback-only proxy. Its connector is the existing adapter-bound network boundary.
internal sealed class AuthChannelProxy : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly WifiLink link;
    private readonly ChannelProcessJob? job;
    private readonly ConcurrentDictionary<TcpClient, Task> clients = new();
    private readonly SemaphoreSlim slots = new(32);
    private readonly Task acceptLoop;
    private int verifiedClients;
    private int disposed;

    private AuthChannelProxy(TcpListener listener, WifiLink link, ChannelProcessJob? job)
    {
        this.listener = listener;
        this.link = link;
        this.job = job;
        acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool IsRunning => !stopping.IsCancellationRequested && !acceptLoop.IsCompleted;
    internal int VerifiedClientCount => Volatile.Read(ref verifiedClients);

    internal static Task<AuthChannelProxy> StartAsync(WifiLink link, ChannelProcessJob? job = null)
    {
        if (link.State != WifiState.Ready) throw new InvalidOperationException("校园 Wi-Fi 未就绪。");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new AuthChannelProxy(listener, link, job));
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
                    using var httpUpstream = await DirectNetwork.ConnectAsync("login.csust.edu.cn", 80, link, timeout.Token);
                    var request = "GET / HTTP/1.1\r\nHost: login.csust.edu.cn\r\nConnection: close\r\n\r\n"u8.ToArray();
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
                using var upstream = await DirectNetwork.ConnectAsync("login.csust.edu.cn", 443, link, timeout.Token);
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
                if (hello[offset + 2] != 0 || listSize != size - 2 || nameSize != size - 5 ||
                    !Encoding.ASCII.GetString(hello.Slice(offset + 5, nameSize)).Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase))
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
        var host = hosts[0][5..].Trim();
        if (parts[0] == "CONNECT" && parts[1].Equals("login.csust.edu.cn:443", StringComparison.OrdinalIgnoreCase) &&
            host.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) return ProxyRoute.HttpsTunnel;
        if (parts[0] == "GET" && Uri.TryCreate(parts[1], UriKind.Absolute, out var target) && target.Scheme == "http" &&
            target.Host.Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase) && target.Port == 80 &&
            target.UserInfo == "" && target.AbsolutePath == "/" && target.Query == "" && target.Fragment == "" &&
            (host.Equals("login.csust.edu.cn", StringComparison.OrdinalIgnoreCase) || host.Equals("login.csust.edu.cn:80", StringComparison.OrdinalIgnoreCase)) &&
            !lines.Any(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))) return ProxyRoute.HttpRoot;
        return ProxyRoute.Denied;
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
