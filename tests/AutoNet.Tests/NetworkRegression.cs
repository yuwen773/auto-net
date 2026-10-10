using System.Buffers.Binary;
using System.Net;
using AutoNet.Core;

internal static class NetworkRegression
{
    public static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("网络诊断：原生关联状态不查询当前连接，属性缺失不误判断连", () =>
        {
            var link = new WifiLink(false, null, null, 7, []);
            foreach (var state in new[] { 0, 2, 3, 5, 6, 7, 99 })
            {
                var observed = Wifi.ReadConnection(link, state, "campus", () => throw new Exception("不应查询当前连接"));
                var policy = new RecoveryPolicy();
                Require(policy.Decide(new(observed.State, false, PortalState.Unknown), DateTimeOffset.UtcNow, true, true) == ActionKind.None);
            }
            Require(Wifi.ReadConnection(link, 1, "campus", () => "").State == WifiState.Unavailable);
            Require(Wifi.ReadConnection(link, 4, "campus", () => throw new Exception()).State == WifiState.Disconnected);
            Require(Wifi.ReadConnection(link, 1, "campus", () => "other").State == WifiState.OtherProfile);
            Require(Wifi.ReadConnection(link, 1, "campus", () => "campus").State == WifiState.WaitingForAddress);
            return Task.CompletedTask;
        }),
        ("网络诊断：TUN与Wi-Fi仅显示路由推断，未知不伪装成直连", () =>
        {
            foreach (var kind in new[] { "Tun", "Wifi", "Unknown" })
            {
                var status = HealthChecks.DescribeSystemPath(true, new(kind, 7));
                Require(status.Contains("出口未核验") && !status.Contains("未经过隧道"));
                if (kind == "Unknown") Require(status.Contains("路由未知"));
            }
            return Task.CompletedTask;
        }),
        ("网络诊断：离线HTTP夹具不登录，缺IP不发请求", async () =>
        {
            var settings = new Settings(); var handler = new MonitorHttp { Online = true };
            using var http = new HttpClient(handler);
            var link = new WifiLink(true, null, IPAddress.Parse("10.0.0.1"), 7, [IPAddress.Parse("10.0.0.2")]);
            var report = await NetworkDiagnostics.RunAsync(settings, link, http, new("Tun", 9), default);
            Require(report.DirectOnline == true && !report.Mutations && report.PhysicalExitEvidence.Contains("未核验"));
            Require(handler.Requests.All(u => u.AbsolutePath != "/eportal/portal/login"));
            var count = handler.Requests.Count;
            var missingIp = await NetworkDiagnostics.RunAsync(settings, link with { Address = null }, http, new("Unknown"), default);
            Require(missingIp.DirectOnline is null && count == handler.Requests.Count);
        }),
        ("DNS：CNAME链只采用目标关联的A记录", () =>
        {
            var alias = DnsPacket.Query("alias.example.com", 123)[12..^4];
            var cname = new byte[] { 0xc0, 0x0c, 0, 5, 0, 1, 0, 0, 0, 20, 0, (byte)alias.Length }.Concat(alias).ToArray();
            var address = alias.Concat(new byte[] { 0, 1, 0, 1, 0, 0, 0, 20, 0, 4, 1, 2, 3, 4 }).ToArray();
            var response = Response(cname, address);
            Require(DnsPacket.Parse(response, 123, "example.com").Single().ToString() == "1.2.3.4");
            Require(DnsPacket.Parse(Response(address), 123, "example.com").Length == 0);
            return Task.CompletedTask;
        }),
        ("DNS：问题不匹配、压缩指针循环和非标准响应被拒绝", () =>
        {
            var valid = Response(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 20, 0, 4, 1, 2, 3, 4 });
            Reject(() => DnsPacket.Parse(valid, 123, "other.example"));
            var loop = (byte[])valid.Clone(); var answerStart = DnsPacket.Query("example.com", 123).Length;
            loop[answerStart + 1] = (byte)answerStart; Reject(() => DnsPacket.Parse(loop, 123));
            var opcode = (byte[])valid.Clone(); opcode[2] = 0x89; Reject(() => DnsPacket.Parse(opcode, 123));
            var truncated = (byte[])valid.Clone(); truncated[2] |= 2; Reject(() => DnsPacket.Parse(truncated, 123));
            return Task.CompletedTask;
        })
    ];

    private static byte[] Response(params byte[][] answers)
    {
        var query = DnsPacket.Query("example.com", 123);
        query[2] = 0x81; query[3] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(6), (ushort)answers.Length);
        return query.Concat(answers.SelectMany(a => a)).ToArray();
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("未拒绝异常DNS响应"); }
    private static void Require(bool condition) { if (!condition) throw new Exception("网络诊断行为不符合预期"); }
}
