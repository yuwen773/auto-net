using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoNet.Core;

public static class DirectNetwork
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset Until, IPAddress[] Addresses)> Cache = new();

    public static HttpClient CreateClient(WifiLink link)
    {
        if (link.Address is null || link.Index == 0) throw new InvalidOperationException("Wi-Fi 尚未取得 IPv4 地址。");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                    ? new[] { literal } : await ResolveAsync(context.DnsEndPoint.Host, link, ct);
                foreach (var address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        Pin(socket, link);
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException) { socket.Dispose(); }
                    catch { socket.Dispose(); throw; }
                }
                throw new SocketException((int)SocketError.HostUnreachable);
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    private static void Pin(Socket socket, WifiLink link)
    {
        // Windows IP_UNICAST_IF is a DWORD interface index in network byte order.
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(link.Index));
        socket.Bind(new IPEndPoint(link.Address!, 0));
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, WifiLink link, CancellationToken ct)
    {
        var key = $"{link.Index}/{link.Address}/{string.Join(',', link.DnsServers.Select(a => a.ToString()))}/{host}";
        if (Cache.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Addresses;
        foreach (var dns in link.DnsServers.Take(2))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(700);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                Pin(socket, link);
                await socket.ConnectAsync(new IPEndPoint(dns, 53), timeout.Token);
                var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
                await socket.SendAsync(DnsPacket.Query(host, id), SocketFlags.None, timeout.Token);
                var buffer = new byte[4096];
                var length = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
                var addresses = DnsPacket.Parse(buffer.AsSpan(0, length), id);
                if (addresses.Length == 0) continue;
                Cache[key] = (DateTimeOffset.UtcNow.AddSeconds(30), addresses);
                return addresses;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
            catch (InvalidDataException) { }
        }
        ct.ThrowIfCancellationRequested();
        throw new SocketException((int)SocketError.HostNotFound);
    }
}

public static class DnsPacket
{
    public static byte[] Query(string host, ushort id)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 1; header[5] = 1;
        stream.Write(header);
        foreach (var label in new System.Globalization.IdnMapping().GetAscii(host).TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid DNS name.");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.Write([0, 0, 1, 0, 1]);
        return stream.ToArray();
    }

    public static IPAddress[] Parse(ReadOnlySpan<byte> data, ushort id)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != id ||
            (data[2] & 0x80) == 0 || (data[2] & 2) != 0 || (data[3] & 15) != 0)
            throw new InvalidDataException("Invalid DNS response.");
        var offset = 12;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        for (var i = 0; i < questions; i++)
        {
            SkipName(data, ref offset);
            if (offset + 4 > data.Length) throw new InvalidDataException("Truncated DNS question.");
            offset += 4;
        }
        var result = new List<IPAddress>();
        for (var i = 0; i < answers; i++)
        {
            SkipName(data, ref offset);
            if (offset + 10 > data.Length) throw new InvalidDataException("Truncated DNS record.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;
            if (offset + length > data.Length) throw new InvalidDataException("Truncated DNS address.");
            if (type == 1 && cls == 1 && length == 4) result.Add(new IPAddress(data.Slice(offset, 4)));
            offset += length;
        }
        return result.ToArray();
    }

    private static void SkipName(ReadOnlySpan<byte> data, ref int offset)
    {
        for (var labels = 0; labels < 128; labels++)
        {
            if (offset >= data.Length) throw new InvalidDataException("Truncated DNS name.");
            var length = data[offset++];
            if (length == 0) return;
            if ((length & 0xc0) == 0xc0)
            {
                if (offset >= data.Length) throw new InvalidDataException("Truncated DNS pointer.");
                offset++; return;
            }
            if (length > 63 || offset + length > data.Length) throw new InvalidDataException("Invalid DNS label.");
            offset += length;
        }
        throw new InvalidDataException("DNS name too long.");
    }
}
