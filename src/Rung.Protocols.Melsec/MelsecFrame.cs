using System.Buffers.Binary;
using Rung.Abstractions;

namespace Rung.Protocols.Melsec;

/// <summary>MC 3E 二进制帧的协议常量与容量。</summary>
public static class MelsecProtocol
{
    /// <summary>MELSEC 以太网模块的常用端口。</summary>
    public const int DefaultPort = 6000;

    /// <summary>请求帧固定头部长度：副头部到请求数据长度字段为止。</summary>
    public const int RequestHeaderLength = 9;

    /// <summary>响应帧固定头部长度：副头部到响应数据长度字段为止。</summary>
    public const int ResponseHeaderLength = 9;

    /// <summary>批量读取的请求帧总长度。</summary>
    public const int BatchReadRequestLength = 21;

    /// <summary>成批读取，字单位一次最多 960 点。</summary>
    public const int MaxWordPoints = 960;

    /// <summary>成批读取，位单位一次最多 7168 点。</summary>
    public const int MaxBitPoints = 7168;

    /// <summary>成批写入，字单位一次最多 960 点。</summary>
    public const int MaxWriteWordPoints = 960;

    internal const ushort RequestSubheader = 0x0050;
    internal const ushort ResponseSubheader = 0x00D0;
    internal const ushort BatchReadCommand = 0x0401;
    internal const ushort BatchWriteCommand = 0x1401;
    internal const ushort WordSubcommand = 0x0000;
    internal const ushort BitSubcommand = 0x0001;

    /// <summary>该软元件一次读取的点数上限。</summary>
    public static int MaxPoints(MelsecDevice device)
        => device.IsBitDevice() ? MaxBitPoints : MaxWordPoints;
}

