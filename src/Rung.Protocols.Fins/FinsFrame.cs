using System.Buffers.Binary;
using Rung.Abstractions;

namespace Rung.Protocols.Fins;

/// <summary>FINS 协议常量与容量。</summary>
public static class FinsProtocol
{
    /// <summary>FINS/UDP 的标准端口。</summary>
    public const int DefaultPort = 9600;

    /// <summary>FINS 头部长度：ICF 到 SID 共 10 字节。</summary>
    public const int HeaderLength = 10;

    /// <summary>请求帧固定长度：头部 + MRC/SRC + 存储区 + 地址 + 点数。</summary>
    public const int ReadRequestLength = 18;

    /// <summary>响应帧最小长度：头部 + MRC/SRC + 结束码。</summary>
    public const int ResponseHeaderLength = 14;

    /// <summary>
    /// 单次读取的最大字数。
    /// <para>
    /// FINS/UDP 的数据段受 UDP 报文长度限制，各机型上限不一。
    /// 取 990 是留了余量的保守值——超限的后果是 CPU 直接拒绝，
    /// 而保守的代价只是多一次往返。
    /// </para>
    /// </summary>
    public const int MaxWords = 990;

    internal const byte CommandIcf = 0x80;
    internal const byte ResponseIcf = 0xC0;
    internal const byte GatewayCount = 0x02;
    internal const byte MemoryAreaMrc = 0x01;
    internal const byte ReadSrc = 0x01;
    internal const byte WriteSrc = 0x02;
}

/// <summary>FINS 通信节点。</summary>
/// <param name="Network">网络号，同网段填 0。</param>
/// <param name="Node">节点号。以太网机型通常是 IP 的最后一段。</param>
/// <param name="Unit">单元号，CPU 填 0。</param>
public readonly record struct FinsNode(byte Network, byte Node, byte Unit = 0);

/// <summary>
/// FINS 帧的组包与解析。
/// <para>
/// 与 MELSEC 相反，<b>FINS 全程大端</b>。两种协议的驱动放在一个仓库里，
/// 这类差异是移植时最容易带错的东西，所以每处字节序都显式写明。
/// </para>
/// </summary>
public static class FinsFrame
{
    /// <summary>组建读存储区请求。</summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="source">本机节点。</param>
    /// <param name="target">PLC 节点。</param>
    /// <param name="serviceId">服务号，用于把响应和请求对上。</param>
    /// <param name="address">起始地址。</param>
    /// <param name="count">点数：按字读时为字数，按位读时为位数。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteReadRequest(
        Span<byte> destination,
        FinsNode source,
        FinsNode target,
        byte serviceId,
        FinsAddress address,
        int count)
    {
        EnsureCapacity(destination, FinsProtocol.ReadRequestLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        WriteHeader(destination, source, target, serviceId);

        destination[10] = FinsProtocol.MemoryAreaMrc;
        destination[11] = FinsProtocol.ReadSrc;

        WriteAddress(destination[12..], address);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], (ushort)count);

        return FinsProtocol.ReadRequestLength;
    }

    /// <summary>组建写存储区请求的总长度。</summary>
    public static int GetWriteRequestLength(FinsAddress address, int payloadBytes)
        => FinsProtocol.ReadRequestLength + (address.HasBit ? 1 : payloadBytes);

    /// <summary>
    /// 组建写存储区请求。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="source">本机节点。</param>
    /// <param name="target">PLC 节点。</param>
    /// <param name="serviceId">服务号。</param>
    /// <param name="address">目标地址。</param>
    /// <param name="payload">
    /// 按字写时为大端字序列；按位写时长度为 1，值 0 或 1。
    /// </param>
    public static int WriteWriteRequest(
        Span<byte> destination,
        FinsNode source,
        FinsNode target,
        byte serviceId,
        FinsAddress address,
        ReadOnlySpan<byte> payload)
    {
        var count = address.HasBit ? 1 : payload.Length / 2;
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var dataBytes = address.HasBit ? 1 : payload.Length;
        var total = FinsProtocol.ReadRequestLength + dataBytes;
        EnsureCapacity(destination, total);

        WriteHeader(destination, source, target, serviceId);

        destination[10] = FinsProtocol.MemoryAreaMrc;
        destination[11] = FinsProtocol.WriteSrc;

        WriteAddress(destination[12..], address);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], (ushort)count);

        payload[..dataBytes].CopyTo(destination[FinsProtocol.ReadRequestLength..]);

        return total;
    }

    /// <summary>
    /// 校验响应并取出数据段。
    /// </summary>
    /// <param name="frame">整个响应帧。</param>
    /// <param name="expectedServiceId">
    /// 期望的服务号。UDP 不保证顺序，上一次超时的响应可能迟到——
    /// 不核对服务号就会把它当成本次的结果，读出一个属于上一轮的旧值。
    /// </param>
    public static ReadOnlySpan<byte> ReadResponseData(ReadOnlySpan<byte> frame, byte expectedServiceId)
    {
        if (frame.Length < FinsProtocol.ResponseHeaderLength)
        {
            throw new ProtocolException(
                $"FINS 响应至少需要 {FinsProtocol.ResponseHeaderLength} 字节，实际 {frame.Length} 字节");
        }

        if (frame[0] != FinsProtocol.ResponseIcf)
        {
            throw new ProtocolException($"FINS 响应的 ICF 应为 0xC0，实际 0x{frame[0]:X2}");
        }

        if (frame[9] != expectedServiceId)
        {
            throw new ProtocolException(
                $"FINS 服务号不匹配：期望 {expectedServiceId}，收到 {frame[9]}。"
                + "多半是上一次超时的响应迟到了");
        }

        var endCode = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        if (endCode != 0)
        {
            throw new ProtocolException(
                $"欧姆龙 CPU 返回错误码 0x{endCode:X4}：{DescribeEndCode(endCode)}");
        }

        return frame[14..];
    }

    /// <summary>常见结束代码的人话解释。</summary>
    private static string DescribeEndCode(ushort endCode) => endCode switch
    {
        0x0401 => "该 CPU 不支持这个指令",
        0x1001 => "指令过长",
        0x1002 => "指令过短",
        0x1003 => "指定的数据数与实际不符",
        0x1101 => "指定的存储区不存在",
        0x1103 => "起始地址超出存储区范围",
        0x110B => "响应过长，请减少一次读取的点数",
        0x110C => "参数有误",
        0x2002 => "该存储区处于只读或保护状态",
        0x2103 => "写入被禁止，可能处于运行模式保护",
        _ => "含义见欧姆龙《FINS 通信手册》的结束代码一览",
    };

    private static void WriteHeader(
        Span<byte> destination, FinsNode source, FinsNode target, byte serviceId)
    {
        destination[0] = FinsProtocol.CommandIcf;      // 需要响应的指令
        destination[1] = 0x00;                          // 保留
        destination[2] = FinsProtocol.GatewayCount;     // 网关计数
        destination[3] = target.Network;
        destination[4] = target.Node;
        destination[5] = target.Unit;
        destination[6] = source.Network;
        destination[7] = source.Node;
        destination[8] = source.Unit;
        destination[9] = serviceId;
    }

    private static void WriteAddress(Span<byte> destination, FinsAddress address)
    {
        destination[0] = address.HasBit ? address.Area.BitCode() : address.Area.WordCode();

        // 字地址 2 字节大端，之后 1 字节位号。按字访问时位号必须是 0
        BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)address.Word);
        destination[3] = address.HasBit ? address.Bit : (byte)0;
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
