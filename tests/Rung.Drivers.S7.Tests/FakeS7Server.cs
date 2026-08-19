using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Rung.Drivers.S7.Tests;

/// <summary>
/// 进程内的假 S7 设备。
/// <para>
/// 它的报文构造是<b>独立于 Rung 另写一遍</b>的，不复用 S7RequestBuilder。
/// 这样测试才有意义：如果两边同源，那只能证明代码和自己一致，
/// 证明不了它跟"S7 协议"一致。现在解析器和这个编码器互为对照。
/// </para>
/// <para>
/// 有了它，握手、半包处理、批量拆分、字节序、写命令这些整条链路上的逻辑，
/// 全部可以在没有真实 PLC 的情况下自动化验证。
/// </para>
/// </summary>
internal sealed class FakeS7Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<(byte Area, ushort Db), byte[]> _memory = [];
    private readonly Task _acceptLoop;
    private bool _disposed;

    public FakeS7Server(ushort negotiatedPduLength = 240)
    {
        NegotiatedPduLength = negotiatedPduLength;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public int Port { get; }

    public ushort NegotiatedPduLength { get; }

    /// <summary>设为 true 后拒绝 COTP 连接，用于验证机架槽号配错时的表现。</summary>
    public bool RejectConnection { get; set; }

    /// <summary>对该 DB 号的读取一律返回"对象不存在"，用于验证单点失败不拖垮整批。</summary>
    public ushort? FailingDbNumber { get; set; }

    /// <summary>已完成的请求-响应次数，用于断言批量合并真的减少了往返。</summary>
    public int ExchangeCount { get; private set; }

    /// <summary>写入模拟存储区，作为采集的数据源。</summary>
    public void Poke(byte area, ushort db, int byteOffset, params byte[] data)
        => data.CopyTo(GetArea(area, db).AsSpan(byteOffset));

    /// <summary>读取模拟存储区，用于验证写命令真的落到了设备上。</summary>
    public byte[] Peek(byte area, ushort db, int byteOffset, int length)
        => GetArea(area, db).AsSpan(byteOffset, length).ToArray();

    private byte[] GetArea(byte area, ushort db)
    {
        if (!_memory.TryGetValue((area, db), out var buffer))
        {
            buffer = new byte[4096];
            _memory[(area, db)] = buffer;
        }

        return buffer;
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
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (SocketException)
        {
            // 监听器已释放
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);

                    var frameLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
                    await stream.ReadExactlyAsync(buffer.AsMemory(4, frameLength - 4), cancellationToken)
                        .ConfigureAwait(false);

                    var response = Respond(buffer.AsSpan(0, frameLength));
                    ExchangeCount++;

                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
            {
                // 客户端断开
            }
        }
    }

    private byte[] Respond(ReadOnlySpan<byte> request)
    {
        // COTP 连接请求
        if (request[5] == 0xE0)
        {
            return RejectConnection
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
            _ => throw new InvalidOperationException($"假设备不支持功能码 0x{request[17]:X2}"),
        };
    }

    private byte[] BuildSetupResponse(ushort pduReference)
    {
        var response = new byte[27];
        WriteAckHeader(response, pduReference, parameterLength: 8, dataLength: 0);

        response[19] = 0xF0;
        response[20] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(21), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(23), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(25), NegotiatedPduLength);

        return response;
    }

    private byte[] BuildReadResponse(ReadOnlySpan<byte> request, ushort pduReference)
    {
        var itemCount = request[18];
        var payloads = new List<(bool Ok, byte[] Data)>(itemCount);

        for (var i = 0; i < itemCount; i++)
        {
            var spec = request.Slice(19 + (i * 12), 12);
            var count = BinaryPrimitives.ReadUInt16BigEndian(spec[4..]);
            var db = BinaryPrimitives.ReadUInt16BigEndian(spec[6..]);
            var area = spec[8];
            var bitAddress = (spec[9] << 16) | (spec[10] << 8) | spec[11];

            if (FailingDbNumber == db)
            {
                payloads.Add((false, []));
                continue;
            }

            payloads.Add((true, GetArea(area, db).AsSpan(bitAddress / 8, count).ToArray()));
        }

        var dataLength = 0;
        for (var i = 0; i < payloads.Count; i++)
        {
            dataLength += payloads[i].Ok ? 4 + payloads[i].Data.Length : 4;

            // 除最后一项外，奇数长度的数据后面补一个填充字节
            if (i != payloads.Count - 1 && payloads[i].Ok && (payloads[i].Data.Length & 1) == 1)
            {
                dataLength++;
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
            response[offset + 1] = 0x04;                                   // 字节/字，长度以位计
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
        var spec = request.Slice(19, 12);
        var db = BinaryPrimitives.ReadUInt16BigEndian(spec[6..]);
        var area = spec[8];
        var bitAddress = (spec[9] << 16) | (spec[10] << 8) | spec[11];
        var isBit = spec[3] == 0x01;

        var dataOffset = 19 + 12;
        var payload = request[(dataOffset + 4)..];
        var target = GetArea(area, db);

        if (isBit)
        {
            var mask = (byte)(1 << (bitAddress % 8));
            if (payload[0] != 0)
            {
                target[bitAddress / 8] |= mask;
            }
            else
            {
                target[bitAddress / 8] &= (byte)~mask;
            }
        }
        else
        {
            var byteLength = BinaryPrimitives.ReadUInt16BigEndian(request[(dataOffset + 2)..]) / 8;
            payload[..byteLength].CopyTo(target.AsSpan(bitAddress / 8));
        }

        var response = new byte[22];
        WriteAckHeader(response, pduReference, parameterLength: 2, dataLength: 1);
        response[19] = 0x05;
        response[20] = 0x01;
        response[21] = 0xFF;

        return response;
    }

    private static void WriteAckHeader(Span<byte> response, ushort pduReference, ushort parameterLength, ushort dataLength)
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

    public async ValueTask DisposeAsync()
    {
        // IAsyncDisposable 要求可重复调用：测试里常常先显式停掉设备模拟断线，
        // 之后 await using 还会再释放一次
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
