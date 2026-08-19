using System.Buffers.Binary;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>
/// S7comm 请求报文组包器。
/// <para>
/// 全部方法都是纯函数：写入调用方给的 <see cref="Span{T}"/>，返回写入的字节数，
/// 不分配、不持有状态、不碰任何 IO。这样每一个字节都能被单元测试逐一断言，
/// 而移植过程中最危险的偏移量错误也就无处藏身。
/// </para>
/// </summary>
public static class S7RequestBuilder
{
    /// <summary>COTP 连接请求报文的固定长度。</summary>
    public const int ConnectionRequestLength = 22;

    /// <summary>通讯建立报文的固定长度。</summary>
    public const int SetupCommunicationLength = 25;

    /// <summary>计算读请求报文的总长度。</summary>
    public static int GetReadRequestLength(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(itemCount, 1);
        return S7Protocol.IsoHeaderLength + S7Protocol.JobHeaderLength + 2
            + (S7Protocol.ItemSpecLength * itemCount);
    }

    /// <summary>计算写请求报文的总长度。</summary>
    public static int GetWriteRequestLength(int dataByteLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dataByteLength, 1);
        return S7Protocol.IsoHeaderLength + S7Protocol.JobHeaderLength + 2
            + S7Protocol.ItemSpecLength + S7Protocol.DataItemHeaderLength + dataByteLength;
    }

    /// <summary>
    /// 组建 COTP 连接请求（CR TPDU）。这是握手的第一步，在它之上才能跑 S7comm。
    /// </summary>
    /// <param name="destination">目标缓冲区，至少 <see cref="ConnectionRequestLength"/> 字节。</param>
    /// <param name="rack">机架号。</param>
    /// <param name="slot">槽号。S7-300 通常 0/2，S7-1200/1500 通常 0/1。</param>
    /// <param name="connectionType">连接类型。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteConnectionRequest(
        Span<byte> destination,
        byte rack,
        byte slot,
        S7ConnectionType connectionType = S7ConnectionType.Pg)
    {
        EnsureCapacity(destination, ConnectionRequestLength);

        WriteTpktHeader(destination, ConnectionRequestLength);

        destination[4] = 0x11;  // LI：其后 17 字节
        destination[5] = 0xE0;  // CR - Connection Request
        destination[6] = 0x00;  // 目的引用 HI
        destination[7] = 0x00;  // 目的引用 LO
        destination[8] = 0x00;  // 源引用 HI
        destination[9] = 0x01;  // 源引用 LO
        destination[10] = 0x00; // 传输类别 0 + 选项

        destination[11] = 0xC0; // 参数：TPDU 最大长度
        destination[12] = 0x01;
        destination[13] = 0x0A; // 2^10 = 1024

        destination[14] = 0xC1; // 参数：源 TSAP
        destination[15] = 0x02;
        destination[16] = 0x01;
        destination[17] = 0x00;

        destination[18] = 0xC2; // 参数：目的 TSAP
        destination[19] = 0x02;
        destination[20] = (byte)connectionType;

        // 机架槽号编码进目的 TSAP 低字节：rack 占高 3 位，slot 占低 5 位
        destination[21] = (byte)((rack << 5) | (slot & 0x1F));

        return ConnectionRequestLength;
    }

    /// <summary>
    /// 组建通讯建立（Setup Communication）请求，用于协商 PDU 长度。
    /// 必须在 COTP 连接建立之后、任何读写之前发送。
    /// </summary>
    public static int WriteSetupCommunication(
        Span<byte> destination,
        ushort pduReference,
        ushort requestedPduLength = S7Protocol.RequestedPduLength)
    {
        EnsureCapacity(destination, SetupCommunicationLength);

        WriteTpktHeader(destination, SetupCommunicationLength);
        WriteCotpDataHeader(destination);
        WriteJobHeader(destination, pduReference, parameterLength: 8, dataLength: 0);

        destination[17] = S7Protocol.FunctionSetupCommunication;
        destination[18] = 0x00;                                          // 保留
        BinaryPrimitives.WriteUInt16BigEndian(destination[19..], 0x0001); // 主叫最大并发作业数
        BinaryPrimitives.WriteUInt16BigEndian(destination[21..], 0x0001); // 被叫最大并发作业数
        BinaryPrimitives.WriteUInt16BigEndian(destination[23..], requestedPduLength);

        return SetupCommunicationLength;
    }

    /// <summary>
    /// 组建读变量请求（Read Var，功能码 0x04）。
    /// </summary>
    /// <param name="destination">目标缓冲区，至少 <see cref="GetReadRequestLength"/> 字节。</param>
    /// <param name="pduReference">PDU 序号，用于把响应和请求对上。</param>
    /// <param name="items">要读取的数据项，个数不得超过 <see cref="S7Protocol.MaxReadItems"/>。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteReadRequest(Span<byte> destination, ushort pduReference, ReadOnlySpan<S7ReadItem> items)
    {
        if (items.IsEmpty)
        {
            throw new ArgumentException("读请求至少要包含一个数据项", nameof(items));
        }

        if (items.Length > byte.MaxValue)
        {
            throw new ArgumentException($"单个请求最多 255 个数据项，实际 {items.Length} 个", nameof(items));
        }

        var totalLength = GetReadRequestLength(items.Length);
        EnsureCapacity(destination, totalLength);

        var parameterLength = (ushort)(2 + (S7Protocol.ItemSpecLength * items.Length));

        WriteTpktHeader(destination, totalLength);
        WriteCotpDataHeader(destination);
        WriteJobHeader(destination, pduReference, parameterLength, dataLength: 0);

        destination[17] = S7Protocol.FunctionRead;
        destination[18] = (byte)items.Length;

        var offset = 19;
        foreach (var item in items)
        {
            WriteItemSpec(destination[offset..], item.Address, item.Count, item.IsBitAccess);
            offset += S7Protocol.ItemSpecLength;
        }

        return totalLength;
    }

    /// <summary>
    /// 组建写变量请求（Write Var，功能码 0x05）。单数据项——
    /// 写命令是事件驱动的、频率低，没有合并的必要，保持简单更不容易出错。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="pduReference">PDU 序号。</param>
    /// <param name="address">目标地址。</param>
    /// <param name="data">要写入的数据。按位写入时长度必须为 1，且仅取最低位。</param>
    /// <param name="bitAccess">是否为单个位的写入。</param>
    /// <returns>写入的字节数。</returns>
    public static int WriteWriteRequest(
        Span<byte> destination,
        ushort pduReference,
        S7Address address,
        ReadOnlySpan<byte> data,
        bool bitAccess = false)
    {
        if (data.IsEmpty)
        {
            throw new ArgumentException("写请求不能没有数据", nameof(data));
        }

        if (bitAccess && data.Length != 1)
        {
            throw new ArgumentException("按位写入时数据长度必须为 1 字节", nameof(data));
        }

        var totalLength = GetWriteRequestLength(data.Length);
        EnsureCapacity(destination, totalLength);

        var parameterLength = (ushort)(2 + S7Protocol.ItemSpecLength);
        var dataLength = (ushort)(S7Protocol.DataItemHeaderLength + data.Length);

        WriteTpktHeader(destination, totalLength);
        WriteCotpDataHeader(destination);
        WriteJobHeader(destination, pduReference, parameterLength, dataLength);

        destination[17] = S7Protocol.FunctionWrite;
        destination[18] = 0x01;

        WriteItemSpec(destination[19..], address, bitAccess ? 1 : data.Length, bitAccess);

        var dataOffset = 19 + S7Protocol.ItemSpecLength;
        destination[dataOffset] = 0x00; // 请求侧该字节保留

        // 长度单位随传输尺寸变化：位以位计，字节以位计
        destination[dataOffset + 1] = (byte)(bitAccess ? S7DataTransportSize.Bit : S7DataTransportSize.Byte);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(dataOffset + 2)..],
            (ushort)(bitAccess ? 1 : data.Length * 8));

        data.CopyTo(destination[(dataOffset + S7Protocol.DataItemHeaderLength)..]);

        return totalLength;
    }

    /// <summary>写入 12 字节的 Any 指针数据项规格。</summary>
    private static void WriteItemSpec(Span<byte> destination, S7Address address, int count, bool bitAccess)
    {
        destination[0] = S7Protocol.VariableSpecification;
        destination[1] = S7Protocol.AnyPointerLength;
        destination[2] = S7Protocol.SyntaxIdS7Any;
        destination[3] = address.Area switch
        {
            S7Area.Timer => S7Protocol.TransportSizeTimer,
            S7Area.Counter => S7Protocol.TransportSizeCounter,
            _ when bitAccess => S7Protocol.TransportSizeBit,
            _ => S7Protocol.TransportSizeByte,
        };

        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], address.DbNumber);
        destination[8] = (byte)address.Area;

        // 3 字节大端的位地址：即便按字节访问，协议要的也是位地址
        var bitAddress = address.BitAddress;
        if ((uint)bitAddress > 0xFFFFFF)
        {
            throw new AddressFormatException(address.ToString(), "字节偏移超出 S7 地址空间（最大 2097151）");
        }

        destination[9] = (byte)(bitAddress >> 16);
        destination[10] = (byte)(bitAddress >> 8);
        destination[11] = (byte)bitAddress;
    }

    private static void WriteTpktHeader(Span<byte> destination, int totalLength)
    {
        destination[0] = 0x03; // TPKT 版本，RFC 1006 固定为 3
        destination[1] = 0x00; // 保留
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)totalLength);
    }

    private static void WriteCotpDataHeader(Span<byte> destination)
    {
        destination[4] = 0x02; // LI：其后 2 字节
        destination[5] = 0xF0; // DT - Data Transfer
        destination[6] = 0x80; // TPDU 序号 0 + EOT
    }

    private static void WriteJobHeader(
        Span<byte> destination,
        ushort pduReference,
        ushort parameterLength,
        ushort dataLength)
    {
        destination[7] = S7Protocol.ProtocolId;
        destination[8] = S7Protocol.RosctrJob;
        BinaryPrimitives.WriteUInt16BigEndian(destination[9..], 0x0000); // 冗余标识，保留
        BinaryPrimitives.WriteUInt16BigEndian(destination[11..], pduReference);
        BinaryPrimitives.WriteUInt16BigEndian(destination[13..], parameterLength);
        BinaryPrimitives.WriteUInt16BigEndian(destination[15..], dataLength);
    }

    private static void EnsureCapacity(Span<byte> destination, int required)
    {
        if (destination.Length < required)
        {
            throw new ArgumentException($"缓冲区不足：需要 {required} 字节，实际 {destination.Length} 字节", nameof(destination));
        }
    }
}
