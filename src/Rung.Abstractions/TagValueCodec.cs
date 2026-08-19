using System.Buffers.Binary;

namespace Rung.Abstractions;

/// <summary>
/// 原始字节与 <see cref="TagValue"/> 之间的通用转换：字节序、标量类型、线性换算。
/// <para>
/// 放在契约层而不是某个协议里，是因为这套逻辑<b>和协议无关</b>。
/// S7、Modbus、三菱 MC 面对的是同样的四种字节排列和同样的换算规则，
/// 各写一份的结果必然是各错各的——而字节序错了不会崩，只会读出一个
/// "看着像那么回事"的错数，是最难查的一类问题。
/// </para>
/// <para>
/// 协议特有的部分（比如西门子 STRING 的 2 字节头）由各自的协议层在外面包一层。
/// </para>
/// </summary>
public static class TagValueCodec
{
    /// <summary>单个标量的最大字节数，用于栈上缓冲区。</summary>
    public const int MaxScalarBytes = 8;

    /// <summary>
    /// 把设备上的字节排列还原成大端序。
    /// <para>
    /// 以内存中读到的字节 A B C D 为基准，枚举名表示还原成数值时的取用顺序。
    /// 拆成"字内换字节"和"换字顺序"两个开关，2/4/8 字节都能统一处理：
    /// 两个开关都打开正好等于整体倒序。
    /// </para>
    /// <para>
    /// 2 字节类型只有一个字，"换字顺序"是空操作，因此
    /// <see cref="ByteOrder.ABCD"/>/<see cref="ByteOrder.CDAB"/> 都是大端，
    /// <see cref="ByteOrder.BADC"/>/<see cref="ByteOrder.DCBA"/> 都是小端，
    /// 与主流网关的约定一致。
    /// </para>
    /// </summary>
    public static void Normalize(ReadOnlySpan<byte> source, Span<byte> destination, ByteOrder order)
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

    /// <summary>
    /// 按点位配置把原始字节解成标量值。调用方负责确保 <paramref name="raw"/>
    /// 至少有 <c>tag.DataType.SizeInBytes()</c> 个字节。
    /// </summary>
    public static TagValue DecodeScalar(ReadOnlySpan<byte> raw, TagDef tag, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(tag);

        Span<byte> ordered = stackalloc byte[MaxScalarBytes];
        var width = tag.DataType.SizeInBytes();
        Normalize(raw[..width], ordered[..width], tag.ByteOrder);

        return FromBigEndian(ordered[..width], tag, timestampUtc);
    }

    /// <summary>按点位配置把一个值编码成设备字节序的原始字节。</summary>
    /// <returns>写入的字节数。</returns>
    public static int EncodeScalar(Span<byte> destination, TagDef tag, TagValue value)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var width = tag.DataType.SizeInBytes();
        Span<byte> bigEndian = stackalloc byte[MaxScalarBytes];
        ToBigEndian(bigEndian[..width], tag, value);

        // 编码是解码的逆运算，而两个开关都是对合的，同一个变换用两次即可还原
        Normalize(bigEndian[..width], destination[..width], tag.ByteOrder);

        return width;
    }

    /// <summary>该点位是否配置了线性换算。</summary>
    public static bool IsScaled(TagDef tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Scale != 1.0 || tag.Offset != 0.0;
    }

    private static TagValue FromBigEndian(ReadOnlySpan<byte> bigEndian, TagDef tag, DateTime timestampUtc)
    {
        // 有线性换算时结果是工程值，类型提升为 Float64。
        // DataType 描述的是设备侧的存储形式，不是应用侧看到的类型
        var scaled = IsScaled(tag);

        switch (tag.DataType)
        {
            case TagDataType.Bool:
                return TagValue.FromBool(bigEndian[0] != 0, timestampUtc);
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
                throw new RungException($"点位 {tag.Name} 的数据类型 {tag.DataType} 不支持标量解码");
        }

        TagValue Finish(long rawValue) => scaled
            ? TagValue.FromDouble((rawValue * tag.Scale) + tag.Offset, timestampUtc)
            : TagValue.FromInteger(tag.DataType, rawValue, timestampUtc);
    }

    private static void ToBigEndian(Span<byte> bigEndian, TagDef tag, TagValue value)
    {
        var scaled = IsScaled(tag);

        // 写入时反算：应用给的是工程值，设备要的是原始值
        double AsRawDouble() => scaled ? (value.AsDouble() - tag.Offset) / tag.Scale : value.AsDouble();
        long AsRawInteger() => scaled ? (long)Math.Round(AsRawDouble()) : value.AsInt64();

        switch (tag.DataType)
        {
            case TagDataType.Bool:
                bigEndian[0] = value.AsBool() ? (byte)1 : (byte)0;
                break;
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
                throw new RungException($"点位 {tag.Name} 的数据类型 {tag.DataType} 不支持标量编码");
        }
    }
}
