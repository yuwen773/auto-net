using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace AutoNet.Core;

public static class HealthChecks
{
    public static readonly Uri[] InternetTargets = [new("https://www.baidu.com/robots.txt"), new("https://www.microsoft.com/robots.txt")];

    public static async Task<bool> InternetAsync(HttpClient http, CancellationToken ct)
    {
        // HTTPS + known content prevents treating a captive redirect or arbitrary HTTP 200 as Internet.
        var results = await Task.WhenAll(InternetTargets.Select(uri => ProbeAsync(http, uri, true, ct)));
        return results.Any(ok => ok);
    }

    public static async Task<bool> ProbeAsync(HttpClient http, Uri uri, bool robots, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(2000);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            if (!robots) return true;
            return (await DrcomPortal.ReadLimitedAsync(response, timeout.Token)).Contains("User-agent:", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (IOException) { return false; }
    }

    public static HttpClient SystemClient(string proxy = "") => new(new SocketsHttpHandler
    {
        UseProxy = proxy != "", Proxy = proxy == "" ? null : new WebProxy(proxy), AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<string> SystemPathAsync(Settings settings, WifiLink wifi, CancellationToken ct)
    {
        using var http = SystemClient(settings.ProxyUrl);
        var ok = await InternetAsync(http, ct);
        if (settings.ProxyUrl != "") return ok ? "本机代理可达" : "本机代理不可达";
        // Verify the selected route instead of assuming that disabling an HTTP proxy bypasses TUN.
        try
        {
            using var dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsTimeout.CancelAfter(1000);
            var addresses = await Dns.GetHostAddressesAsync(InternetTargets[0].Host, dnsTimeout.Token);
            var ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip is not null && GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes()), out var index) == 0)
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.GetIPProperties().GetIPv4Properties()?.Index == index);
                if (adapter is not null && (adapter.Name + " " + adapter.Description).Contains("tun", StringComparison.OrdinalIgnoreCase))
                    return ok ? "TUN 路径可达" : "TUN 路径不可达";
                if (index == wifi.Index) return ok ? "系统直连可达（未经过隧道）" : "系统直连不可达";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (System.Net.Sockets.SocketException) { }
        catch (NetworkInformationException) { }
        return ok ? "系统路径可达（隧道未核验）" : "系统路径不可达（隧道未核验）";
    }

    public static string UuService()
    {
        try
        {
            using var service = new ServiceController("GameViewerService");
            if (service.Status != ServiceControllerStatus.Running) return "UU 服务未运行";
            var ids = new HashSet<int>();
            foreach (var name in new[] { "GameViewerServer", "GameViewerService", "GameViewerHealthd" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) { ids.Add(process.Id); }
            var count = TcpConnections.CountEstablished(ids);
            return count > 0 ? $"UU 服务运行，{count} 条 TCP 连接；会话待实测" : "UU 服务运行；在线状态待核验";
        }
        catch (InvalidOperationException) { return "未检测到 UU 服务"; }
        catch (System.ComponentModel.Win32Exception) { return "无法读取 UU 服务状态"; }
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint index);
}

internal static class TcpConnections
{
    public static int CountEstablished(HashSet<int> processes)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
        if (size <= 0) return 0;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, 2, 5, 0) != 0) return 0;
            var count = Marshal.ReadInt32(buffer);
            var established = 0;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * 24;
                if (Marshal.ReadInt32(row) == 5 && processes.Contains(Marshal.ReadInt32(row + 20))) established++;
            }
            return established;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr buffer, ref int size, bool order, int family, int tableClass, uint reserved);
}
