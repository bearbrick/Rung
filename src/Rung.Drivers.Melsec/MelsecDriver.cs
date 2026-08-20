using System.Net.Sockets;
using Rung.Abstractions;
using Rung.Protocols.Melsec;

namespace Rung.Drivers.Melsec;

/// <summary>MELSEC 驱动参数。</summary>
public sealed record MelsecDriverOptions
{
    /// <summary>CPU 侧的监视定时器，毫秒。</summary>
    public int MonitoringTimerMs { get; init; } = 4000;

    /// <summary>批量合并时允许跨越的最大空洞点数。</summary>
    public int MaxGapPoints { get; init; } = 16;

    /// <summary>从设备配置里解析 MELSEC 特有参数。</summary>
    public static MelsecDriverOptions FromDeviceOptions(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MelsecDriverOptions
        {
            MonitoringTimerMs = options.GetInt32("monitoringTimerMs", 4000),
            MaxGapPoints = options.GetInt32("maxGapPoints", 16),
        };
    }
}

/// <summary>
/// 三菱 MELSEC 驱动，走 MC 3E 二进制帧 over TCP。
/// <para>
/// 报文自己实现，理由和 S7 一样：没有许可证干净又活跃维护的现成库。
/// 编解码在 <c>Rung.Protocols.Melsec</c> 里做成无 IO 的纯函数，
/// 这里只负责传输。
/// </para>
/// <para>
/// <b>三菱的 32 位值是低字在前</b>：一个 Float32 占 D(n) 和 D(n+1)，
/// D(n) 是低 16 位。因此这类点位的字节序通常要配成 <see cref="ByteOrder.DCBA"/>。
/// 配错不会报错，只会读出一个看着像那么回事的数。
/// </para>
/// <para>和所有驱动一样，<b>不是线程安全的</b>：调用方负责把同一设备的读写串行化。</para>
/// </summary>
public sealed class MelsecDriver : IDeviceDriver
{
    private const int BufferSize = 8192;

    private readonly DeviceOptions _deviceOptions;
    private readonly MelsecDriverOptions _melsecOptions;
    private readonly byte[] _receiveBuffer = new byte[BufferSize];
    private readonly byte[] _sendBuffer = new byte[BufferSize];

    private Socket? _socket;
    private bool _disposed;

    /// <summary>创建一个驱动实例。此时不发起任何网络 IO。</summary>
    public MelsecDriver(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deviceOptions = options;
        _melsecOptions = MelsecDriverOptions.FromDeviceOptions(options);
        DeviceId = options.DeviceId;
    }

    /// <inheritdoc/>
    public string DeviceId { get; }

    /// <inheritdoc/>
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <summary>MC 不协商帧长，上限是协议固定的。这里报单次可读的最大字节数。</summary>
    public int MaxFrameBytes => State == DriverState.Connected ? MelsecProtocol.MaxWordPoints * 2 : 0;

    /// <inheritdoc/>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CloseSocket();
        State = DriverState.Connecting;

        try
        {
            var port = _deviceOptions.Port > 0 ? _deviceOptions.Port : MelsecProtocol.DefaultPort;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _socket = socket;

            using var timeout = CreateTimeout(cancellationToken);
            await socket.ConnectAsync(_deviceOptions.Host, port, timeout.Token).ConfigureAwait(false);

            // MC 没有握手：连上就能发请求。这一点比 S7 简单得多
            State = DriverState.Connected;
        }
        catch
        {
            State = DriverState.Faulted;
            CloseSocket();
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        CloseSocket();
        State = DriverState.Disconnected;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadPlan CreateReadPlan(IReadOnlyList<TagDef> tags)
    {
        EnsureConnected();

        return MelsecReadPlanner.Create(
            tags, new MelsecReadPlannerOptions { MaxGapPoints = _melsecOptions.MaxGapPoints });
    }

    /// <inheritdoc/>
    public async ValueTask<int> ExecuteAsync(
        IReadPlan plan,
        TagValue[] destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureConnected();

        if (plan is not MelsecReadPlan melsecPlan)
        {
            throw new ArgumentException($"读取计划必须由 {nameof(MelsecDriver)} 编译", nameof(plan));
        }

        var timestamp = DateTime.UtcNow;

        foreach (var issue in melsecPlan.Issues)
        {
            destination[issue.TagIndex] = TagValue.Bad(
                melsecPlan.Tags[issue.TagIndex].DataType, TagQuality.ConfigError, timestamp);
        }

        var goodCount = 0;

        for (var requestIndex = 0; requestIndex < melsecPlan.Requests.Count; requestIndex++)
        {
            var request = melsecPlan.Requests[requestIndex];

            var length = MelsecFrame.WriteBatchReadRequest(
                _sendBuffer, request.Address, request.Points, _melsecOptions.MonitoringTimerMs);

            var frameLength = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);

            goodCount += Decode(melsecPlan, requestIndex, frameLength, destination, timestamp);
        }

        return goodCount;
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(TagDef tag, TagValue value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);
        EnsureConnected();

        if (tag.Access == TagAccess.Read)
        {
            throw new RungException($"点位 {tag.Name} 是只读的");
        }

        var address = MelsecAddressParser.Parse(tag.Address);

        int length;
        if (address.IsBit)
        {
            Span<byte> bit = [value.AsBool() ? (byte)1 : (byte)0];
            length = MelsecFrame.WriteBatchWriteRequest(
                _sendBuffer, address, bit, _melsecOptions.MonitoringTimerMs);
        }
        else
        {
            var words = Math.Max(1, (tag.ByteLength + 1) / 2);
            Span<byte> payload = stackalloc byte[words * 2];
            payload.Clear();

            TagValueCodec.EncodeScalar(payload, tag, value);

            length = MelsecFrame.WriteBatchWriteRequest(
                _sendBuffer, address, payload, _melsecOptions.MonitoringTimerMs);
        }

        var frameLength = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);

