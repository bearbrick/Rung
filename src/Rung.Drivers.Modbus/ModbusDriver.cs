using System.Globalization;
using System.Net;
using System.Net.Sockets;
using FluentModbus;
using Rung.Abstractions;

namespace Rung.Drivers.Modbus;

/// <summary>Modbus TCP 驱动的连接参数。</summary>
public sealed record ModbusDriverOptions
{
    /// <summary>默认从站号。点位地址可用 <c>3:HR100</c> 覆盖。</summary>
    public byte DefaultUnitId { get; init; } = 1;

    /// <summary>批量合并时允许跨越的最大空洞。</summary>
    public int MaxGapRegisters { get; init; } = 16;

    /// <summary>从设备配置里解析 Modbus 特有参数。</summary>
    public static ModbusDriverOptions FromDeviceOptions(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ModbusDriverOptions
        {
            DefaultUnitId = (byte)options.GetInt32("unitId", 1),
            MaxGapRegisters = options.GetInt32("maxGapRegisters", 16),
        };
    }
}

/// <summary>
/// Modbus TCP 驱动。
/// <para>
/// 与 S7 驱动不同，这里<b>不自己实现报文</b>：FluentModbus 是 MIT、活跃维护、
/// 原生异步，而且 Modbus 协议本身简单到不值得自己维护一份。
/// 驱动只负责地址语义、批量合并和值解释——那才是各家网关真正拉开差距的地方。
/// </para>
/// <para>
/// 和所有驱动一样，<b>不是线程安全的</b>：调用方负责把同一设备的读写串行化。
/// </para>
/// </summary>
public sealed class ModbusDriver : IDeviceDriver
{
    private readonly DeviceOptions _deviceOptions;
    private readonly ModbusDriverOptions _modbusOptions;

    private ModbusTcpClient? _client;
    private bool _disposed;

    /// <summary>创建一个驱动实例。此时不发起任何网络 IO。</summary>
    public ModbusDriver(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _deviceOptions = options;
        _modbusOptions = ModbusDriverOptions.FromDeviceOptions(options);
        DeviceId = options.DeviceId;
    }

    /// <inheritdoc/>
    public string DeviceId { get; }

    /// <inheritdoc/>
    public DriverState State { get; private set; } = DriverState.Disconnected;

    /// <summary>
    /// Modbus 不协商 PDU，上限是协议固定的。这里报单次可读的最大字节数
    /// （125 个寄存器 × 2），让上层的诊断展示有个统一口径。
    /// </summary>
    public int MaxPduLength => State == DriverState.Connected ? ModbusLimits.MaxReadRegisters * 2 : 0;

    /// <inheritdoc/>
    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Disconnect();
        State = DriverState.Connecting;

