using System.Net.Sockets;
using Rung.Abstractions;
using Rung.Protocols.S7;

namespace Rung.Drivers.S7;

/// <summary>
/// 西门子 S7 设备驱动。一个实例对应一台设备的一条长连接。
/// <para>
/// <b>不是线程安全的。</b>调用方（采集调度器）负责把同一设备的读写串行化到单一队列，
/// 读写共用一条通道、写命令插队。这个约定让驱动可以复用固定缓冲区，
/// 每轮采集零分配。
/// </para>
/// <para>
/// 每次读都不重连——PLC 的连接资源很有限（S7-300 通常只有十几条），
/// 频繁建连会挤占产线本身的通讯。连接一旦失效就置为
/// <see cref="DriverState.Faulted"/>，交给上层的重连状态机按退避策略处理。
/// </para>
/// </summary>
public sealed class S7Driver : IDeviceDriver
{
    /// <summary>接收缓冲区。协商 PDU 最大 960，加上 ISO 头也远小于此，留足余量。</summary>
    private const int BufferSize = 2048;

    private readonly DeviceOptions _deviceOptions;
    private readonly S7DriverOptions _s7Options;
    private readonly byte[] _receiveBuffer = new byte[BufferSize];
    private readonly byte[] _sendBuffer = new byte[BufferSize];

    private Socket? _socket;
    private ushort _pduReference;
    private bool _disposed;

    /// <summary>创建一个驱动实例。此时不发起任何网络 IO。</summary>
    public S7Driver(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deviceOptions = options;
        _s7Options = S7DriverOptions.FromDeviceOptions(options);
        DeviceId = options.DeviceId;
    }

    /// <inheritdoc/>
    public string DeviceId { get; }

    /// <inheritdoc/>
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <inheritdoc/>
    public int MaxPduLength { get; private set; }

    /// <inheritdoc/>
    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        State = DriverState.Connecting;

        try
        {
            var port = _deviceOptions.Port > 0 ? _deviceOptions.Port : S7Protocol.DefaultPort;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _socket = socket;

            using var timeout = CreateTimeout(cancellationToken);
            await socket.ConnectAsync(_deviceOptions.Host, port, timeout.Token).ConfigureAwait(false);

            await HandshakeAsync(timeout.Token).ConfigureAwait(false);
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
        MaxPduLength = 0;
        State = DriverState.Disconnected;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadPlan CreateReadPlan(IReadOnlyList<TagDef> tags)
    {
        EnsureConnected();

        return S7ReadPlanner.Create(
            tags,
            MaxPduLength,
            new S7ReadPlannerOptions { MaxGapBytes = _s7Options.MaxGapBytes });
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

        if (plan is not S7ReadPlan s7Plan)
        {
            throw new ArgumentException($"读取计划必须由 {nameof(S7Driver)} 编译", nameof(plan));
        }

        if (destination.Length < s7Plan.Tags.Count)
        {
            throw new ArgumentException(
                $"目标数组长度 {destination.Length} 小于点位数 {s7Plan.Tags.Count}", nameof(destination));
        }

        var timestamp = DateTime.UtcNow;

        // 配置有问题的点位每轮都要如实标记出来，不能留着上一轮的旧值
        foreach (var issue in s7Plan.Issues)
        {
            destination[issue.TagIndex] = TagValue.Bad(
                s7Plan.Tags[issue.TagIndex].DataType, TagQuality.ConfigError, timestamp);
        }

        var goodCount = 0;
        for (var requestIndex = 0; requestIndex < s7Plan.Requests.Count; requestIndex++)
        {
            var frameLength = await ExchangeReadAsync(s7Plan.Requests[requestIndex], cancellationToken)
                .ConfigureAwait(false);

            goodCount += DecodeResponse(s7Plan, requestIndex, frameLength, destination, timestamp);
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

        var address = S7AddressParser.Parse(tag.Address);
        var isBit = tag.DataType == TagDataType.Bool;

        Span<byte> payload = stackalloc byte[S7ValueCodec.GetReadByteLength(tag)];
        var payloadLength = S7ValueCodec.Encode(payload, tag, value);

        var length = S7RequestBuilder.WriteWriteRequest(
            _sendBuffer, NextPduReference(), address, payload[..payloadLength], isBit);

        var frameLength = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);

        var result = S7ResponseReader.ReadWriteResult(_receiveBuffer.AsSpan(0, frameLength));
        if (result != S7ReturnCode.Success)
        {
            throw new RungException($"写入点位 {tag.Name} 失败：设备返回 {result}");
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        CloseSocket();

        return ValueTask.CompletedTask;
    }

    /// <summary>COTP 连接 + 通讯建立。两步都成功才算连上。</summary>
    private async ValueTask HandshakeAsync(CancellationToken cancellationToken)
    {
        var length = S7RequestBuilder.WriteConnectionRequest(
            _sendBuffer, _s7Options.Rack, _s7Options.Slot, _s7Options.ConnectionType);

        var frameLength = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);
        S7ResponseReader.ValidateConnectionConfirm(_receiveBuffer.AsSpan(0, frameLength));

        length = S7RequestBuilder.WriteSetupCommunication(
            _sendBuffer, NextPduReference(), _s7Options.RequestedPduLength);

        frameLength = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);
        MaxPduLength = S7ResponseReader.ReadNegotiatedPduLength(_receiveBuffer.AsSpan(0, frameLength));
    }

