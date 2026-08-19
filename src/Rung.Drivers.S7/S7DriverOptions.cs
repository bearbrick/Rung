using Rung.Abstractions;
using Rung.Protocols.S7;

namespace Rung.Drivers.S7;

/// <summary>S7 驱动的连接参数，从 <see cref="DeviceOptions.Extra"/> 中解析。</summary>
public sealed record S7DriverOptions
{
    /// <summary>机架号。</summary>
    public byte Rack { get; init; }

    /// <summary>槽号。S7-300/400 通常是 2，S7-1200/1500 通常是 1。</summary>
    public byte Slot { get; init; } = 1;

    /// <summary>连接类型。</summary>
    public S7ConnectionType ConnectionType { get; init; } = S7ConnectionType.Pg;

    /// <summary>请求协商的 PDU 长度。设备会给出实际值，通常砍到 240 或 480。</summary>
    public ushort RequestedPduLength { get; init; } = S7Protocol.RequestedPduLength;

    /// <summary>批量合并时允许跨越的最大空洞字节数。</summary>
    public int MaxGapBytes { get; init; } = 8;

    /// <summary>
    /// 从设备配置中解析 S7 特有的参数。
    /// <para>
    /// 机架号和槽号配错是新设备接入时最高频的问题，因此这里给出的默认值
    /// 对准最常见的 S7-1200/1500（0/1），而不是任意值。
    /// </para>
    /// </summary>
    public static S7DriverOptions FromDeviceOptions(DeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new S7DriverOptions
        {
            Rack = (byte)options.GetInt32("rack", 0),
            Slot = (byte)options.GetInt32("slot", 1),
            ConnectionType = (S7ConnectionType)options.GetInt32(
                "connectionType", (int)S7ConnectionType.Pg),
            RequestedPduLength = (ushort)options.GetInt32(
                "requestedPduLength", S7Protocol.RequestedPduLength),
            MaxGapBytes = options.GetInt32("maxGapBytes", 8),
        };
    }
}
