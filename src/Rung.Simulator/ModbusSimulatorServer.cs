using System.Buffers.Binary;
using System.Net;
using FluentModbus;

namespace Rung.Simulator;

/// <summary>一台模拟的 Modbus TCP 从站。</summary>
public sealed record SimulatedModbusDeviceConfig
{
    /// <summary>设备名，用于日志与展示。</summary>
    public string Name { get; init; } = "modbus-sim";

    /// <summary>监听端口。</summary>
    public int Port { get; init; } = 502;

    /// <summary>要响应的从站号。</summary>
    public IReadOnlyList<byte> UnitIds { get; init; } = [1];

    /// <summary>信号列表。地址用 Modbus 语法，如 <c>HR0</c>、<c>CO5</c>。</summary>
    public IReadOnlyList<SignalConfig> Signals { get; init; } = [];
}

/// <summary>
/// Modbus 从站模拟器。
/// <para>
/// 与 S7 模拟器不同，这里<b>直接用 FluentModbus 的服务端</b>而不是另写一份报文实现。
/// 独立实现的价值在于交叉验证协议编解码，而 Modbus 的编解码本来就不是 Rung 写的——
/// 两边同源在这里不损失任何东西，反而省下一份没人维护的代码。
/// </para>
/// <para>信号生成器与 S7 模拟器共用，因此数据同样是活的。</para>
/// </summary>
public sealed class ModbusSimulatorServer : IAsyncDisposable
{
    private readonly ModbusTcpServer _server = new();
    private readonly List<(byte UnitId, char Area, ushort Offset, string Type, SignalGenerator Generator)> _signals = [];
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly Timer _refresh;

    private bool _disposed;

    /// <summary>启动一台模拟从站。</summary>
    public ModbusSimulatorServer(SimulatedModbusDeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Name = config.Name;
        Port = config.Port;

        _server.Start(new IPEndPoint(IPAddress.Loopback, config.Port));
        foreach (var unitId in config.UnitIds)
        {
            _server.AddUnit(unitId);
        }

        foreach (var signal in config.Signals)
        {
            var (unitId, area, offset) = ParseAddress(signal.Address, config.UnitIds[0]);
            _signals.Add((unitId, area, offset, signal.Type, SignalGenerator.Create(signal)));
        }

        Refresh(null);

        // Modbus 服务端是被动的：客户端来读时并不会通知我们，
        // 所以用定时器把信号推进到当前时刻
        _refresh = new Timer(Refresh, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>设备名。</summary>
    public string Name { get; }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    private void Refresh(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _startedUtc;

        lock (_server.Lock)
        {
            foreach (var (unitId, area, offset, type, generator) in _signals)
            {
                if (!generator.Overwrites && state is not null)
                {
                    continue;
                }

                var value = generator.ValueAt(elapsed);

                if (area is 'C' or 'D')
                {
                    var buffer = area == 'C'
                        ? _server.GetCoilBuffer(unitId)
                        : _server.GetDiscreteInputBuffer(unitId);

                    // 线圈缓冲区是按位打包的，索引是字节序号而不是线圈号
                    var mask = (byte)(1 << (offset % 8));
                    buffer[offset / 8] = value != 0
                        ? (byte)(buffer[offset / 8] | mask)
                        : (byte)(buffer[offset / 8] & ~mask);

                    continue;
                }

                var registers = area == 'H'
                    ? _server.GetHoldingRegisterBuffer(unitId)
                    : _server.GetInputRegisterBuffer(unitId);

                WriteBigEndian(registers[(offset * 2)..], type, value);
            }
        }
    }

    /// <summary>
    /// 寄存器缓冲区里存的就是线上字节，因此一律按大端写入。
    /// 用 <c>GetHoldingRegisters()</c> 的 <c>Span&lt;short&gt;</c> 会写成主机字节序，
    /// 在小端机器上等于给自己造了个字节序错觉。
    /// </summary>
    private static void WriteBigEndian(Span<byte> destination, string type, double value)
    {
        switch (type.ToLowerInvariant())
        {
            case "int16":
                BinaryPrimitives.WriteInt16BigEndian(destination, (short)Math.Round(value));
                break;
            case "uint16":
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)Math.Round(value));
                break;
            case "int32":
                BinaryPrimitives.WriteInt32BigEndian(destination, (int)Math.Round(value));
                break;
            case "uint32":
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)Math.Round(value));
                break;
            case "float32":
                BinaryPrimitives.WriteSingleBigEndian(destination, (float)value);
                break;
            case "float64":
                BinaryPrimitives.WriteDoubleBigEndian(destination, value);
                break;
            default:
                throw new ArgumentException($"Modbus 模拟器不支持数据类型 \"{type}\"", nameof(type));
        }
    }

    /// <summary>解析 <c>[unit:]{HR|IR|CO|DI}{offset}</c>。模拟器只需要这一种写法。</summary>
    private static (byte UnitId, char Area, ushort Offset) ParseAddress(string address, byte defaultUnitId)
    {
        var text = address.Trim().ToUpperInvariant();
        var unitId = defaultUnitId;

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            unitId = byte.Parse(text[..colon], System.Globalization.CultureInfo.InvariantCulture);
            text = text[(colon + 1)..];
        }

        var area = text[..2] switch
        {
            "HR" => 'H',
            "IR" => 'I',
            "CO" => 'C',
            "DI" => 'D',
            _ => throw new ArgumentException($"Modbus 模拟器只认 HR/IR/CO/DI 前缀，实际是 \"{address}\"", nameof(address)),
        };

        return (unitId, area,
            ushort.Parse(text[2..], System.Globalization.CultureInfo.InvariantCulture));
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

        _server.Stop();
        _server.Dispose();
    }
}
