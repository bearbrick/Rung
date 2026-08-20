using System.Net;
using System.Net.Sockets;
using Rung.Abstractions;
using Rung.Protocols.Fins;

namespace Rung.Drivers.Fins;

/// <summary>FINS 驱动参数。</summary>
public sealed record FinsDriverOptions
{
    /// <summary>本机节点号。同网段下通常填本机 IP 的最后一段。</summary>
    public byte SourceNode { get; init; } = 1;

    /// <summary>PLC 节点号。0 表示按目标 IP 的最后一段自动推断。</summary>
    public byte DestinationNode { get; init; }

    /// <summary>批量合并时允许跨越的最大空洞字数。</summary>
    public int MaxGapWords { get; init; } = 16;

    /// <summary>从设备配置里解析 FINS 特有参数。</summary>
    public static FinsDriverOptions FromDeviceOptions(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new FinsDriverOptions
        {
            SourceNode = (byte)options.GetInt32("sourceNode", 1),
            DestinationNode = (byte)options.GetInt32("destinationNode", 0),
            MaxGapWords = options.GetInt32("maxGapWords", 16),
        };
    }
}

/// <summary>
/// 欧姆龙 FINS/UDP 驱动。
/// <para>
/// 选 UDP 而不是 FINS/TCP：欧姆龙以太网单元默认开的就是 UDP，
/// 而且不需要 TCP 那套节点地址协商握手。代价是要自己处理丢包和乱序——
/// 每个请求带服务号，响应必须核对，否则上一次超时的响应迟到时
/// 会被当成本次结果，读出一个属于上一轮的旧值。
/// </para>
/// <para>
/// <b>欧姆龙的 32 位值是"低字在前、字内大端"</b>，对应字节序 <see cref="ByteOrder.CDAB"/>。
/// 注意与三菱的 <see cref="ByteOrder.DCBA"/> 不同——两家都低字在前，但字内字节序相反。
/// </para>
/// </summary>
public sealed class FinsDriver : IDeviceDriver
{
    private const int BufferSize = 4096;

    private readonly DeviceOptions _deviceOptions;
    private readonly FinsDriverOptions _finsOptions;
    private readonly byte[] _receiveBuffer = new byte[BufferSize];
    private readonly byte[] _sendBuffer = new byte[BufferSize];

    private Socket? _socket;
    private IPEndPoint? _endpoint;
    private FinsNode _source;
    private FinsNode _target;
    private byte _serviceId;
    private bool _disposed;

    /// <summary>创建一个驱动实例。此时不发起任何网络 IO。</summary>
    public FinsDriver(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deviceOptions = options;
        _finsOptions = FinsDriverOptions.FromDeviceOptions(options);
        DeviceId = options.DeviceId;
    }

    /// <inheritdoc/>
    public string DeviceId { get; }

    /// <inheritdoc/>
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <summary>FINS 不协商帧长，上限由协议和 UDP 报文长度决定。</summary>
    public int MaxPduLength => State == DriverState.Connected ? FinsProtocol.MaxWords * 2 : 0;

    /// <inheritdoc/>
    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CloseSocket();
        State = DriverState.Connecting;

        try
        {
            var port = _deviceOptions.Port > 0 ? _deviceOptions.Port : FinsProtocol.DefaultPort;
            var address = ResolveAddress(_deviceOptions.Host);
            _endpoint = new IPEndPoint(address, port);

            // UDP 没有连接的概念。这里 Connect 只是把对端固定下来，
            // 好处是之后收到的其他来源报文会被内核直接丢掉
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(_endpoint);
            _socket = socket;

            // 节点号没配就按 IP 最后一段推断——欧姆龙以太网单元的默认约定
            var destination = _finsOptions.DestinationNode != 0
                ? _finsOptions.DestinationNode
                : address.GetAddressBytes()[^1];

            _source = new FinsNode(0, _finsOptions.SourceNode);
            _target = new FinsNode(0, destination);

            State = DriverState.Connected;
        }
        catch
        {
            State = DriverState.Faulted;
            CloseSocket();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
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

        return FinsReadPlanner.Create(
            tags, new FinsReadPlannerOptions { MaxGapWords = _finsOptions.MaxGapWords });
    }

    /// <inheritdoc/>
    public async ValueTask<int> ExecuteAsync(
        IReadPlan plan, TagValue[] destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);
        EnsureConnected();

        if (plan is not FinsReadPlan finsPlan)
        {
            throw new ArgumentException($"读取计划必须由 {nameof(FinsDriver)} 编译", nameof(plan));
        }