    private async ValueTask<int> ExchangeReadAsync(
        S7ReadRequestGroup request,
        CancellationToken cancellationToken)
    {
        var items = request.Items as S7ReadItem[] ?? [.. request.Items];
        var length = S7RequestBuilder.WriteReadRequest(_sendBuffer, NextPduReference(), items);

        return await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把响应拆回各个点位。
    /// <para>
    /// 单项失败只影响该点位——产线上配错一个 DB 号是常事，
    /// 不该让同一批次的其余点位跟着遭殃。
    /// </para>
    /// </summary>
    private int DecodeResponse(
        S7ReadPlan plan,
        int requestIndex,
        int frameLength,
        TagValue[] destination,
        DateTime timestamp)
    {
        var frame = _receiveBuffer.AsSpan(0, frameLength);
        var cursor = S7ResponseReader.ReadResults(frame);
        var expected = plan.Requests[requestIndex].Items.Count;

        if (cursor.ItemCount != expected)
        {
            throw new ProtocolException(
                $"响应含 {cursor.ItemCount} 个数据项，请求的是 {expected} 个");
        }

        // 先把每一项的位置和状态记下来，再按点位逐个解码。
        // 游标是单向的，而点位与数据项不是一一对应（一项承载多个点位）
        Span<int> offsets = stackalloc int[expected];
        Span<int> lengths = stackalloc int[expected];
        Span<S7ReturnCode> codes = stackalloc S7ReturnCode[expected];

        for (var i = 0; i < expected; i++)
        {
            cursor.TryReadNext(out codes[i], out offsets[i], out lengths[i]);
        }

        var goodCount = 0;
        foreach (var tagIndex in plan.TagIndexesByRequest[requestIndex])
        {
            var tag = plan.Tags[tagIndex];
            var location = plan.Locations[tagIndex];
            var code = codes[location.ItemIndex];

            if (code != S7ReturnCode.Success)
            {
                destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                continue;
            }

            var start = offsets[location.ItemIndex] + location.ByteOffset;
            var available = lengths[location.ItemIndex] - location.ByteOffset;

            if (available < S7ValueCodec.GetReadByteLength(tag))
            {
                destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                continue;
            }

            destination[tagIndex] = S7ValueCodec.Decode(
                frame.Slice(start, available), tag, location.BitOffset, timestamp);
            goodCount++;
        }

        return goodCount;
    }

    /// <summary>发一帧、收一帧。返回收到的整帧长度。</summary>
    private async ValueTask<int> ExchangeAsync(int requestLength, CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new RungException($"设备 {DeviceId} 未连接");

        using var timeout = CreateTimeout(cancellationToken);

        try
        {
            await SendAllAsync(socket, _sendBuffer.AsMemory(0, requestLength), timeout.Token)
                .ConfigureAwait(false);

            return await ReceiveFrameAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // 一次交换失败之后链路就不可信了，无论失败原因是什么：
            // socket 异常、超时、报文非法、对端优雅关闭（read 返回 0）都算。
            // 这里刻意不做异常类型白名单——漏掉任何一类，设备都会卡在 Connected
            // 状态上，重连状态机永远不介入，表现为"这台设备再也不上报数据了"。
            //
            // 但不在这里自行重连：退避策略是上层的事，
            // 驱动闷头重试会把 PLC 那十几条连接资源占满，影响产线本身。
            State = DriverState.Faulted;
            throw;
        }
    }

    private static async ValueTask SendAllAsync(
        Socket socket,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < payload.Length)
        {
            sent += await socket.SendAsync(payload[sent..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 先读 4 字节 TPKT 头拿到整帧长度，再把剩下的收齐。
    /// TCP 不保证消息边界，不这么做就会踩半包/粘包。
    /// </summary>
    private async ValueTask<int> ReceiveFrameAsync(Socket socket, CancellationToken cancellationToken)
    {
        await ReceiveExactlyAsync(socket, _receiveBuffer.AsMemory(0, 4), cancellationToken)
            .ConfigureAwait(false);

        var frameLength = S7ResponseReader.ReadFrameLength(_receiveBuffer);
        if (frameLength > _receiveBuffer.Length)
        {
            throw new ProtocolException(
                $"响应帧 {frameLength} 字节超出接收缓冲区 {_receiveBuffer.Length} 字节");
        }

        await ReceiveExactlyAsync(socket, _receiveBuffer.AsMemory(4, frameLength - 4), cancellationToken)
            .ConfigureAwait(false);

        return frameLength;
    }

    private static async ValueTask ReceiveExactlyAsync(
        Socket socket,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var received = 0;
        while (received < destination.Length)
        {
            var read = await socket.ReceiveAsync(destination[received..], SocketFlags.None, cancellationToken)
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

    private ushort NextPduReference() => ++_pduReference;

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
            // 关闭失败没有补救手段，也不该掩盖真正的错误
        }

        _socket = null;
    }
}

/// <summary>S7 驱动工厂。Core 层按协议名找到它来创建驱动，无需引用本程序集。</summary>
public sealed class S7DriverFactory : IDeviceDriverFactory
{
    /// <inheritdoc/>
    public string Protocol => "s7";

    /// <inheritdoc/>
    public string AddressSyntaxHint =>
        "DB1.DBW10 / DB1.DBX0.5 / DB1.DBD20 / MW100 / M100.0 / I0.0 / Q1.3 / T5 / C3（支持德文 E/A/Z）";

    /// <inheritdoc/>
    public IDeviceDriver Create(DeviceOptions options) => new S7Driver(options);

    /// <summary>S7-300 的协商值，也是所有西门子 CPU 里最小的。</summary>
    private const int ConservativePduLength = 240;

    /// <inheritdoc/>
    public IReadPlan CompileOffline(DeviceOptions options, IReadOnlyList<TagDef> tags)
    {
        ArgumentNullException.ThrowIfNull(options);

        var s7Options = S7DriverOptions.FromDeviceOptions(options);

        return S7ReadPlanner.Create(tags, ConservativePduLength,
            new S7ReadPlannerOptions { MaxGapBytes = s7Options.MaxGapBytes });
    }
}
