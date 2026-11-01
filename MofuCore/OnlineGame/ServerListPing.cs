using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MofuCore.OnlineGame;

/// <summary>一次 Server List Ping 的结果；不在线时 <see cref="Online"/> 为 false，其余字段为默认值。</summary>
public sealed record ServerPingResult(
    bool Online,
    string? Motd,
    byte[]? FaviconPng,
    int OnlinePlayers,
    int MaxPlayers,
    string? VersionName)
{
    public static ServerPingResult Offline { get; } = new(false, null, null, 0, 0, null);
}

/// <summary>
/// Minecraft Java 版 Server List Ping（status 协议）：TCP 握手包 + 状态请求，
/// 解析玩家数 / MOTD / 服务器图标（favicon base64 PNG）。
/// </summary>
public static class ServerListPing
{
    private const int TimeoutMs = 4000;

    /// <summary>状态请求包：长度 1 + 包 id 0x00（必须带长度前缀，否则服务端会挂起）。</summary>
    private static readonly byte[] StatusRequestPacket = [0x01, 0x00];

    /// <summary>按地址 ping 一台服务器；host 支持带端口的 "host:port" 写法。</summary>
    public static Task<ServerPingResult> PingAsync(string address, CancellationToken cancellationToken = default)
    {
        var (host, port) = ParseAddress(address);
        return PingAsync(host, port, cancellationToken);
    }

    public static async Task<ServerPingResult> PingAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return ServerPingResult.Offline;
        if (port <= 0 || port > 65535) port = 25565;

        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(TimeoutMs);
            var token = cancellation.Token;

            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);

            using var stream = client.GetStream();
            await stream.WriteAsync(BuildHandshake(host, port), token).ConfigureAwait(false);
            await stream.WriteAsync(StatusRequestPacket, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            var payload = await ReadStatusPayloadAsync(stream, token).ConfigureAwait(false);
            return Parse(payload);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException or ArgumentException)
        {
            return ServerPingResult.Offline;
        }
    }

    /// <summary>"host" / "host:port" → (host, port)；端口非法时回退 25565。</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        var value = (address ?? string.Empty).Trim();
        if (value.Length == 0) return (string.Empty, 25565);

        // [IPv6]:port 与 host:port 两种形态
        int port = 25565;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0 && close + 1 < value.Length && value[close + 1] == ':')
            {
                if (int.TryParse(value[(close + 2)..], out var v6Port)) port = v6Port;
                value = value[1..close];
            }
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out var namedPort))
            {
                port = namedPort;
                value = value[..colon];
            }
        }

        return (value, port);
    }

    private static byte[] BuildHandshake(string host, int port)
    {
        var payload = new MemoryStream();
        WriteVarInt(payload, 0x00);                       // 包 id：握手
        WriteVarInt(payload, -1);                         // 协议版本：-1 = 只查状态
        WriteString(payload, host);
        payload.Write(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((ushort)port))); // unsigned short 大端
        WriteVarInt(payload, 1);                          // 下一状态：status

        var packet = new MemoryStream();
        WriteVarInt(packet, (int)payload.Length);
        packet.Write(payload.ToArray());
        return packet.ToArray();
    }

    /// <summary>读完整一个 status 响应包并取出 JSON 字符串（跳过包 id）。</summary>
    private static async Task<string> ReadStatusPayloadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var length = await ReadVarIntAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length <= 0 || length > 8 * 1024 * 1024) throw new IOException("bad packet length");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                .ConfigureAwait(false);
            if (chunk <= 0) throw new IOException("connection closed");
            read += chunk;
        }

        var cursor = 0;
        var packetId = ReadVarInt(buffer, ref cursor);
        if (packetId != 0x00) throw new IOException("unexpected packet id");

        var jsonLength = ReadVarInt(buffer, ref cursor);
        if (jsonLength <= 0 || cursor + jsonLength > buffer.Length) throw new IOException("bad json length");
        return Encoding.UTF8.GetString(buffer, cursor, jsonLength);
    }

    private static ServerPingResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        int online = 0, max = 0;
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
        {
            online = ReadInt(players, "online");
            max = ReadInt(players, "max");
        }

        string? version = null;
        if (root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.Object)
        {
            version = versionElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }

        // MOTD：JSON 文本组件或旧版 § 字符串，原样交给上层渲染器解析
        string? motd = null;
        if (root.TryGetProperty("description", out var description))
        {
            motd = description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : description.GetRawText();
        }

        byte[]? favicon = null;
        if (root.TryGetProperty("favicon", out var faviconElement)
            && faviconElement.ValueKind == JsonValueKind.String
            && faviconElement.GetString() is { } dataUrl)
        {
            var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
            if (dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                try
                {
                    favicon = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                }
                catch (FormatException)
                {
                    // 图标数据坏掉时按没有图标处理
                }
            }
        }

        return new ServerPingResult(true, motd, favicon, online, max, version);
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static void WriteVarInt(MemoryStream stream, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            stream.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }

        stream.WriteByte((byte)v);
    }

    private static void WriteString(MemoryStream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static async Task<int> ReadVarIntAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var result = 0;
        var buffer = new byte[1];
        for (var shift = 0; shift < 35; shift += 7)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new IOException("connection closed");
            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new IOException("varint too long");
    }

    private static int ReadVarInt(byte[] buffer, ref int cursor)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (cursor >= buffer.Length) throw new IOException("varint truncated");
            var b = buffer[cursor++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }

        throw new IOException("varint too long");
    }
}
