using System.Buffers.Binary;
using System.Text;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>
/// 原始字节与 <see cref="TagValue"/> 之间的互转。
/// <para>
/// 字节序处理是这里的核心。同一个品牌不同型号、甚至同一台 PLC 的不同功能块，
/// 32 位数的字节排列都可能不同，四种排列在真实产线上都见过。
/// 因此换算逻辑必须逐点位可配，而且必须有覆盖全部四种排列的测试向量——
/// 字节序错了不会崩，只会读出一个"看着像那么回事"的错数，最难查。
/// </para>
/// </summary>
public static class S7ValueCodec
{
    /// <summary>
    /// 西门子 STRING 的头部长度：第 1 字节是最大容量，第 2 字节是当前实际长度。
    /// 这 2 个字节是 S7 特有的，不体现在 <see cref="TagDef.ByteLength"/> 里。
    /// </summary>
    public const int S7StringHeaderLength = 2;

    /// <summary>单个值的最大字节数，用于栈上缓冲区。</summary>
    private const int MaxScalarBytes = 8;

    /// <summary>
    /// 该点位在 S7 上一次读取需要的字节数，含 STRING 的头部。
    /// </summary>
    public static int GetReadByteLength(TagDef tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return tag.DataType == TagDataType.String
            ? tag.Length + S7StringHeaderLength
            : tag.ByteLength;
    }

    /// <summary>
    /// 把原始字节解成一个采集值。
    /// </summary>
    /// <param name="raw">该点位对应的原始字节，长度应为 <see cref="GetReadByteLength"/>。</param>
    /// <param name="tag">点位配置。</param>
    /// <param name="bitOffset">位偏移，仅 <see cref="TagDataType.Bool"/> 使用。</param>
    /// <param name="timestampUtc">采集时刻，UTC。</param>
    public static TagValue Decode(ReadOnlySpan<byte> raw, TagDef tag, byte bitOffset, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var required = GetReadByteLength(tag);
        if (raw.Length < required)
        {
            throw new ProtocolException(
                $"点位 {tag.Name} 需要 {required} 字节，实际只有 {raw.Length} 字节");
        }

        if (tag.DataType == TagDataType.Bool)
        {
            return TagValue.FromBool((raw[0] & (1 << bitOffset)) != 0, timestampUtc);
        }

        if (tag.DataType == TagDataType.String)
        {
            return TagValue.FromString(DecodeS7String(raw, tag.Length), timestampUtc);
        }

        if (tag.DataType == TagDataType.Bytes)
        {
            return TagValue.FromBytes(raw[..tag.Length].ToArray(), timestampUtc);
        }

        Span<byte> ordered = stackalloc byte[MaxScalarBytes];
        var width = tag.DataType.SizeInBytes();
        Normalize(raw[..width], ordered[..width], tag.ByteOrder);

        return DecodeScalar(ordered[..width], tag, timestampUtc);
    }

    /// <summary>
    /// 把一个待写入的值编码成原始字节。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    /// <param name="tag">点位配置。</param>
    /// <param name="value">工程值。若配置了 <see cref="TagDef.Scale"/>，会先反算回原始值。</param>
    /// <returns>写入的字节数。</returns>
    public static int Encode(Span<byte> destination, TagDef tag, TagValue value)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (tag.DataType == TagDataType.Bool)
        {
            destination[0] = value.AsBool() ? (byte)1 : (byte)0;
            return 1;
        }

        if (tag.DataType == TagDataType.String)
        {
            return EncodeS7String(destination, value.AsString(), tag.Length);
        }

        if (tag.DataType == TagDataType.Bytes)
        {
            var bytes = value.AsBytes();
            bytes.CopyTo(destination);
            return bytes.Length;
        }

        var width = tag.DataType.SizeInBytes();
        Span<byte> bigEndian = stackalloc byte[MaxScalarBytes];
        EncodeScalar(bigEndian[..width], tag, value);

