namespace Rung.Protocols.S7;

/// <summary>设备针对单个数据项返回的状态码。</summary>
public enum S7ReturnCode : byte
{
    /// <summary>保留值；出现在写请求的数据项里。</summary>
    Reserved = 0x00,

    /// <summary>硬件故障。</summary>
    HardwareFault = 0x01,

    /// <summary>不允许访问该对象。</summary>
    AccessDenied = 0x03,

    /// <summary>地址超出范围。通常是 DB 长度不够或偏移写错了。</summary>
    InvalidAddress = 0x05,

    /// <summary>不支持的数据类型。</summary>
    DataTypeNotSupported = 0x06,

    /// <summary>数据类型不一致。</summary>
    DataTypeInconsistent = 0x07,

    /// <summary>对象不存在。最常见的原因是 DB 号写错，或该 DB 未下载到 PLC。</summary>
    ObjectDoesNotExist = 0x0A,

    /// <summary>成功。</summary>
    Success = 0xFF,
}

/// <summary>
/// 响应数据段里的传输尺寸。
/// <para>
/// <b>注意这是一套和请求侧完全不同的编码</b>，而且长度字段的单位随它变化：
/// 0x03/0x04/0x05 的长度以<b>位</b>计，0x06/0x07/0x09 的长度以<b>字节</b>计。
/// 这是 S7 解析最经典的错误来源——搞混了就会读出长度差 8 倍的数据。
/// </para>
/// </summary>
public enum S7DataTransportSize : byte
{
    /// <summary>空。</summary>
    Null = 0x00,

    /// <summary>位，长度以位计。</summary>
    Bit = 0x03,

    /// <summary>字节/字，长度以位计。</summary>
    Byte = 0x04,

    /// <summary>整数，长度以位计。</summary>
    Int = 0x05,

    /// <summary>双整数，长度以字节计。</summary>
    DInt = 0x06,

    /// <summary>实数，长度以字节计。</summary>
    Real = 0x07,

    /// <summary>八位组串，长度以字节计。</summary>
    OctetString = 0x09,
}

/// <summary>COTP 连接请求中的连接类型，编码进目的 TSAP 的高字节。</summary>
public enum S7ConnectionType : byte
{
    /// <summary>编程器连接。默认值，权限最高，绝大多数场景用它。</summary>
    Pg = 0x01,

    /// <summary>操作面板连接。</summary>
    Op = 0x02,

    /// <summary>S7 基本通讯连接。</summary>
    Basic = 0x03,
}

/// <summary>S7comm 的协议常量与容量计算。</summary>
public static class S7Protocol
{
    /// <summary>S7comm over ISO-TCP 的标准端口。</summary>
    public const int DefaultPort = 102;

    /// <summary>TPKT(4) + COTP 数据传输头(3)。所有 S7 报文都以这 7 个字节开头。</summary>
    public const int IsoHeaderLength = 7;

    /// <summary>请求（ROSCTR = Job）的 S7 头长度。</summary>
    public const int JobHeaderLength = 10;

    /// <summary>响应（ROSCTR = Ack_Data）的 S7 头长度，比请求多出错误类别和错误码两个字节。</summary>
    public const int AckDataHeaderLength = 12;

    /// <summary>读/写请求中单个数据项规格（Any 指针）的长度。</summary>
    public const int ItemSpecLength = 12;

    /// <summary>响应数据段中单个数据项头部的长度：返回码 + 传输尺寸 + 长度。</summary>
    public const int DataItemHeaderLength = 4;

    /// <summary>协商时请求的 PDU 长度。设备会在响应里给出实际协商值，通常是 240 / 480 / 960。</summary>
    public const ushort RequestedPduLength = 960;

    internal const byte ProtocolId = 0x32;
    internal const byte RosctrJob = 0x01;
    internal const byte RosctrAckData = 0x03;
    internal const byte FunctionSetupCommunication = 0xF0;
    internal const byte FunctionRead = 0x04;
    internal const byte FunctionWrite = 0x05;
    internal const byte VariableSpecification = 0x12;
    internal const byte AnyPointerLength = 0x0A;
    internal const byte SyntaxIdS7Any = 0x10;
    internal const byte TransportSizeBit = 0x01;
    internal const byte TransportSizeByte = 0x02;
    internal const byte TransportSizeCounter = 0x1C;
    internal const byte TransportSizeTimer = 0x1D;

    /// <summary>
    /// 一次读取能取回的最大字节数。
    /// <para>
    /// 协商得到的 PDU 长度是从 S7 协议标识 <c>0x32</c> 起算的，不含 TPKT 和 COTP。
    /// 响应侧的固定开销 = Ack_Data 头 12 + 参数 2 + 数据项头 4 = 18 字节。
    /// 因此 S7-300 协商 240 时单次可读 222 字节，S7-1500 协商 480 时为 462 字节，
    /// 与西门子社区广为流传的经验值一致。
    /// </para>
    /// </summary>
    public static int MaxReadBytes(int negotiatedPduLength)
        => negotiatedPduLength - AckDataHeaderLength - 2 - DataItemHeaderLength;

    /// <summary>
    /// 一次写入能提交的最大字节数。
    /// <para>
    /// 请求侧开销 = Job 头 10 + 参数 2 + 数据项规格 12 + 数据项头 4 = 28 字节。
    /// Snap7 在此处保守地按 35 计算（多扣掉了 TPKT+COTP 的 7 字节）。
    /// 写命令频率低，宁可保守也不要被设备拒收，因此这里同样留出那 7 字节余量。
    /// </para>
    /// </summary>
    public static int MaxWriteBytes(int negotiatedPduLength)
        => negotiatedPduLength - JobHeaderLength - 2 - ItemSpecLength - DataItemHeaderLength - IsoHeaderLength;

    /// <summary>
    /// 单个请求报文最多能携带的数据项个数。
    /// 请求侧开销 = Job 头 10 + 参数 2，此后每项占 12 字节。
    /// </summary>
    public static int MaxReadItems(int negotiatedPduLength)
        => (negotiatedPduLength - JobHeaderLength - 2) / ItemSpecLength;

    /// <summary>
    /// 判断 <paramref name="itemByteLengths"/> 这批数据项的响应能否装进一个 PDU。
    /// 批量合并算法在切分请求时必须同时满足项数上限和这个响应容量上限。
    /// </summary>
    public static bool ResponseFitsInPdu(int negotiatedPduLength, ReadOnlySpan<int> itemByteLengths)
    {
        var total = AckDataHeaderLength + 2;
        for (var i = 0; i < itemByteLengths.Length; i++)
        {
            total += DataItemHeaderLength + itemByteLengths[i];

            // 除最后一项外，奇数长度的数据后面会补一个填充字节
            if (i != itemByteLengths.Length - 1 && (itemByteLengths[i] & 1) == 1)
            {
                total++;
            }
        }

        return total <= negotiatedPduLength;
    }

    /// <summary>该传输尺寸的长度字段是以位计（true）还是以字节计（false）。</summary>
    public static bool IsBitCountedLength(S7DataTransportSize size)
        => size is S7DataTransportSize.Bit or S7DataTransportSize.Byte or S7DataTransportSize.Int;
}