        // 写响应没有数据段，能解析出来就说明 CPU 接受了
        MelsecFrame.ReadResponseData(_receiveBuffer.AsSpan(0, frameLength));
    }

    private int Decode(
        MelsecReadPlan plan,
        int requestIndex,
        int frameLength,
        TagValue[] destination,
        DateTime timestamp)
    {
        var request = plan.Requests[requestIndex];
        var data = MelsecFrame.ReadResponseData(_receiveBuffer.AsSpan(0, frameLength));
        var isBit = request.Address.IsBit;

        Span<byte> bits = isBit ? stackalloc byte[request.Points] : [];
        if (isBit)
        {
            // 位单位响应每字节装两个点，先展开成每点一字节再按下标取
            MelsecFrame.UnpackBits(data, bits, request.Points);
        }

        var goodCount = 0;

        foreach (var tagIndex in plan.TagIndexesByRequest[requestIndex])
        {
            var tag = plan.Tags[tagIndex];
            var location = plan.Locations[tagIndex];

            if (isBit)
            {
                if (location.Offset >= bits.Length)
                {
                    destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                    continue;
                }

                destination[tagIndex] = TagValue.FromBool(bits[location.Offset] != 0, timestamp);
                goodCount++;
                continue;
            }

            var byteLength = Math.Max(2, (tag.ByteLength + 1) / 2 * 2);
            if (location.Offset + byteLength > data.Length)
            {
                destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                continue;
            }

            var slice = data.Slice(location.Offset, byteLength);

            destination[tagIndex] = tag.DataType switch
            {
                // 字软元件里的布尔：整个字非零为真。MELSEC 上表示状态位
                // 一般用 M/B 继电器，用 D 存布尔属于兼容写法
                TagDataType.Bool => TagValue.FromBool(slice[0] != 0 || slice[1] != 0, timestamp),
                TagDataType.Bytes => TagValue.FromBytes(slice[..tag.Length].ToArray(), timestamp),
                _ => TagValueCodec.DecodeScalar(slice, tag, timestamp),
            };

            goodCount++;
        }

        return goodCount;
    }

    private async ValueTask<int> ExchangeAsync(int requestLength, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new RungException($"设备 {DeviceId} 未连接");

        using var timeout = CreateTimeout(cancellationToken);

        try
        {
            var sent = 0;
            while (sent < requestLength)
            {
                sent += await socket
                    .SendAsync(_sendBuffer.AsMemory(sent, requestLength - sent), SocketFlags.None, timeout.Token)
                    .ConfigureAwait(false);
            }

            return await ReceiveFrameAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // 与其他驱动一致：一次交换失败之后链路就不可信，交给上层重连状态机
            State = DriverState.Faulted;
            throw;
        }
    }

    /// <summary>先读 9 字节头拿到整帧长度，再把剩下的收齐。TCP 不保证消息边界。</summary>
    private async ValueTask<int> ReceiveFrameAsync(Socket socket, CancellationToken cancellationToken)
    {
        await ReceiveExactlyAsync(
            socket, _receiveBuffer.AsMemory(0, MelsecProtocol.ResponseHeaderLength), cancellationToken)
            .ConfigureAwait(false);

        var frameLength = MelsecFrame.ReadFrameLength(_receiveBuffer);
        if (frameLength > _receiveBuffer.Length)
        {
            throw new ProtocolException(
                $"MC 响应帧 {frameLength} 字节超出接收缓冲区 {_receiveBuffer.Length} 字节");
        }

        await ReceiveExactlyAsync(
            socket,
            _receiveBuffer.AsMemory(
                MelsecProtocol.ResponseHeaderLength,
                frameLength - MelsecProtocol.ResponseHeaderLength),
            cancellationToken).ConfigureAwait(false);

        return frameLength;
    }

    private static async ValueTask ReceiveExactlyAsync(
        Socket socket, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var received = 0;
        while (received < destination.Length)
        {
            var read = await socket
                .ReceiveAsync(destination[received..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new RungException("对端关闭了连接");
            }

            received += read;
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromMilliseconds(_deviceOptions.TimeoutMs));

        return source;
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != DriverState.Connected)
        {
            throw new RungException($"设备 {DeviceId} 当前状态为 {State}，无法执行操作");
        }
    }

    private void CloseSocket()
    {
        if (_socket is null)
        {
            return;
        }

        try
        {
            _socket.Dispose();
        }
        catch (SocketException)
        {
            // 关闭失败没有补救手段
        }

        _socket = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            CloseSocket();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>MELSEC 驱动工厂。</summary>
public sealed class MelsecDriverFactory : IDeviceDriverFactory
{
    /// <inheritdoc/>
    public string Protocol => "melsec-mc";

    /// <inheritdoc/>
    public string AddressSyntaxHint =>
        "D100 / M200 / R500 / TN10（十进制编号）· X1F / Y2A / B100 / W1A0 / ZR3000（十六进制编号）"
        + " · 32 位值通常需要把字节序配成 DCBA（低字在前）";

    /// <inheritdoc/>
    public IDeviceDriver Create(DeviceOptions options) => new MelsecDriver(options);

    /// <inheritdoc/>
    public IReadPlan CompileOffline(DeviceOptions options, IReadOnlyList<TagDef> tags)
    {
        ArgumentNullException.ThrowIfNull(options);

        var melsecOptions = MelsecDriverOptions.FromDeviceOptions(options);

        return MelsecReadPlanner.Create(
            tags, new MelsecReadPlannerOptions { MaxGapPoints = melsecOptions.MaxGapPoints });
    }
}
