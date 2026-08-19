using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Rung.Simulator;

/// <summary>
/// 一台模拟的西门子 PLC。
/// <para>
/// 报文编码全部自己实现，不引用 Rung 的任何代码——这是它作为测试对手方的前提。
/// 支持 COTP 握手、通讯建立协商、多项读、单项写，以及一整套故障注入开关。
/// </para>
/// <para>
/// 端口传 0 时由系统分配，因此可以在一个进程里同时起很多台，
/// 用来验证多设备编排而不必占用固定端口。
/// </para>
/// </summary>
public sealed class S7SimulatorServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SimulatedMemory _memory = new();
    private readonly List<(SimAddress Address, string Type, SignalGenerator Generator)> _signals = [];
    private readonly Task _acceptLoop;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Lock _gate = new();

    private bool _disposed;
    private DateTime _lastForcedDrop = DateTime.UtcNow;

    /// <summary>启动一台模拟设备。</summary>
    public S7SimulatorServer(SimulatedDeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Name = config.Name;
        NegotiatedPduLength = config.NegotiatedPduLength;
        Faults = config.Faults ?? new FaultInjection();

        foreach (var signal in config.Signals)
        {
            var address = SimulatedMemory.ParseAddress(signal.Address);
            _signals.Add((address, signal.Type, SignalGenerator.Create(signal)));
        }

        // 常量信号只在启动时写一次，之后客户端写进去的值才留得住
        RefreshSignals(initialize: true);

        _listener = new TcpListener(IPAddress.Loopback, config.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    /// <summary>设备名，用于日志。</summary>
    public string Name { get; }

    /// <summary>实际监听端口。构造时传 0 会得到系统分配的端口。</summary>
    public int Port { get; }

    /// <summary>协商时返回的 PDU 长度。</summary>
    public ushort NegotiatedPduLength { get; }

    /// <summary>故障注入开关，运行时可改。</summary>
    public FaultInjection Faults { get; }

    /// <summary>累计完成的收发次数，用来断言批量合并确实减少了往返。</summary>
    public int ExchangeCount { get; private set; }

    /// <summary>当前建立的连接数。</summary>
    public int ConnectionCount { get; private set; }

    /// <summary>
    /// 直接把原始字节写进存储区。测试里用它摆出确定的初值，
    /// 比配一堆常量信号直观得多。
    /// </summary>
    public void Poke(string address, params byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var parsed = SimulatedMemory.ParseAddress(address);
        lock (_gate)
        {
            data.CopyTo(_memory.GetArea(parsed.Area, parsed.DbNumber).AsSpan(parsed.ByteOffset));
        }
    }

    /// <summary>读出存储区的原始字节，用于验证写命令确实落到了设备上。</summary>
    public byte[] Peek(string address, int length)
    {
        var parsed = SimulatedMemory.ParseAddress(address);
        lock (_gate)
        {
            return _memory.Read(parsed.Area, parsed.DbNumber, parsed.ByteOffset, length);
        }
    }

    /// <summary>把所有动态信号推进到当前时刻。</summary>
    private void RefreshSignals(bool initialize = false)
    {
        var elapsed = DateTime.UtcNow - _startedUtc;

        foreach (var (address, type, generator) in _signals)
        {
            if (!initialize && !generator.Overwrites)
            {
                continue;
            }

            _memory.Write(address, type, generator.ValueAt(elapsed));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                ConnectionCount++;

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
            var buffer = new byte[4096];
            var exchangesOnThisConnection = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);

                    var frameLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
                    await stream.ReadExactlyAsync(buffer.AsMemory(4, frameLength - 4), cancellationToken)
                        .ConfigureAwait(false);

                    if (Faults.ResponseDelayMs > 0)
                    {
                        await Task.Delay(Faults.ResponseDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    byte[] response;
                    lock (_gate)
                    {
                        response = Respond(buffer.AsSpan(0, frameLength));
                        ExchangeCount++;
                    }

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                    exchangesOnThisConnection++;

                    if (ShouldDrop(exchangesOnThisConnection))
                    {
                        return; // using 会关掉连接，客户端看到的就是对端断开
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException
                                          or SocketException)
            {
                // 客户端断开
            }
        }
    }

    private bool ShouldDrop(int exchangesOnThisConnection)
    {
        if (Faults.DropAfterExchanges > 0 && exchangesOnThisConnection >= Faults.DropAfterExchanges)
        {
            return true;
        }

        if (Faults.DropEverySeconds <= 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastForcedDrop).TotalSeconds < Faults.DropEverySeconds)
        {
            return false;
        }

        _lastForcedDrop = now;
        return true;
    }

    private byte[] Respond(ReadOnlySpan<byte> request)
    {
        if (request[5] == 0xE0) // COTP 连接请求
        {
            return Faults.RejectConnections
                ? [0x03, 0x00, 0x00, 0x07, 0x02, 0x80, 0x00]
                : [0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, 0x00, 0x01, 0x00, 0x02, 0x00,
                   0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x01];
        }

        var pduReference = BinaryPrimitives.ReadUInt16BigEndian(request[11..]);

        return request[17] switch
        {
            0xF0 => BuildSetupResponse(pduReference),
            0x04 => BuildReadResponse(request, pduReference),
            0x05 => BuildWriteResponse(request, pduReference),
            _ => throw new InvalidOperationException($"模拟器不支持功能码 0x{request[17]:X2}"),
        };
    }

    private byte[] BuildSetupResponse(ushort pduReference)
    {
        var response = new byte[27];
        WriteAckHeader(response, pduReference, parameterLength: 8, dataLength: 0);

        response[19] = 0xF0;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(21), 1);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(23), 1);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(25), NegotiatedPduLength);

        return response;
    }

    private byte[] BuildReadResponse(ReadOnlySpan<byte> request, ushort pduReference)
    {
        RefreshSignals();

        var itemCount = request[18];
        var payloads = new List<(bool Ok, byte[] Data)>(itemCount);

        for (var i = 0; i < itemCount; i++)
        {
            var spec = request.Slice(19 + (i * 12), 12);
            var count = BinaryPrimitives.ReadUInt16BigEndian(spec[4..]);
            var db = BinaryPrimitives.ReadUInt16BigEndian(spec[6..]);
            var area = spec[8];
            var bitAddress = (spec[9] << 16) | (spec[10] << 8) | spec[11];

            if (Faults.FailingDbNumbers.Contains(db))
            {
                payloads.Add((false, []));
                continue;
            }

            payloads.Add((true, _memory.Read(area, db, bitAddress / 8, count)));
        }

        var dataLength = 0;
        for (var i = 0; i < payloads.Count; i++)
        {
            dataLength += payloads[i].Ok ? 4 + payloads[i].Data.Length : 4;

            if (i != payloads.Count - 1 && payloads[i].Ok && (payloads[i].Data.Length & 1) == 1)
            {
                dataLength++; // 除末项外，奇数长度补一个填充字节
            }
        }

        var response = new byte[21 + dataLength];
        WriteAckHeader(response, pduReference, parameterLength: 2, dataLength: (ushort)dataLength);
        response[19] = 0x04;
        response[20] = (byte)payloads.Count;

        var offset = 21;
        for (var i = 0; i < payloads.Count; i++)
        {
            var (ok, data) = payloads[i];
            if (!ok)
            {
                response[offset] = 0x0A; // 对象不存在
                offset += 4;
                continue;
            }

            response[offset] = 0xFF;
            response[offset + 1] = 0x04; // 字节/字，长度以位计
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(offset + 2), (ushort)(data.Length * 8));
            data.CopyTo(response.AsSpan(offset + 4));
            offset += 4 + data.Length;

            if (i != payloads.Count - 1 && (data.Length & 1) == 1)
            {
                offset++;
            }
        }

        return response;
    }

    private byte[] BuildWriteResponse(ReadOnlySpan<byte> request, ushort pduReference)
    {
        var response = new byte[22];
        WriteAckHeader(response, pduReference, parameterLength: 2, dataLength: 1);
        response[19] = 0x05;
        response[20] = 0x01;

        if (Faults.RejectWrites)
        {
            response[21] = 0x03; // 不允许访问该对象
            return response;
        }

        var spec = request.Slice(19, 12);
        var db = BinaryPrimitives.ReadUInt16BigEndian(spec[6..]);
        var area = spec[8];
        var bitAddress = (spec[9] << 16) | (spec[10] << 8) | spec[11];
        var isBit = spec[3] == 0x01;

        var dataOffset = 19 + 12;
        var payload = request[(dataOffset + 4)..];
        var target = _memory.GetArea(area, db);

        if (isBit)
        {
            var mask = (byte)(1 << (bitAddress % 8));
            target[bitAddress / 8] = payload[0] != 0
                ? (byte)(target[bitAddress / 8] | mask)
                : (byte)(target[bitAddress / 8] & ~mask);
        }
        else
        {
            var byteLength = BinaryPrimitives.ReadUInt16BigEndian(request[(dataOffset + 2)..]) / 8;
            payload[..byteLength].CopyTo(target.AsSpan(bitAddress / 8));
        }

        response[21] = 0xFF;
        return response;
    }

    private static void WriteAckHeader(
        Span<byte> response, ushort pduReference, ushort parameterLength, ushort dataLength)
    {
        response[0] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(response[2..], (ushort)response.Length);
        response[4] = 0x02;
        response[5] = 0xF0;
        response[6] = 0x80;
        response[7] = 0x32;
        response[8] = 0x03; // Ack_Data
        BinaryPrimitives.WriteUInt16BigEndian(response[11..], pduReference);
        BinaryPrimitives.WriteUInt16BigEndian(response[13..], parameterLength);
        BinaryPrimitives.WriteUInt16BigEndian(response[15..], dataLength);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
