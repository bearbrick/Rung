using System.Buffers.Binary;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>S7comm 响应报文解析器。与组包器一样是纯函数，不分配、不做 IO。</summary>
public static class S7ResponseReader
{
    /// <summary>
    /// 从 TPKT 头部读出整帧长度。
    /// <para>
    /// 传输层靠它决定还要再收多少字节：先固定读 4 字节 TPKT 头，
    /// 拿到总长后再把剩下的收齐，避免半包/粘包。
    /// </para>
    /// </summary>
    /// <param name="header">至少 4 字节的 TPKT 头。</param>
    /// <returns>整帧字节数，含 TPKT 头自身。</returns>
    public static int ReadFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            throw new ProtocolException($"TPKT 头至少需要 4 字节，实际 {header.Length} 字节");
        }

        if (header[0] != 0x03)
        {
            throw new ProtocolException($"TPKT 版本应为 0x03，实际 0x{header[0]:X2}");
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (length < 7)
        {
            throw new ProtocolException($"TPKT 声明的帧长 {length} 小于最小报文长度");
        }

        return length;
    }

    /// <summary>
    /// 校验 COTP 连接确认（CC TPDU）。握手第一步的响应。
    /// </summary>
    /// <exception cref="ProtocolException">对端拒绝连接，通常是机架槽号配错了。</exception>
    public static void ValidateConnectionConfirm(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6)
        {
            throw new ProtocolException($"COTP 响应过短：{frame.Length} 字节");
        }

        var pduType = frame[5];
        if (pduType != 0xD0)
        {
            var reason = pduType == 0x80
                ? "对端拒绝了连接（DR），请检查机架号/槽号，以及 PLC 是否还有空闲连接资源"
                : $"期望连接确认 0xD0，实际 0x{pduType:X2}";
            throw new ProtocolException(reason);
        }
    }

    /// <summary>
    /// 解析通讯建立响应，返回协商后的 PDU 长度。
    /// 之后所有的批量合并切分都以这个值为准。
    /// </summary>
    public static ushort ReadNegotiatedPduLength(ReadOnlySpan<byte> frame)
    {
        ValidateAckDataHeader(frame, S7Protocol.FunctionSetupCommunication, minimumLength: 27);

        // 参数段：F0 00 <主叫:2> <被叫:2> <PDU长度:2>
        var pduLength = BinaryPrimitives.ReadUInt16BigEndian(frame[25..]);
        if (pduLength < 240)
        {
            throw new ProtocolException($"协商得到的 PDU 长度 {pduLength} 异常，最小应为 240");
        }

        return pduLength;
    }

    /// <summary>
    /// 解析写变量响应，返回设备给出的状态码。
    /// </summary>
    public static S7ReturnCode ReadWriteResult(ReadOnlySpan<byte> frame)
    {
        ValidateAckDataHeader(frame, S7Protocol.FunctionWrite, minimumLength: 22);

        var itemCount = frame[20];
        if (itemCount != 1)
        {
            throw new ProtocolException($"写响应应包含 1 个数据项，实际 {itemCount} 个");
        }

        return (S7ReturnCode)frame[21];
    }

    /// <summary>
    /// 打开一个读变量响应的数据项游标。
    /// </summary>
    public static S7ReadResultCursor ReadResults(ReadOnlySpan<byte> frame)
    {
        ValidateAckDataHeader(frame, S7Protocol.FunctionRead, minimumLength: 21);
        return new S7ReadResultCursor(frame);
    }

    /// <summary>
    /// 校验响应帧的 TPKT / COTP / S7 头，并确认功能码符合预期。
    /// </summary>
    internal static void ValidateAckDataHeader(ReadOnlySpan<byte> frame, byte expectedFunction, int minimumLength)
    {
        if (frame.Length < minimumLength)
        {
            throw new ProtocolException($"响应报文过短：需要至少 {minimumLength} 字节，实际 {frame.Length} 字节");
        }

        if (frame[0] != 0x03)
        {
            throw new ProtocolException($"TPKT 版本应为 0x03，实际 0x{frame[0]:X2}");
        }

        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(frame[2..]);
        if (declaredLength != frame.Length)
        {
            throw new ProtocolException($"TPKT 声明帧长 {declaredLength}，实际收到 {frame.Length} 字节");
        }

        if (frame[5] != 0xF0)
        {
            throw new ProtocolException($"期望 COTP 数据传输 0xF0，实际 0x{frame[5]:X2}");
        }

        if (frame[7] != S7Protocol.ProtocolId)
        {
            throw new ProtocolException($"S7 协议标识应为 0x32，实际 0x{frame[7]:X2}");
        }

        if (frame[8] != S7Protocol.RosctrAckData)
        {
            throw new ProtocolException($"期望 Ack_Data(0x03)，实际 ROSCTR = 0x{frame[8]:X2}");
        }

        // 错误类别/错误码是整帧级别的失败，与单个数据项的返回码不是一回事
        var errorClass = frame[17];
        var errorCode = frame[18];
        if (errorClass != 0 || errorCode != 0)
        {
            throw new ProtocolException(
                $"设备返回错误：错误类别 0x{errorClass:X2}，错误码 0x{errorCode:X2}");
        }

        if (frame[19] != expectedFunction)
        {
            throw new ProtocolException($"期望功能码 0x{expectedFunction:X2}，实际 0x{frame[19]:X2}");
        }
    }
}

