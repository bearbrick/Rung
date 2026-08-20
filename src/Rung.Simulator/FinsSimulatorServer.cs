using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Rung.Simulator;

/// <summary>一台模拟的欧姆龙 CPU。</summary>
public sealed record SimulatedFinsDeviceConfig
{
    /// <summary>设备名。</summary>
    public string Name { get; init; } = "fins-sim";

    /// <summary>监听端口。</summary>
    public int Port { get; init; } = 9600;

    /// <summary>信号列表。地址用 FINS 语法，如 <c>D100</c>、<c>CIO50.03</c>。</summary>
    public IReadOnlyList<SignalConfig> Signals { get; init; } = [];
}

/// <summary>
/// 欧姆龙 FINS/UDP 模拟器。
/// <para>
/// 报文构造与地址解析都是<b>独立重新实现</b>的，不引用 Rung.Protocols.Fins。
/// </para>
/// <para>
/// 与 MELSEC 模拟器的一处关键差异：<b>FINS 全程大端</b>，
/// 而且 32 位值虽然同样"低字在前"，字内却是大端——
/// 对应字节序 CDAB，不是三菱的 DCBA。
/// </para>
/// </summary>
public sealed class FinsSimulatorServer : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private readonly Lock _gate = new();

    /// <summary>存储区代码 → 字数组。位访问也落在同一块内存上。</summary>
    private readonly Dictionary<byte, ushort[]> _areas = [];

    private readonly List<(byte Area, int Word, int Bit, bool HasBit, string Type, SignalGenerator Generator)>
        _signals = [];

    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Timer _refresh;

    private bool _disposed;

    /// <summary>启动一台模拟 CPU。</summary>
    public FinsSimulatorServer(SimulatedFinsDeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Name = config.Name;

        foreach (var signal in config.Signals)
        {
            var (area, word, bit, hasBit) = ParseAddress(signal.Address);
            _signals.Add((area, word, bit, hasBit, signal.Type, SignalGenerator.Create(signal)));
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, config.Port));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        Refresh(null);
        _refresh = new Timer(Refresh, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
    }

    /// <summary>设备名。</summary>
    public string Name { get; }

    /// <summary>实际监听端口。</summary>
    public int Port { get; }

    /// <summary>丢弃接下来这么多个请求，用来模拟 UDP 丢包。</summary>
    public int DropNextRequests { get; set; }

    /// <summary>直接写字，测试里用来摆确定初值。</summary>
    public void PokeWords(string address, params ushort[] values)
    {
        var (area, word, _, _) = ParseAddress(address);

        lock (_gate)
        {
            values.CopyTo(Area(area).AsSpan(word));
        }
    }

    /// <summary>直接写位。</summary>
    public void PokeBit(string address, bool value)
    {
        var (area, word, bit, _) = ParseAddress(address);

        lock (_gate)
        {
            var words = Area(area);
            words[word] = value
                ? (ushort)(words[word] | (1 << bit))
                : (ushort)(words[word] & ~(1 << bit));
        }
    }

    /// <summary>读回字，用于验证写命令。</summary>
    public ushort[] PeekWords(string address, int count)
    {
        var (area, word, _, _) = ParseAddress(address);

        lock (_gate)
        {
            return Area(area).AsSpan(word, count).ToArray();
        }
    }

    /// <summary>读回位。</summary>
    public bool PeekBit(string address)
    {
        var (area, word, bit, _) = ParseAddress(address);

        lock (_gate)
        {
            return (Area(area)[word] & (1 << bit)) != 0;
        }
    }

    private ushort[] Area(byte code)
    {
        if (!_areas.TryGetValue(code, out var area))
        {
            area = new ushort[32768];
            _areas[code] = area;
        }

        return area;
    }

    private void Refresh(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _startedUtc;

        lock (_gate)
        {
            foreach (var (area, word, bit, hasBit, type, generator) in _signals)
            {
                if (!generator.Overwrites && state is not null)
                {
                    continue;
                }

                var value = generator.ValueAt(elapsed);
                var words = Area(area);

                if (hasBit)
                {
                    words[word] = value != 0
                        ? (ushort)(words[word] | (1 << bit))
                        : (ushort)(words[word] & ~(1 << bit));

                    continue;
                }

                WriteWords(words.AsSpan(word), type, value);
            }
        }
    }

    /// <summary>
    /// 按类型写字。32 位值低字在前，与三菱一致；但字内是大端，与三菱相反。
    /// </summary>
    private static void WriteWords(Span<ushort> destination, string type, double value)
    {
        switch (type.ToLowerInvariant())
        {
            case "int16":
                destination[0] = unchecked((ushort)(short)Math.Round(value));
                break;
            case "uint16":
                destination[0] = (ushort)Math.Round(value);
                break;
            case "int32":
                var i32 = unchecked((uint)(int)Math.Round(value));
                destination[0] = (ushort)i32;
                destination[1] = (ushort)(i32 >> 16);
                break;
            case "float32":
                var bits = BitConverter.SingleToUInt32Bits((float)value);
                destination[0] = (ushort)bits;
                destination[1] = (ushort)(bits >> 16);
                break;
            default:
                throw new ArgumentException($"FINS 模拟器不支持数据类型 \"{type}\"", nameof(type));
        }
    }

    /// <summary>独立实现的地址解析。欧姆龙全部十进制。</summary>
    private static (byte Area, int Word, int Bit, bool HasBit) ParseAddress(string address)
    {
        var text = address.Trim().ToUpperInvariant();

        var (code, prefix) = text switch
        {
            _ when text.StartsWith("CIO", StringComparison.Ordinal) => ((byte)0xB0, 3),
            _ when text.StartsWith('D') => ((byte)0x82, 1),
            _ when text.StartsWith('W') => ((byte)0xB1, 1),
            _ when text.StartsWith('H') => ((byte)0xB2, 1),
            _ when text.StartsWith('A') => ((byte)0xB3, 1),
            _ when char.IsAsciiDigit(text[0]) => ((byte)0xB0, 0),
            _ => throw new ArgumentException($"FINS 模拟器无法解析地址 \"{address}\"", nameof(address)),
        };

        var rest = text[prefix..];
        var bit = 0;
        var hasBit = false;

        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            bit = int.Parse(rest[(dot + 1)..], CultureInfo.InvariantCulture);
            hasBit = true;
            rest = rest[..dot];
        }

        return (code, int.Parse(rest, CultureInfo.InvariantCulture), bit, hasBit);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    if (DropNextRequests > 0)
                    {
                        // 模拟 UDP 丢包：收下但不回
                        DropNextRequests--;
                        continue;
                    }
                }

                byte[] response;
                lock (_gate)
                {
                    response = Respond(buffer.AsSpan(0, result.ReceivedBytes));
                }

                await _socket.SendToAsync(response, SocketFlags.None, result.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 正常停机
        }
    }

    private byte[] Respond(ReadOnlySpan<byte> request)
    {
        var serviceId = request[9];
        var srcNetwork = request[6];
        var srcNode = request[7];
        var srcUnit = request[8];

        var areaCode = request[12];
        var word = BinaryPrimitives.ReadUInt16BigEndian(request[13..]);
        var bit = request[15];
        var count = BinaryPrimitives.ReadUInt16BigEndian(request[16..]);

        var isBit = areaCode is 0x30 or 0x31 or 0x32 or 0x33 or 0x02;
        var wordCode = isBit ? ToWordCode(areaCode) : areaCode;

        if (request[11] == 0x02)
        {
            // 写
            var payload = request[18..];
            var words = Area(wordCode);

            if (isBit)
            {
                words[word] = payload[0] != 0
                    ? (ushort)(words[word] | (1 << bit))
                    : (ushort)(words[word] & ~(1 << bit));
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    words[word + i] = BinaryPrimitives.ReadUInt16BigEndian(payload[(i * 2)..]);
                }
            }

            return BuildResponse(srcNetwork, srcNode, srcUnit, serviceId, 0x02, []);
        }

        // 读
        var source = Area(wordCode);

        if (isBit)
        {
            var data = new byte[count];
            for (var i = 0; i < count; i++)
            {
                data[i] = (byte)((source[word] & (1 << (bit + i))) != 0 ? 1 : 0);
            }

            return BuildResponse(srcNetwork, srcNode, srcUnit, serviceId, 0x01, data);
        }

        var payloadBytes = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payloadBytes.AsSpan(i * 2), source[word + i]);
        }

        return BuildResponse(srcNetwork, srcNode, srcUnit, serviceId, 0x01, payloadBytes);
    }

    private static byte ToWordCode(byte bitCode) => bitCode switch
    {
        0x30 => 0xB0,
        0x31 => 0xB1,
        0x32 => 0xB2,
        0x33 => 0xB3,
        _ => 0x82,
    };

    private static byte[] BuildResponse(
        byte network, byte node, byte unit, byte serviceId, byte src, ReadOnlySpan<byte> data)
    {
        var response = new byte[14 + data.Length];

        response[0] = 0xC0;        // 响应
        response[1] = 0x00;
        response[2] = 0x02;
        response[3] = network;     // 目的地就是原来的来源
        response[4] = node;
        response[5] = unit;
        response[6] = 0x00;
        response[7] = 0x01;
        response[8] = 0x00;
        response[9] = serviceId;   // 原样回传，客户端靠它对上请求
        response[10] = 0x01;
        response[11] = src;
        response[12] = 0x00;       // 结束码：正常
        response[13] = 0x00;

        data.CopyTo(response.AsSpan(14));

        return response;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _refresh.DisposeAsync().ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期内
        }

        _shutdown.Dispose();
    }
}