/// <summary>
/// MC 3E 二进制帧的组包与解析。
/// <para>
/// 全部是纯函数：写入调用方给的 <see cref="Span{T}"/>，返回写入字节数，
/// 不分配、不持有状态、不碰任何 IO。和 S7 那边一样，
/// 这样每个字节都能被单元测试逐一断言。
/// </para>
/// <para>
/// <b>MELSEC 全程小端</b>，与 S7 的大端正好相反。移植时最容易出错的就是这里——
/// 字节序搞反不会报错，只会读出一个看着像那么回事的数。
/// </para>
/// </summary>
public static class MelsecFrame
{
    /// <summary>
    /// 组建成批读取请求。
    /// </summary>
    /// <param name="destination">目标缓冲区，至少 <see cref="MelsecProtocol.BatchReadRequestLength"/> 字节。</param>
    /// <param name="address">起始软元件。</param>
    /// <param name="points">点数：字软元件按字计，位软元件按位计。</param>
    /// <param name="monitoringTimerMs">CPU 侧的监视定时器，毫秒。0 表示无限等待。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteBatchReadRequest(
        Span<byte> destination,
        MelsecAddress address,
        int points,
        int monitoringTimerMs = 4000)
    {
        EnsureCapacity(destination, MelsecProtocol.BatchReadRequestLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(points, MelsecProtocol.MaxPoints(address.Device));

        // 请求数据长度从监视定时器算起，到帧尾
        WriteHeader(destination, requestDataLength: 12, monitoringTimerMs);

        BinaryPrimitives.WriteUInt16LittleEndian(destination[11..], MelsecProtocol.BatchReadCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[13..],
            address.IsBit ? MelsecProtocol.BitSubcommand : MelsecProtocol.WordSubcommand);

        WriteDeviceSpec(destination[15..], address, points);

        return MelsecProtocol.BatchReadRequestLength;
    }

    /// <summary>组建成批写入请求的总长度。</summary>
    public static int GetBatchWriteRequestLength(MelsecAddress address, int points)
        => 21 + (address.IsBit ? (points + 1) / 2 : points * 2);

    /// <summary>
    /// 组建成批写入请求。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="address">起始软元件。</param>
    /// <param name="payload">
    /// 要写入的数据。字软元件为小端字序列；位软元件为每点一字节的 0/1，
    /// 由本方法压缩成半字节。
    /// </param>
    /// <param name="monitoringTimerMs">监视定时器，毫秒。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteBatchWriteRequest(
        Span<byte> destination,
        MelsecAddress address,
        ReadOnlySpan<byte> payload,
        int monitoringTimerMs = 4000)
    {
        var points = address.IsBit ? payload.Length : payload.Length / 2;
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 1);

        var dataBytes = address.IsBit ? (points + 1) / 2 : points * 2;
        var total = 21 + dataBytes;
        EnsureCapacity(destination, total);

        WriteHeader(destination, requestDataLength: (ushort)(12 + dataBytes), monitoringTimerMs);

        BinaryPrimitives.WriteUInt16LittleEndian(destination[11..], MelsecProtocol.BatchWriteCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[13..],
            address.IsBit ? MelsecProtocol.BitSubcommand : MelsecProtocol.WordSubcommand);

        WriteDeviceSpec(destination[15..], address, points);

        if (!address.IsBit)
        {
            payload.CopyTo(destination[21..]);
            return total;
        }

        // 位单位下每字节装两个点：高半字节是前一个点
        destination[21..total].Clear();
        for (var i = 0; i < points; i++)
        {
            if (payload[i] != 0)
            {
                destination[21 + (i / 2)] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            }
        }

        return total;
    }

    /// <summary>
    /// 从响应头读出整帧长度。
    /// <para>
    /// 传输层靠它决定还要再收多少字节：先固定读 9 字节头，
    /// 拿到响应数据长度后再把剩下的收齐，避免半包/粘包。
    /// </para>
    /// </summary>
    /// <param name="header">至少 9 字节的响应头。</param>
    /// <returns>整帧字节数，含头部。</returns>
    public static int ReadFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < MelsecProtocol.ResponseHeaderLength)
        {
            throw new ProtocolException(
                $"MC 响应头至少需要 {MelsecProtocol.ResponseHeaderLength} 字节，实际 {header.Length} 字节");
        }

        var subheader = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (subheader != MelsecProtocol.ResponseSubheader)
        {
            throw new ProtocolException(
                $"MC 响应副头部应为 0x{MelsecProtocol.ResponseSubheader:X4}，实际 0x{subheader:X4}");
        }

        var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(header[7..]);
        if (dataLength < 2)
        {
            throw new ProtocolException($"MC 响应数据长度 {dataLength} 小于结束代码本身的长度");
        }

        return MelsecProtocol.ResponseHeaderLength + dataLength;
    }

    /// <summary>
    /// 校验响应并取出数据段。
    /// </summary>
    /// <exception cref="ProtocolException">帧非法，或 CPU 返回了非零结束代码。</exception>
    public static ReadOnlySpan<byte> ReadResponseData(ReadOnlySpan<byte> frame)
    {
        var expected = ReadFrameLength(frame);
        if (frame.Length != expected)
        {
            throw new ProtocolException($"MC 响应声明帧长 {expected}，实际收到 {frame.Length} 字节");
        }

        var endCode = BinaryPrimitives.ReadUInt16LittleEndian(frame[9..]);
        if (endCode != 0)
        {
            throw new ProtocolException(
                $"MELSEC CPU 返回错误码 0x{endCode:X4}：{DescribeEndCode(endCode)}");
        }

        return frame[11..];
    }

    /// <summary>把位单位响应里的半字节展开成每点一字节。</summary>
    public static void UnpackBits(ReadOnlySpan<byte> packed, Span<byte> destination, int points)
    {
        for (var i = 0; i < points; i++)
        {
            var b = packed[i / 2];
            destination[i] = (byte)((i % 2 == 0 ? b >> 4 : b & 0x0F) != 0 ? 1 : 0);
        }
    }

    /// <summary>常见结束代码的人话解释。现场最常撞上的就这几个。</summary>
    private static string DescribeEndCode(ushort endCode) => endCode switch
    {
        0xC051 => "读写点数超出允许范围",
        0xC056 => "起始软元件号加点数超出软元件范围",
        0xC059 => "指令或子指令有误",
        0xC05C => "请求内容有误，通常是软元件代码写错",
        0xC05F => "该请求无法对目标 CPU 执行",
        0xC060 => "请求内容有误，通常是位软元件的数据指定不对",
        0xC061 => "请求数据长度与实际内容不符",
        0x4031 => "指定的软元件号超出范围",
        _ => "含义见三菱《MELSEC 通信协议参考手册》的结束代码一览",
    };

    private static void WriteHeader(Span<byte> destination, ushort requestDataLength, int monitoringTimerMs)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, MelsecProtocol.RequestSubheader);

        destination[2] = 0x00;  // 网络编号：本站
        destination[3] = 0xFF;  // PC 编号：本站
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], 0x03FF); // 请求目标模块 I/O 编号
        destination[6] = 0x00;  // 请求目标模块站号

        BinaryPrimitives.WriteUInt16LittleEndian(destination[7..], requestDataLength);

        // 监视定时器以 250 ms 为单位。0 表示无限等待——
        // 网关侧本来就有自己的超时，让 CPU 也无限等只会让故障更难定位
        var ticks = Math.Clamp(monitoringTimerMs / 250, 1, ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[9..], (ushort)ticks);
    }

    private static void WriteDeviceSpec(Span<byte> destination, MelsecAddress address, int points)
    {
        // 起始软元件号占 3 字节，小端
        destination[0] = (byte)address.Number;
        destination[1] = (byte)(address.Number >> 8);
        destination[2] = (byte)(address.Number >> 16);
        destination[3] = (byte)address.Device;

        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)points);
    }

    private static void EnsureCapacity(Span<byte> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"缓冲区不足：需要 {required} 字节，实际 {destination.Length} 字节", nameof(destination));
        }
    }
}
