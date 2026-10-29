using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using AutoNet.Core;

// Explicitly invoked offline QA: both proxy and destination are loopback listeners.
internal static class WebViewProxyRegression
{
    public static async Task<int> RunAsync()
    {
        using var proxy = new LoopbackServer(true);
        using var destination = new LoopbackServer(false);
        var profile = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "issue33", "webview-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WebViewProxyRegression).Assembly.Location);
        start.ArgumentList.Add("--webview-proxy-peer");
        start.ArgumentList.Add(proxy.Port.ToString());
        start.ArgumentList.Add(destination.Port.ToString());
        start.ArgumentList.Add(profile);
        using var child = Process.Start(start)!;
        try
        {
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(40));
            if (ready != "READY") throw new InvalidOperationException("WebView fixture did not become ready: " + ready);
            proxy.Stop();
            await child.StandardInput.WriteLineAsync("continue");
            var done = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var requests = proxy.Requests.ToArray();
            var hello = proxy.ClientHello;
            var oldName = Encoding.ASCII.GetBytes("0123456789.invalid");
            var actualName = Encoding.ASCII.GetBytes("login.csust.edu.cn");
            var nameOffset = hello is null ? -1 : hello.AsSpan().IndexOf(oldName);
            if (nameOffset >= 0) actualName.CopyTo(hello!.AsSpan(nameOffset));
            var passed = done == "DONE" && child.ExitCode == 0 && destination.Accepted == 0 &&
                hello is not null && nameOffset >= 0 && AuthChannelProxy.IsAllowedClientHello(hello) &&
                requests.Any(line => line.Contains("/index", StringComparison.Ordinal)) &&
                requests.Any(line => line.Contains("/fetch", StringComparison.Ordinal)) &&
                requests.Any(line => line.StartsWith($"CONNECT 127.0.0.1:{destination.Port} ", StringComparison.Ordinal));
            Console.WriteLine(passed ? "PASS WebView 代理：导航、fetch、WebSocket 经代理且代理停止不直连" :
                $"FAIL WebView 代理：done={done}, exit={child.ExitCode}, direct={destination.Accepted}, requests={string.Join(" | ", requests)}");
            return passed ? 0 : 1;
        }
        catch (Exception e)
        {
            Console.WriteLine("FAIL WebView 代理：" + e.GetType().Name + " " + e.Message);
            return 1;
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }

    public static async Task<int> RunPeerAsync(string[] args)
    {
        var job = ChannelProcessJob.CreateForCurrentProcess();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000),
                Size = new System.Drawing.Size(640, 480), ShowInTaskbar = false };
            var view = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            form.Shown += async (_, _) =>
            {
                try
                {
                    var options = new CoreWebView2EnvironmentOptions($"--proxy-server=http://127.0.0.1:{args[1]} --proxy-bypass-list=<-loopback> --disable-quic --disable-background-networking");
                    var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: args[3], options: options);
                    await view.EnsureCoreWebView2Async(environment);
                    if (!job.Contains(checked((int)view.CoreWebView2.BrowserProcessId)) ||
                        environment.GetProcessInfos().Any(info => !job.Contains(checked((int)info.ProcessId))))
                        throw new InvalidOperationException("WebView 进程未全部进入会话进程组");
                    var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    view.CoreWebView2.NavigationCompleted += (_, _) => navigation.TrySetResult();
                    view.CoreWebView2.Navigate($"http://127.0.0.1:{args[2]}/index");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await view.CoreWebView2.ExecuteScriptAsync($"fetch('http://127.0.0.1:{args[2]}/fetch').catch(()=>{{}}); try {{ new WebSocket('ws://127.0.0.1:{args[2]}/socket'); }} catch {{ }}; true;");
                    await Task.Delay(1500);
                    var tlsNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    view.CoreWebView2.NavigationCompleted += (_, _) => tlsNavigation.TrySetResult();
                    view.CoreWebView2.Navigate("https://0123456789.invalid/");
                    await tlsNavigation.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Console.WriteLine("READY"); Console.Out.Flush();
                    await Task.Run(() => Console.ReadLine());
                    view.CoreWebView2.Navigate($"http://127.0.0.1:{args[2]}/after-stop");
                    await Task.Delay(2000);
                    var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.2"), 7, [IPAddress.Parse("10.0.0.1")]);
                    await using (var actualProxy = await AuthChannelProxy.StartAsync(link, job))
                    {
                        using var actualView = new WebView2 { Dock = DockStyle.Fill };
                        form.Controls.Add(actualView);
                        var actualOptions = new CoreWebView2EnvironmentOptions($"--proxy-server=http://127.0.0.1:{actualProxy.Port} --proxy-bypass-list=<-loopback> --disable-quic --disable-background-networking");
                        var actualEnvironment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: args[3] + "-production", options: actualOptions);
                        await actualView.EnsureCoreWebView2Async(actualEnvironment);
                        if (!job.Contains(checked((int)actualView.CoreWebView2.BrowserProcessId)))
                            throw new InvalidOperationException("生产代理夹具的浏览器不在进程组中");
                        var actualNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        actualView.CoreWebView2.NavigationCompleted += (_, _) => actualNavigation.TrySetResult();
                        actualView.CoreWebView2.Navigate($"http://127.0.0.1:{args[2]}/actual");
                        await actualNavigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        if (actualProxy.VerifiedClientCount == 0) throw new InvalidOperationException("生产代理未核验 WebView 连接");
                        await actualProxy.DisposeAsync();
                        actualView.CoreWebView2.Navigate($"http://127.0.0.1:{args[2]}/actual-after-stop");
                        await Task.Delay(2000);
                    }
                    Console.WriteLine("DONE"); Console.Out.Flush();
                    completion.TrySetResult(0);
                }
                catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); completion.TrySetResult(1); }
                finally { view.Dispose(); form.Close(); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly bool proxy;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public byte[]? ClientHello { get; private set; }
        private int accepted;
        public int Accepted => accepted;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public LoopbackServer(bool proxy)
        {
            this.proxy = proxy;
            listener.Start();
            _ = ListenAsync();
        }

        private async Task ListenAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref accepted);
                    using var stream = client.GetStream();
                    var bytes = new List<byte>();
                    var b = new byte[1];
                    while (bytes.Count < 16384 && await stream.ReadAsync(b, stop.Token) != 0)
                    {
                        bytes.Add(b[0]);
                        var n = bytes.Count;
                        if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
                    }
                    var request = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0];
                    Requests.Enqueue(request);
                    if (proxy && request.StartsWith("CONNECT 0123456789.invalid:443 ", StringComparison.OrdinalIgnoreCase))
                    {
                        await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), stop.Token);
                        var header = new byte[5];
                        await stream.ReadExactlyAsync(header, stop.Token);
                        var length = (header[3] << 8) | header[4];
                        if (header[0] == 22 && length is > 0 and <= 16384)
                        {
                            var hello = new byte[length];
                            await stream.ReadExactlyAsync(hello, stop.Token);
                            ClientHello = hello;
                        }
                        continue;
                    }
                    var index = proxy && request.Contains("/index", StringComparison.Ordinal);
                    var body = index ? "<!doctype html><title>offline fixture</title>" : "";
                    var response = $"HTTP/1.1 {(index ? "200 OK" : "403 Forbidden")}\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or IOException) { }
        }

        public void Stop() { stop.Cancel(); listener.Stop(); }
        public void Dispose() { Stop(); stop.Dispose(); }
    }
}
