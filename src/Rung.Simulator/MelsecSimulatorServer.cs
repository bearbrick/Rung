using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Rung.Simulator;

/// <summary>一台模拟的三菱 MELSEC CPU。</summary>
public sealed record SimulatedMelsecDeviceConfig
{
    /// <summary>设备名。</summary>
    public string Name { get; init; } = "melsec-sim";

    /// <summary>监听端口。</summary>
    public int Port { get; init; } = 6000;

    /// <summary>信号列表。地址用 MELSEC 语法，如 <c>D100</c>、<c>M200</c>、<c>X1F</c>。</summary>
    public IReadOnlyList<SignalConfig> Signals { get; init; } = [];
}

/// <summary>
/// MELSEC MC 3E 从站模拟器。
/// <para>
/// 报文构造与地址解析都是<b>独立重新实现</b>的，不引用 Rung.Protocols.Melsec。
/// 两边同源的话，一个写错的偏移量会同时体现在模拟器和被测代码上，
/// 测试全绿但真机一读就错。
/// </para>
/// </summary>
public sealed class MelsecSimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly Lock _gate = new();

    /// <summary>字软元件：软元件代码 → 字数组。</summary>
    private readonly Dictionary<byte, ushort[]> _words = [];

    /// <summary>位软元件：软元件代码 → 位数组。</summary>
    private readonly Dictionary<byte, bool[]> _bits = [];

    private readonly List<(byte Device, int Number, string Type, SignalGenerator Generator)> _signals = [];
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Timer _refresh;

    private bool _disposed;

    /// <summary>启动一台模拟 CPU。</summary>
    public MelsecSimulatorServer(SimulatedMelsecDeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Name = config.Name;

        foreach (var signal in config.Signals)
        {
            var (device, number) = ParseAddress(signal.Address);
            _signals.Add((device, number, signal.Type, SignalGenerator.Create(signal)));
        }

        _listener = new TcpListener(IPAddress.Loopback, config.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        Refresh(null);
        _refresh = new Timer(Refresh, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>设备名。</summary>
    public string Name { get; }

    /// <summary>实际监听端口。</summary>
    public int Port { get; }

    /// <summary>直接写字软元件，测试里用来摆确定初值。</summary>
    public void PokeWords(string address, params ushort[] values)
    {
        var (device, number) = ParseAddress(address);

        lock (_gate)
        {
            values.CopyTo(WordArea(device).AsSpan(number));
        }
    }

    /// <summary>直接写位软元件。</summary>
    public void PokeBit(string address, bool value)
    {
        var (device, number) = ParseAddress(address);

        lock (_gate)
        {
            BitArea(device)[number] = value;
        }
    }

    /// <summary>读回字软元件，用于验证写命令。</summary>
    public ushort[] PeekWords(string address, int count)
    {
        var (device, number) = ParseAddress(address);

        lock (_gate)
        {
            return WordArea(device).AsSpan(number, count).ToArray();
        }
    }

    /// <summary>读回位软元件。</summary>
    public bool PeekBit(string address)
    {
        var (device, number) = ParseAddress(address);

        lock (_gate)
        {
            return BitArea(device)[number];
        }
    }

    private ushort[] WordArea(byte device)
    {
        if (!_words.TryGetValue(device, out var area))
        {
            area = new ushort[16384];
            _words[device] = area;
        }

        return area;
    }

    private bool[] BitArea(byte device)
    {
        if (!_bits.TryGetValue(device, out var area))
        {
            area = new bool[16384];
            _bits[device] = area;
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
            foreach (var (device, number, type, generator) in _signals)
            {
                if (!generator.Overwrites && state is not null)
                {
                    continue;
                }

                var value = generator.ValueAt(elapsed);

                if (IsBitDevice(device))
                {
                    BitArea(device)[number] = value != 0;
                    continue;
                }

                WriteWords(WordArea(device).AsSpan(number), type, value);
            }
        }
    }

    /// <summary>
    /// 按类型写字软元件。
    /// <para>
    /// 32 位值<b>低字在前</b>：D(n) 是低 16 位，D(n+1) 是高 16 位。
    /// 这是 MELSEC 的规定，也是接入时最容易搞反的地方。
    /// </para>
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
                throw new ArgumentException($"MELSEC 模拟器不支持数据类型 \"{type}\"", nameof(type));
        }
    }

    private static bool IsBitDevice(byte device)
        => device is 0x9C or 0x9D or 0x90 or 0x92 or 0x93 or 0xA0;

    /// <summary>独立实现的地址解析。X/Y/B/W/ZR 是十六进制编号，其余十进制。</summary>
    private static (byte Device, int Number) ParseAddress(string address)
    {
        var text = address.Trim().ToUpperInvariant();

        var (device, prefix, hex) = text switch
        {
            _ when text.StartsWith("ZR", StringComparison.Ordinal) => ((byte)0xB0, 2, true),
            _ when text.StartsWith("TN", StringComparison.Ordinal) => ((byte)0xC2, 2, false),
            _ when text.StartsWith("CN", StringComparison.Ordinal) => ((byte)0xC5, 2, false),
            _ when text.StartsWith('X') => ((byte)0x9C, 1, true),
            _ when text.StartsWith('Y') => ((byte)0x9D, 1, true),
            _ when text.StartsWith('M') => ((byte)0x90, 1, false),
            _ when text.StartsWith('L') => ((byte)0x92, 1, false),
            _ when text.StartsWith('F') => ((byte)0x93, 1, false),
            _ when text.StartsWith('B') => ((byte)0xA0, 1, true),
            _ when text.StartsWith('D') => ((byte)0xA8, 1, false),
            _ when text.StartsWith('W') => ((byte)0xB4, 1, true),
            _ when text.StartsWith('R') => ((byte)0xAF, 1, false),
            _ => throw new ArgumentException($"MELSEC 模拟器无法解析地址 \"{address}\"", nameof(address)),
        };

        var number = int.Parse(text[prefix..],
            hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);

        return (device, number);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // 正常停机
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 9), cancellationToken)
                        .ConfigureAwait(false);

                    var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(7));
                    await stream.ReadExactlyAsync(buffer.AsMemory(9, dataLength), cancellationToken)
                        .ConfigureAwait(false);

                    byte[] response;
                    lock (_gate)
                    {
                        response = Respond(buffer.AsSpan(0, 9 + dataLength));
                    }

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException
                                          or OperationCanceledException or SocketException)
            {
                // 客户端断开
            }
        }
    }

    private byte[] Respond(ReadOnlySpan<byte> request)
    {
        var command = BinaryPrimitives.ReadUInt16LittleEndian(request[11..]);
        var subcommand = BinaryPrimitives.ReadUInt16LittleEndian(request[13..]);

        var number = request[15] | (request[16] << 8) | (request[17] << 16);
        var device = request[18];
        var points = BinaryPrimitives.ReadUInt16LittleEndian(request[19..]);

        return command switch
        {
            0x0401 => BuildReadResponse(device, number, points, subcommand == 1),
            0x1401 => BuildWriteResponse(request, device, number, points, subcommand == 1),
            _ => BuildError(0xC059),
        };
    }

    private byte[] BuildReadResponse(byte device, int number, int points, bool bitUnits)
    {
        var dataBytes = bitUnits ? (points + 1) / 2 : points * 2;
        var response = new byte[11 + dataBytes];
        WriteResponseHeader(response, (ushort)(2 + dataBytes), endCode: 0);

        if (bitUnits)
        {
            var area = BitArea(device);
            for (var i = 0; i < points; i++)
            {
                if (area[number + i])
                {
                    response[11 + (i / 2)] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
                }
            }

            return response;
        }

        var words = WordArea(device);
        for (var i = 0; i < points; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(11 + (i * 2)), words[number + i]);
        }

        return response;
    }

    private byte[] BuildWriteResponse(
        ReadOnlySpan<byte> request, byte device, int number, int points, bool bitUnits)
    {
        var payload = request[21..];

        if (bitUnits)
        {
            var area = BitArea(device);
            for (var i = 0; i < points; i++)
            {
                var b = payload[i / 2];
                area[number + i] = (i % 2 == 0 ? b >> 4 : b & 0x0F) != 0;
            }
        }
        else
        {
            var words = WordArea(device);
            for (var i = 0; i < points; i++)
            {
                words[number + i] = BinaryPrimitives.ReadUInt16LittleEndian(payload[(i * 2)..]);
            }
        }

        var response = new byte[11];
        WriteResponseHeader(response, dataLength: 2, endCode: 0);

        return response;
    }

    private static byte[] BuildError(ushort endCode)
    {
        var response = new byte[11];
        WriteResponseHeader(response, dataLength: 2, endCode);

        return response;
    }

    private static void WriteResponseHeader(Span<byte> response, ushort dataLength, ushort endCode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(response, 0x00D0);
        response[2] = 0x00;
        response[3] = 0xFF;
        BinaryPrimitives.WriteUInt16LittleEndian(response[4..], 0x03FF);
        response[6] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(response[7..], dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(response[9..], endCode);
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
        _listener.Stop();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期内
        }

        _shutdown.Dispose();
    }
}
