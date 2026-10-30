using System.Text;

/// <summary>
/// 回归共用的 TLS ClientHello 夹具：只构造代理会解析的最小握手（单密码套件、SNI 扩展），
/// 不发起任何真实 TLS 或网络流量。认证通道与认证页边界两条回归共用，避免字节级重复。
/// </summary>
internal static class TlsClientHelloFixture
{
    /// <summary>握手消息（从 HandshakeType 开始），可直接交给 <c>AuthChannelProxy.IsAllowedClientHello</c>。</summary>
    public static byte[] Handshake(string hostname)
    {
        var name = Encoding.ASCII.GetBytes(hostname);
        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);
        body.Add(0);
        body.AddRange([0, 2, 0x13, 0x01, 1, 0]);
        var extensionLength = name.Length + 9;
        body.AddRange([(byte)(extensionLength >> 8), (byte)extensionLength, 0, 0,
            (byte)((name.Length + 5) >> 8), (byte)(name.Length + 5),
            (byte)((name.Length + 3) >> 8), (byte)(name.Length + 3), 0,
            (byte)(name.Length >> 8), (byte)name.Length]);
        body.AddRange(name);
        var size = body.Count;
        var hello = new List<byte> { 1, (byte)(size >> 16), (byte)(size >> 8), (byte)size };
        hello.AddRange(body);
        return hello.ToArray();
    }

    /// <summary>把握手消息包进 TLS 记录头：代理在 CONNECT 后先读 5 字节记录头。</summary>
    public static byte[] Record(string hostname)
    {
        var handshake = Handshake(hostname);
        var record = new byte[5 + handshake.Length];
        record[0] = 22;
        record[1] = 3;
        record[2] = 1;
        record[3] = (byte)(handshake.Length >> 8);
        record[4] = (byte)handshake.Length;
        handshake.CopyTo(record, 5);
        return record;
    }
}
