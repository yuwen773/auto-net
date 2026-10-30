using System.Net;
using System.Net.Sockets;
using System.Text;
using AutoNet.Core;
using System.Diagnostics;

internal static class AuthChannelProxyRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("通道代理：非会话进程不得使用环回代理", async () =>
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardInput = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(AuthChannelProxyRegression).Assembly.Location);
            start.ArgumentList.Add("--proxy-job-test-peer");
            using var peer = Process.Start(start)!;
            try
            {
                var line = await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (line is null || !line.StartsWith("PORT ", StringComparison.Ordinal) || !int.TryParse(line[5..], out var port))
                    throw new Exception("代理夹具未就绪：" + line);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n"));
                var buffer = new byte[256];
                var count = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Require(Encoding.ASCII.GetString(buffer, 0, count).StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal), "外部进程使用了会话代理");
                await peer.StandardInput.WriteLineAsync();
                Require(await peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "TLS-REJECTED", "会话内明文隧道未被拒绝");
                await peer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(peer.ExitCode == 0, "代理夹具异常退出");
            }
            finally { if (!peer.HasExited) { peer.Kill(); await peer.WaitForExitAsync(); } }
        }),
        ("通道代理：环回连接可核验实际客户端进程", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var accepted = await listener.AcceptTcpClientAsync();
                var owner = await ProxyClientIdentity.FindOwnerAsync(accepted, CancellationToken.None);
                Require(owner == Environment.ProcessId, "环回连接的所属进程核验不符");
            }
            finally { listener.Stop(); }
        }),
        ("通道代理：TLS ClientHello 仅接受固定门户 SNI", () =>
        {
            Require(AuthChannelProxy.IsAllowedClientHello(TlsClientHelloFixture.Handshake("LOGIN.CSUST.EDU.CN")), "可信 SNI 被拒绝");
            Require(!AuthChannelProxy.IsAllowedClientHello(TlsClientHelloFixture.Handshake("login.csust.edu.cn.evil.example")), "伪装 SNI 被放行");
            var truncated = TlsClientHelloFixture.Handshake("login.csust.edu.cn");
            Require(!AuthChannelProxy.IsAllowedClientHello(truncated[..^1]), "截断握手被放行");
            return Task.CompletedTask;
        }),
        ("通道代理：CONNECT 后明文不能转发到443", () =>
        {
            Require(!AuthChannelProxy.IsAllowedClientHello(Encoding.ASCII.GetBytes("GET /synthetic-password HTTP/1.1\r\n\r\n")),
                "明文被当作 TLS ClientHello");
            return Task.CompletedTask;
        }),
        ("通道代理：域名伪装、IP、端口与主机头不符均拒绝", async () =>
        {
            var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.2"), 7, [IPAddress.Parse("10.0.0.1")]);
            await using var proxy = await AuthChannelProxy.StartAsync(link);
            foreach (var request in new[]
            {
                "CONNECT login.csust.edu.cn.evil.com:443 HTTP/1.1\r\nHost: login.csust.edu.cn.evil.com:443\r\n\r\n",
                "CONNECT x.login.csust.edu.cn:443 HTTP/1.1\r\nHost: x.login.csust.edu.cn:443\r\n\r\n",
                "CONNECT csust.edu.cn:443 HTTP/1.1\r\nHost: csust.edu.cn:443\r\n\r\n",
                "CONNECT 127.0.0.1:443 HTTP/1.1\r\nHost: 127.0.0.1:443\r\n\r\n",
                "CONNECT login.csust.edu.cn:80 HTTP/1.1\r\nHost: login.csust.edu.cn:80\r\n\r\n",
                "GET http://login.csust.edu.cn:443/ HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n",
                "GET http://login.csust.edu.cn/synthetic-password HTTP/1.1\r\nHost: login.csust.edu.cn\r\n\r\n",
                "GET http://login.csust.edu.cn/%73ynthetic-password HTTP/1.1\r\nHost: login.csust.edu.cn\r\n\r\n",
                "GET http://login.csust.edu.cn/ HTTP/1.1\r\nHost: other.example\r\n\r\n",
                "CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\nHost: evil.example\r\n\r\n"
            })
            {
                using var client = new TcpClient();
                Require(AuthChannelProxy.ParseRequest(request) == AuthChannelProxy.ProxyRoute.Denied, "伪装目标通过策略");
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                using var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
                var buffer = new byte[256];
                var count = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Require(Encoding.ASCII.GetString(buffer, 0, count).StartsWith("HTTP/1.1 403 ", StringComparison.Ordinal), "伪装或无效端口未被拒绝");
            }
        }),
        ("通道代理：明文仅允许固定门户的80端口", () =>
        {
            Require(AuthChannelProxy.ParseRequest("GET http://LOGIN.CSUST.EDU.CN:80/ HTTP/1.1\r\nHost: LOGIN.CSUST.EDU.CN\r\n\r\n") ==
                AuthChannelProxy.ProxyRoute.HttpRoot, "允许的明文目标未通过策略");
            return Task.CompletedTask;
        }),
        ("通道代理：允许域名大小写归一后仅连接HTTPS目标", () =>
        {
            Require(AuthChannelProxy.ParseRequest("CONNECT LOGIN.CSUST.EDU.CN:443 HTTP/1.1\r\nHost: LOGIN.CSUST.EDU.CN:443\r\n\r\n") ==
                AuthChannelProxy.ProxyRoute.HttpsTunnel, "允许的 HTTPS 目标未通过策略");
            return Task.CompletedTask;
        })
    ];

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task<int> RunPeerAsync()
    {
        var job = ChannelProcessJob.CreateForCurrentProcess();
        var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.2"), 7, [IPAddress.Parse("10.0.0.1")]);
        await using var proxy = await AuthChannelProxy.StartAsync(link, job);
        Console.WriteLine("PORT " + proxy.Port); Console.Out.Flush();
        await Task.Run(() => Console.ReadLine());
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("CONNECT login.csust.edu.cn:443 HTTP/1.1\r\nHost: login.csust.edu.cn:443\r\n\r\n"));
        var buffer = new byte[256];
        var response = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (!Encoding.ASCII.GetString(buffer, 0, response).StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal)) return 3;
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /synthetic-password HTTP/1.1\r\n\r\n"));
        try { if (await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != 0) return 4; }
        catch (IOException) { }
        Console.WriteLine("TLS-REJECTED"); Console.Out.Flush();
        return 0;
    }
}
