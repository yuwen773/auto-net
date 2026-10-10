namespace AutoNet.Core;

public sealed record NetworkDiagnosticReport
{
    public bool WifiConnected { get; init; }
    public string WifiState { get; init; } = "Unknown";
    public int InterfaceIndex { get; init; }
    public string? WifiIpv4 { get; init; }
    public string[] DnsServers { get; init; } = [];
    public bool? DirectOnline { get; init; }
    public string Portal { get; init; } = "Unknown";
    public string ProbeType { get; init; } = "HTTPS robots 内容检查和 Dr.COM 只读状态查询";
    public string SocketBinding { get; init; } = "未发起";
    public RouteSelection Route { get; init; } = new("Unknown");
    public string PhysicalExitEvidence { get; init; } = "未核验，需脱敏抓包";
    public bool Mutations => false;
}

// No credential, Wi-Fi connect, storage or mutation dependency is available to diagnostics.
public static class NetworkDiagnostics
{
    public static async Task<NetworkDiagnosticReport> RunAsync(Settings settings, WifiLink link, HttpClient http, RouteSelection route, CancellationToken ct)
    {
        var report = new NetworkDiagnosticReport
        {
            WifiConnected = link.Connected, WifiState = link.State.ToString(), InterfaceIndex = link.Index,
            WifiIpv4 = link.Address?.ToString(), DnsServers = link.DnsServers.Select(a => a.ToString()).ToArray(), Route = route
        };
        if (link.State != WifiState.Ready) return report;
        var online = await HealthChecks.InternetAsync(http, ct);
        var portal = PortalState.Unknown;
        try { portal = await new DrcomPortal(http, new Uri(settings.PortalOrigin)).InspectAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException) { ct.ThrowIfCancellationRequested(); }
        return report with { DirectOnline = online, Portal = portal.ToString(), SocketBinding = "生产客户端使用指定 Wi-Fi 接口和源 IPv4；不等于物理出口证明" };
    }
}