/// <summary>
/// 读变量响应的数据项游标。
/// <para>
/// 做成 <c>ref struct</c> 是为了让它只能活在栈上，从而可以安全地把
/// <see cref="ReadOnlySpan{T}"/> 直接切给调用方——整个解析过程零分配、零拷贝。
/// </para>
/// </summary>
public ref struct S7ReadResultCursor
{
    private readonly ReadOnlySpan<byte> _frame;
    private int _offset;
    private int _consumed;

    internal S7ReadResultCursor(ReadOnlySpan<byte> frame)
    {
        _frame = frame;
        ItemCount = frame[20];
        _offset = 21; // ISO 头 7 + Ack_Data 头 12 + 参数 2
        _consumed = 0;
    }

    /// <summary>响应中声明的数据项个数。应与请求中的项数一致。</summary>
    public int ItemCount { get; }

    /// <summary>已经读出的数据项个数。</summary>
    public readonly int Consumed => _consumed;

    /// <summary>
    /// 读出下一个数据项。
    /// </summary>
    /// <param name="returnCode">该项的状态码。非 <see cref="S7ReturnCode.Success"/> 时 <paramref name="data"/> 为空。</param>
    /// <param name="data">该项的原始数据，直接切自响应缓冲区，未做任何拷贝。</param>
    /// <returns>还有数据项时返回 true。</returns>
    public bool TryReadNext(out S7ReturnCode returnCode, out ReadOnlySpan<byte> data)
    {
        returnCode = S7ReturnCode.Reserved;
        data = default;

        if (_consumed >= ItemCount)
        {
            return false;
        }

        if (_offset + S7Protocol.DataItemHeaderLength > _frame.Length)
        {
            throw new ProtocolException(
                $"响应在第 {_consumed + 1} 项处截断：还需要 {S7Protocol.DataItemHeaderLength} 字节的数据项头");
        }

        returnCode = (S7ReturnCode)_frame[_offset];
        var transportSize = (S7DataTransportSize)_frame[_offset + 1];
        var rawLength = BinaryPrimitives.ReadUInt16BigEndian(_frame[(_offset + 2)..]);
        _offset += S7Protocol.DataItemHeaderLength;
        _consumed++;

        if (returnCode != S7ReturnCode.Success)
        {
            // 失败的数据项不携带数据，也不产生填充字节
            return true;
        }

        // 长度字段的单位随传输尺寸变化。这个换算搞错就会读出长度差 8 倍的数据，
        // 而且往往表现为"偶尔读到脏值"，是最难查的一类问题
        var byteLength = S7Protocol.IsBitCountedLength(transportSize)
            ? (rawLength + 7) / 8
            : rawLength;

        if (_offset + byteLength > _frame.Length)
        {
            throw new ProtocolException(
                $"响应在第 {_consumed} 项处截断：声明 {byteLength} 字节，缓冲区只剩 {_frame.Length - _offset} 字节");
        }

        data = _frame.Slice(_offset, byteLength);
        _offset += byteLength;

        // 除最后一项外，奇数长度的数据后面补一个填充字节
        if (_consumed < ItemCount && (byteLength & 1) == 1)
        {
            _offset++;
        }

        return true;
    }
}