        try
        {
            var port = _deviceOptions.Port > 0 ? _deviceOptions.Port : 502;
            var client = new ModbusTcpClient
            {
                ConnectTimeout = _deviceOptions.TimeoutMs,
                ReadTimeout = _deviceOptions.TimeoutMs,
                WriteTimeout = _deviceOptions.TimeoutMs,
            };

            var endpoint = new IPEndPoint(ResolveAddress(_deviceOptions.Host), port);

            // FluentModbus 只提供同步 Connect。它带超时，且连接是低频动作，
            // 阻塞采集工作者自己的那条循环可以接受
            client.Connect(endpoint, ModbusEndianness.BigEndian);

            _client = client;
            State = DriverState.Connected;
        }
        catch
        {
            State = DriverState.Faulted;
            Disconnect();
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        Disconnect();
        State = DriverState.Disconnected;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadPlan CreateReadPlan(IReadOnlyList<TagDef> tags)
    {
        EnsureConnected();

        return ModbusReadPlanner.Create(tags, new ModbusReadPlannerOptions
        {
            DefaultUnitId = _modbusOptions.DefaultUnitId,
            MaxGapRegisters = _modbusOptions.MaxGapRegisters,
        });
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

        if (plan is not ModbusReadPlan modbusPlan)
        {
            throw new ArgumentException($"读取计划必须由 {nameof(ModbusDriver)} 编译", nameof(plan));
        }

        var timestamp = DateTime.UtcNow;

        foreach (var issue in modbusPlan.Issues)
        {
            destination[issue.TagIndex] = TagValue.Bad(
                modbusPlan.Tags[issue.TagIndex].DataType, TagQuality.ConfigError, timestamp);
        }

        var goodCount = 0;

        for (var requestIndex = 0; requestIndex < modbusPlan.Requests.Count; requestIndex++)
        {
            var request = modbusPlan.Requests[requestIndex];
            var payload = await ReadBlockAsync(request, cancellationToken).ConfigureAwait(false);

            goodCount += Decode(modbusPlan, requestIndex, payload.Span, destination, timestamp);
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

        var client = _client!;
        var address = ModbusAddressParser.Parse(tag.Address, _modbusOptions.DefaultUnitId);

        if (!address.Area.IsWritable())
        {
            throw new RungException(
                $"点位 {tag.Name} 位于 {address.Area}，该区在 Modbus 协议上就是只读的");
        }

        try
        {
            if (address.Area == ModbusArea.Coil)
            {
                await client.WriteSingleCoilAsync(
                    address.UnitId, address.Offset, value.AsBool(), cancellationToken).ConfigureAwait(false);

                return;
            }

            var payload = EncodeRegisters(tag, value, address);

            await client.WriteMultipleRegistersAsync(
                address.UnitId, address.Offset, payload, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            State = DriverState.Faulted;
            throw;
        }
    }

    /// <summary>
    /// 把待写入的值编码成寄存器字节流。
    /// <para>
    /// 寄存器里的单个位是个麻烦：Modbus 没有"写寄存器某一位"的功能码，
    /// 只能读改写。这里明确拒绝而不是悄悄做读改写——
    /// 读改写在并发场景下会丢掉别人刚写进去的位，产线上这种丢失极难排查。
    /// </para>
    /// </summary>
    private static byte[] EncodeRegisters(TagDef tag, TagValue value, ModbusAddress address)
    {
        if (address.HasBit)
        {
            throw new RungException(
                $"点位 {tag.Name} 指向寄存器内的单个位。Modbus 没有对应的写功能码，"
                + "读改写会在并发下丢位，请改用线圈区，或整寄存器写入");
        }

        var byteLength = (tag.ByteLength + 1) / 2 * 2;
        var payload = new byte[byteLength];

        if (tag.DataType == TagDataType.Bool)
        {
            payload[1] = value.AsBool() ? (byte)1 : (byte)0;
            return payload;
        }

        TagValueCodec.EncodeScalar(payload, tag, value);
        return payload;
    }

    private async ValueTask<Memory<byte>> ReadBlockAsync(
        ModbusReadRequest request,
        CancellationToken cancellationToken)
    {
        var client = _client!;

        try
        {
            return request.Area switch
            {
                ModbusArea.HoldingRegister => await client
                    .ReadHoldingRegistersAsync(request.UnitId, request.Start, request.Count, cancellationToken)
                    .ConfigureAwait(false),
                ModbusArea.InputRegister => await client
                    .ReadInputRegistersAsync(request.UnitId, request.Start, request.Count, cancellationToken)
                    .ConfigureAwait(false),
                ModbusArea.Coil => await client
                    .ReadCoilsAsync(request.UnitId, request.Start, request.Count, cancellationToken)
                    .ConfigureAwait(false),
                _ => await client
                    .ReadDiscreteInputsAsync(request.UnitId, request.Start, request.Count, cancellationToken)
                    .ConfigureAwait(false),
            };
        }
        catch
        {
            // 与 S7 驱动同样的判断：一次交换失败之后链路就不可信，
            // 交给上层重连状态机，驱动自己不重试
            State = DriverState.Faulted;
            throw;
        }
    }

    private static int Decode(
        ModbusReadPlan plan,
        int requestIndex,
        ReadOnlySpan<byte> payload,
        TagValue[] destination,
        DateTime timestamp)
    {
        var isBitArea = plan.Requests[requestIndex].Area.IsBitArea();
        var goodCount = 0;

        foreach (var tagIndex in plan.TagIndexesByRequest[requestIndex])
        {
            var tag = plan.Tags[tagIndex];
            var location = plan.Locations[tagIndex];

            if (isBitArea)
            {
                // 位区的响应是打包的：第 n 位在第 n/8 个字节的第 n%8 位
                var byteIndex = location.Offset / 8;
                if (byteIndex >= payload.Length)
                {
                    destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                    continue;
                }

                var bit = (payload[byteIndex] & (1 << (location.Offset % 8))) != 0;
                destination[tagIndex] = TagValue.FromBool(bit, timestamp);
                goodCount++;
                continue;
            }

            var byteLength = Math.Max(2, (tag.ByteLength + 1) / 2 * 2);
            if (location.Offset + byteLength > payload.Length)
            {
                destination[tagIndex] = TagValue.Bad(tag.DataType, TagQuality.DeviceError, timestamp);
                continue;
            }

            var slice = payload.Slice(location.Offset, byteLength);

            destination[tagIndex] = tag.DataType switch
            {
                // 寄存器里的布尔：指定了位就取那一位，否则整个寄存器非零为真
                TagDataType.Bool => TagValue.FromBool(
                    DecodeRegisterBool(slice, location.BitOffset, location.HasBit), timestamp),
                TagDataType.Bytes => TagValue.FromBytes(slice[..tag.Length].ToArray(), timestamp),
                _ => TagValueCodec.DecodeScalar(slice, tag, timestamp),
            };

            goodCount++;
        }

        return goodCount;
    }

    /// <summary>
    /// 解释寄存器里的布尔值。
    /// <para>
    /// 地址写了位（<c>HR100.3</c>）就取那一位；没写就按"整个寄存器非零为真"。
    /// 后者是现场常见约定：很多设备用一整个寄存器表示一个状态位。
    /// </para>
    /// </summary>
    private static bool DecodeRegisterBool(ReadOnlySpan<byte> register, byte bitOffset, bool hasBit)
    {
        // 寄存器在 Modbus 线上是大端：高字节在前，位 0-7 落在低字节
        var word = (ushort)((register[0] << 8) | register[1]);

        return hasBit ? (word & (1 << bitOffset)) != 0 : word != 0;
    }

    private static IPAddress ResolveAddress(string host)
        => IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(host).FirstOrDefault(
                static a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new RungException($"无法解析主机名 \"{host}\"");

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != DriverState.Connected)
        {
            throw new RungException(
                string.Create(CultureInfo.InvariantCulture,
                    $"设备 {DeviceId} 当前状态为 {State}，无法执行操作"));
        }
    }

    private void Disconnect()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            _client.Disconnect();
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            // 关闭失败没有补救手段
        }

        _client = null;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Disconnect();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Modbus TCP 驱动工厂。</summary>
public sealed class ModbusDriverFactory : IDeviceDriverFactory
{
    /// <inheritdoc/>
    public string Protocol => "modbus-tcp";

    /// <inheritdoc/>
    public string AddressSyntaxHint =>
        "HR100 / IR10 / CO5 / DI7（0 基，推荐）· 40001 / 30001（经典 1 基）· "
        + "HR100.3（寄存器内第 3 位）· 3:HR100（指定从站号）";

    /// <inheritdoc/>
    public IDeviceDriver Create(DeviceOptions options) => new ModbusDriver(options);

    /// <inheritdoc/>
    public IReadPlan CompileOffline(DeviceOptions options, IReadOnlyList<TagDef> tags)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Modbus 不协商任何东西，上限是协议固定的，因此离线编译与在线完全一致
        var modbusOptions = ModbusDriverOptions.FromDeviceOptions(options);

        return ModbusReadPlanner.Create(tags, new ModbusReadPlannerOptions
        {
            DefaultUnitId = modbusOptions.DefaultUnitId,
            MaxGapRegisters = modbusOptions.MaxGapRegisters,
        });
    }
}