        // 编码是解码的逆运算，而这两个开关都是对合的，所以同一个变换用两次就能还原
        Normalize(bigEndian[..width], destination[..width], tag.ByteOrder);
        return width;
    }

    /// <summary>
    /// 把设备上的字节排列还原成大端序。
    /// <para>
    /// 以内存中读到的字节 A B C D 为基准，枚举名表示还原成数值时的取用顺序。
    /// 拆成"字内换字节"和"换字顺序"两个开关，8 字节类型也自然成立：
    /// 两个开关都打开正好等于整体倒序。
    /// </para>
    /// <para>
    /// 2 字节类型只有一个字，"换字顺序"是空操作，
    /// 因此 ABCD/CDAB 都是大端、BADC/DCBA 都是小端——与主流网关的约定一致。
    /// </para>
    /// </summary>
    internal static void Normalize(ReadOnlySpan<byte> source, Span<byte> destination, ByteOrder order)
    {
        var swapBytesInWord = order is ByteOrder.BADC or ByteOrder.DCBA;
        var swapWords = order is ByteOrder.CDAB or ByteOrder.DCBA;
        var wordCount = source.Length / 2;

        if (source.Length == 1 || (!swapBytesInWord && !swapWords))
        {
            source.CopyTo(destination);
            return;
        }

        for (var word = 0; word < wordCount; word++)
        {
            var targetWord = swapWords ? wordCount - 1 - word : word;

            var hi = source[word * 2];
            var lo = source[(word * 2) + 1];

            destination[targetWord * 2] = swapBytesInWord ? lo : hi;
            destination[(targetWord * 2) + 1] = swapBytesInWord ? hi : lo;
        }
    }

    private static TagValue DecodeScalar(ReadOnlySpan<byte> bigEndian, TagDef tag, DateTime timestampUtc)
    {
        // 有线性换算时，采集结果是工程值，类型提升为 Float64。
        // DataType 描述的是 PLC 侧的存储形式，不是应用侧看到的类型
        var scaled = tag.Scale != 1.0 || tag.Offset != 0.0;

        switch (tag.DataType)
        {
            case TagDataType.Int8:
                return Finish((sbyte)bigEndian[0]);
            case TagDataType.UInt8:
                return Finish(bigEndian[0]);
            case TagDataType.Int16:
                return Finish(BinaryPrimitives.ReadInt16BigEndian(bigEndian));
            case TagDataType.UInt16:
                return Finish(BinaryPrimitives.ReadUInt16BigEndian(bigEndian));
            case TagDataType.Int32:
                return Finish(BinaryPrimitives.ReadInt32BigEndian(bigEndian));
            case TagDataType.UInt32:
                return Finish(BinaryPrimitives.ReadUInt32BigEndian(bigEndian));
            case TagDataType.Int64:
                return Finish(BinaryPrimitives.ReadInt64BigEndian(bigEndian));
            case TagDataType.UInt64:
                var u64 = BinaryPrimitives.ReadUInt64BigEndian(bigEndian);
                return scaled
                    ? TagValue.FromDouble((u64 * tag.Scale) + tag.Offset, timestampUtc)
                    : TagValue.FromInteger(TagDataType.UInt64, unchecked((long)u64), timestampUtc);
            case TagDataType.Float32:
                var f32 = BinaryPrimitives.ReadSingleBigEndian(bigEndian);
                return scaled
                    ? TagValue.FromDouble((f32 * tag.Scale) + tag.Offset, timestampUtc)
                    : TagValue.FromSingle(f32, timestampUtc);
            case TagDataType.Float64:
                var f64 = BinaryPrimitives.ReadDoubleBigEndian(bigEndian);
                return TagValue.FromDouble(scaled ? (f64 * tag.Scale) + tag.Offset : f64, timestampUtc);
            default:
                throw new ProtocolException($"点位 {tag.Name} 的数据类型 {tag.DataType} 不支持标量解码");
        }

        TagValue Finish(long rawValue) => scaled
            ? TagValue.FromDouble((rawValue * tag.Scale) + tag.Offset, timestampUtc)
            : TagValue.FromInteger(tag.DataType, rawValue, timestampUtc);
    }

    private static void EncodeScalar(Span<byte> bigEndian, TagDef tag, TagValue value)
    {
        var scaled = tag.Scale != 1.0 || tag.Offset != 0.0;

        // 写入时反算：应用给的是工程值，PLC 要的是原始值
        double AsRawDouble() => scaled ? (value.AsDouble() - tag.Offset) / tag.Scale : value.AsDouble();
        long AsRawInteger() => scaled ? (long)Math.Round(AsRawDouble()) : value.AsInt64();

        switch (tag.DataType)
        {
            case TagDataType.Int8:
                bigEndian[0] = unchecked((byte)(sbyte)AsRawInteger());
                break;
            case TagDataType.UInt8:
                bigEndian[0] = unchecked((byte)AsRawInteger());
                break;
            case TagDataType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(bigEndian, unchecked((short)AsRawInteger()));
                break;
            case TagDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(bigEndian, unchecked((ushort)AsRawInteger()));
                break;
            case TagDataType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(bigEndian, unchecked((int)AsRawInteger()));
                break;
            case TagDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(bigEndian, unchecked((uint)AsRawInteger()));
                break;
            case TagDataType.Int64:
                BinaryPrimitives.WriteInt64BigEndian(bigEndian, AsRawInteger());
                break;
            case TagDataType.UInt64:
                BinaryPrimitives.WriteUInt64BigEndian(bigEndian, unchecked((ulong)AsRawInteger()));
                break;
            case TagDataType.Float32:
                BinaryPrimitives.WriteSingleBigEndian(bigEndian, (float)AsRawDouble());
                break;
            case TagDataType.Float64:
                BinaryPrimitives.WriteDoubleBigEndian(bigEndian, AsRawDouble());
                break;
            default:
                throw new ProtocolException($"点位 {tag.Name} 的数据类型 {tag.DataType} 不支持标量编码");
        }
    }

    /// <summary>解析西门子 STRING：[最大容量][实际长度][字符…]。</summary>
    private static string DecodeS7String(ReadOnlySpan<byte> raw, int capacity)
    {
        var actual = raw[1];

        // 实际长度字段来自设备，不可信：DB 没初始化过时这里可能是任意值
        var length = Math.Min(actual, Math.Min(capacity, raw.Length - S7StringHeaderLength));

        return length <= 0
            ? string.Empty
            : Encoding.ASCII.GetString(raw.Slice(S7StringHeaderLength, length));
    }

    private static int EncodeS7String(Span<byte> destination, string value, int capacity)
    {
        var length = Math.Min(value.Length, capacity);

        destination[0] = (byte)capacity;
        destination[1] = (byte)length;
        Encoding.ASCII.GetBytes(value.AsSpan(0, length), destination[S7StringHeaderLength..]);

        return capacity + S7StringHeaderLength;
    }
}