        var timestamp = DateTime.UtcNow;

        foreach (var issue in finsPlan.Issues)
        {
            destination[issue.TagIndex] = TagValue.Bad(
                finsPlan.Tags[issue.TagIndex].DataType, TagQuality.ConfigError, timestamp);
        }

        var goodCount = 0;

        for (var requestIndex = 0; requestIndex < finsPlan.Requests.Count; requestIndex++)
        {
            var request = finsPlan.Requests[requestIndex];
            var serviceId = NextServiceId();

            var length = FinsFrame.WriteReadRequest(
                _sendBuffer, _source, _target, serviceId, request.Address, request.Count);

            var received = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);
            var data = FinsFrame.ReadResponseData(_receiveBuffer.AsSpan(0, received), serviceId);

            goodCount += Decode(finsPlan, requestIndex, data, destination, timestamp);
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

        var address = FinsAddressParser.Parse(tag.Address);

        if (!address.Area.IsWritable())
        {
            throw new RungException($"点位 {tag.Name} 位于 {address.Area} 区，该区按只读处理");
        }

        var serviceId = NextServiceId();
        int length;

        if (address.HasBit)
        {
            Span<byte> bit = [value.AsBool() ? (byte)1 : (byte)0];
            length = FinsFrame.WriteWriteRequest(_sendBuffer, _source, _target, serviceId, address, bit);
        }
        else
        {
            var words = Math.Max(1, (tag.ByteLength + 1) / 2);
            Span<byte> payload = stackalloc byte[words * 2];
            payload.Clear();

            TagValueCodec.EncodeScalar(payload, tag, value);

            length = FinsFrame.WriteWriteRequest(
                _sendBuffer, _source, _target, serviceId, address, payload);
        }

        var received = await ExchangeAsync(length, cancellationToken).ConfigureAwait(false);

        // 写响应没有数据段，能解析出来就说明 CPU 接受了
        FinsFrame.ReadResponseData(_receiveBuffer.AsSpan(0, received), serviceId);
    }

    private static int Decode(
        FinsReadPlan plan,
        int requestIndex,
        ReadOnlySpan<byte> data,
        TagValue[] destination,
        DateTime timestamp)
    {
        var goodCount = 0;

        foreach (var tagIndex in plan.TagIndexesByRequest[requestIndex])
        {
            var tag = plan.Tags[tagIndex];
            var location = plan.Locations[tagIndex];

            if (location.Offset + 2 > data.Length)
            {
                destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                continue;
            }

            if (location.HasBit)
            {
                // 字是大端：位 0-7 落在低字节，也就是第二个字节
                var word = (ushort)((data[location.Offset] << 8) | data[location.Offset + 1]);
                destination[tagIndex] = TagValue.FromBool((word & (1 << location.Bit)) != 0, timestamp);
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
            await socket.SendAsync(_sendBuffer.AsMemory(0, requestLength), SocketFlags.None, timeout.Token)
                .ConfigureAwait(false);

            return await socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            State = DriverState.Faulted;
            throw;
        }
    }

    /// <summary>服务号逐次递增，用于把响应和请求对上。0 保留不用。</summary>
    private byte NextServiceId() => _serviceId = (byte)(_serviceId == 255 ? 1 : _serviceId + 1);

    private static IPAddress ResolveAddress(string host)
        => IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(host).FirstOrDefault(
                static a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new RungException($"无法解析主机名 \"{host}\"");

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

/// <summary>FINS 驱动工厂。</summary>
public sealed class FinsDriverFactory : IDeviceDriverFactory
{
    /// <inheritdoc/>
    public string Protocol => "omron-fins";

    /// <inheritdoc/>
    public string AddressSyntaxHint =>
        "D100 / D100.05（DM 区，位号 0-15）· CIO200 / W10.03 / H5 / A50 · 全部十进制"
        + " · 32 位值通常需要把字节序配成 CDAB（低字在前、字内大端）";

    /// <inheritdoc/>
    public IDeviceDriver Create(DeviceOptions options) => new FinsDriver(options);

    /// <inheritdoc/>
    public IReadPlan CompileOffline(DeviceOptions options, IReadOnlyList<TagDef> tags)
    {
        ArgumentNullException.ThrowIfNull(options);

        var finsOptions = FinsDriverOptions.FromDeviceOptions(options);

        return FinsReadPlanner.Create(
            tags, new FinsReadPlannerOptions { MaxGapWords = finsOptions.MaxGapWords });
    }
}
