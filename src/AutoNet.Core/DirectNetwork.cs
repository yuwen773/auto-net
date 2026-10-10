using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AutoNet.Core;

public static class DirectNetwork
{
    public static HttpClient CreateClient(WifiLink link)
    {
        if (link.Address is null || link.Index == 0) throw new InvalidOperationException("Wi-Fi 尚未取得 IPv4 地址。");
        // A recreated Wi-Fi client must not reuse DNS answers from a previous association.
        var cache = new ConcurrentDictionary<string, (DateTimeOffset Until, IPAddress[] Addresses)>();
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
                    ? new[] { literal } : await ResolveAsync(context.DnsEndPoint.Host, link, cache, ct);
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

    private static async Task<IPAddress[]> ResolveAsync(string host, WifiLink link, ConcurrentDictionary<string, (DateTimeOffset Until, IPAddress[] Addresses)> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(host, out var cached) && cached.Until > DateTimeOffset.UtcNow) return cached.Addresses;
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
                var addresses = DnsPacket.Parse(buffer.AsSpan(0, length), id, host);
                if (addresses.Length == 0) continue;
                cache[host] = (DateTimeOffset.UtcNow.AddSeconds(30), addresses);
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

    public static IPAddress[] Parse(ReadOnlySpan<byte> data, ushort id, string? expectedHost = null)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != id ||
            (data[2] & 0xf8) != 0x80 || (data[2] & 2) != 0 || (data[3] & 15) != 0)
            throw new InvalidDataException("Invalid DNS response.");
        var offset = 12;
        var questions = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (questions != 1) throw new InvalidDataException("Unexpected DNS question count.");
        var question = ReadName(data, ref offset);
        if (offset + 4 > data.Length || BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]) != 1)
            throw new InvalidDataException("Invalid DNS question.");
        if (expectedHost is not null && !question.Equals(new System.Globalization.IdnMapping().GetAscii(expectedHost).TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mismatched DNS question.");
        offset += 4;
        var addresses = new List<(string Owner, IPAddress Address)>();
        var aliases = new List<(string Owner, string Target)>();
        for (var i = 0; i < answers; i++)
        {
            var owner = ReadName(data, ref offset);
            if (offset + 10 > data.Length) throw new InvalidDataException("Truncated DNS record.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
            offset += 10;
            if (offset + length > data.Length) throw new InvalidDataException("Truncated DNS address.");
            if (type == 1 && cls == 1 && length == 4) addresses.Add((owner, new IPAddress(data.Slice(offset, 4))));
            if (type == 5 && cls == 1)
            {
                var end = offset;
                var target = ReadName(data, ref end);
                if (end != offset + length) throw new InvalidDataException("Invalid CNAME length.");
                aliases.Add((owner, target));
            }
            offset += length;
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { question };
        for (var i = 0; i < aliases.Count; i++)
            foreach (var alias in aliases)
                if (names.Contains(alias.Owner)) names.Add(alias.Target);
        return addresses.Where(a => names.Contains(a.Owner)).Select(a => a.Address).Distinct().ToArray();
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        var seen = new HashSet<int>();
        var cursor = offset;
        var jumped = false;
        for (var count = 0; count < 128; count++)
        {
            if (cursor >= data.Length || !seen.Add(cursor)) throw new InvalidDataException("Invalid DNS name.");
            var length = data[cursor++];
            if (!jumped) offset = cursor;
            if (length == 0) return string.Join('.', labels);
            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) throw new InvalidDataException("Truncated DNS pointer.");
                var target = ((length & 0x3f) << 8) | data[cursor++];
                if (!jumped) offset = cursor;
                jumped = true;
                cursor = target;
                continue;
            }
            if (length > 63 || cursor + length > data.Length) throw new InvalidDataException("Invalid DNS label.");
            labels.Add(Encoding.ASCII.GetString(data.Slice(cursor, length)));
            cursor += length;
            if (!jumped) offset = cursor;
        }
        throw new InvalidDataException("DNS name too long.");
    }
}
