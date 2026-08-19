namespace Rung.Abstractions;

/// <summary>
/// 一次采集得到的值。
/// <para>
/// 刻意设计成不含点位名的值类型：调用方持有 <see cref="TagDef"/> 列表，
/// 采集结果按下标一一对应写入目标数组，因此名字不需要在热路径上重复携带。
/// </para>
/// <para>
/// 数值统一存进一个 <see cref="long"/> 位槽（浮点数按 IEEE-754 位模式重解释），
/// 只有字符串和字节数组才走引用字段。这样每轮采集上千个点位也不会产生装箱垃圾——
/// 500 ms 周期下这个差别在 GC 上是看得见的。
/// </para>
/// </summary>
public readonly struct TagValue : IEquatable<TagValue>
{
    private readonly long _bits;
    private readonly object? _reference;

    private TagValue(TagDataType dataType, TagQuality quality, DateTime timestampUtc, long bits, object? reference)
    {
        DataType = dataType;
        Quality = quality;
        TimestampUtc = timestampUtc;
        _bits = bits;
        _reference = reference;
    }

    /// <summary>值的数据类型。</summary>
    public TagDataType DataType { get; }

    /// <summary>本次采集的质量。</summary>
    public TagQuality Quality { get; }

    /// <summary>采集时刻，始终为 UTC。展示层负责转本地时区。</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>质量是否为 <see cref="TagQuality.Good"/>。</summary>
    public bool IsGood => Quality == TagQuality.Good;

    /// <summary>构造一个布尔值。</summary>
    public static TagValue FromBool(bool value, DateTime timestampUtc)
        => new(TagDataType.Bool, TagQuality.Good, timestampUtc, value ? 1L : 0L, null);

    /// <summary>构造一个整数值。<paramref name="dataType"/> 必须是整数类型。</summary>
    public static TagValue FromInteger(TagDataType dataType, long value, DateTime timestampUtc)
    {
        if (dataType is < TagDataType.Int8 or > TagDataType.UInt64)
        {
            throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "不是整数类型");
        }

        return new TagValue(dataType, TagQuality.Good, timestampUtc, value, null);
    }

    /// <summary>构造一个 32 位浮点值。</summary>
    public static TagValue FromSingle(float value, DateTime timestampUtc)
        => new(TagDataType.Float32, TagQuality.Good, timestampUtc, BitConverter.SingleToInt32Bits(value), null);

    /// <summary>构造一个 64 位浮点值。</summary>
    public static TagValue FromDouble(double value, DateTime timestampUtc)
        => new(TagDataType.Float64, TagQuality.Good, timestampUtc, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>构造一个字符串值。</summary>
    public static TagValue FromString(string value, DateTime timestampUtc)
        => new(TagDataType.String, TagQuality.Good, timestampUtc, 0L, value);

    /// <summary>构造一个字节数组值。</summary>
    public static TagValue FromBytes(byte[] value, DateTime timestampUtc)
        => new(TagDataType.Bytes, TagQuality.Good, timestampUtc, 0L, value);

    /// <summary>
    /// 构造一个坏值。保留 <paramref name="dataType"/> 以便北向输出仍能给出正确的类型信息。
    /// </summary>
    public static TagValue Bad(TagDataType dataType, TagQuality quality, DateTime timestampUtc)
    {
        if (quality == TagQuality.Good)
        {
            throw new ArgumentException("坏值的质量不能是 Good", nameof(quality));
        }

        return new TagValue(dataType, quality, timestampUtc, 0L, null);
    }

    /// <summary>把当前值降级为 <see cref="TagQuality.Stale"/>，保留原值和原时间戳。</summary>
    public TagValue AsStale() => new(DataType, TagQuality.Stale, TimestampUtc, _bits, _reference);

    /// <summary>读取布尔值。</summary>
    public bool AsBool() => _bits != 0L;

    /// <summary>按整数读取。浮点类型会被截断，字符串/字节数组抛异常。</summary>
    public long AsInt64() => DataType switch
    {
        TagDataType.Float32 => (long)BitConverter.Int32BitsToSingle((int)_bits),
        TagDataType.Float64 => (long)BitConverter.Int64BitsToDouble(_bits),
        TagDataType.String or TagDataType.Bytes => throw new InvalidOperationException($"{DataType} 无法转为整数"),
        _ => _bits,
    };

    /// <summary>
    /// 按 <see cref="double"/> 读取。<see cref="TagDef.Scale"/> / <see cref="TagDef.Offset"/>
    /// 的线性换算走这条路径。
    /// </summary>
    public double AsDouble() => DataType switch
    {
        TagDataType.Float32 => BitConverter.Int32BitsToSingle((int)_bits),
        TagDataType.Float64 => BitConverter.Int64BitsToDouble(_bits),
        TagDataType.UInt64 => unchecked((ulong)_bits),
        TagDataType.String or TagDataType.Bytes => throw new InvalidOperationException($"{DataType} 无法转为浮点数"),
        _ => _bits,
    };

    /// <summary>读取字符串值。</summary>
    public string AsString() => _reference as string
        ?? throw new InvalidOperationException($"{DataType} 不是字符串");

    /// <summary>读取字节数组值。</summary>
    public byte[] AsBytes() => _reference as byte[]
        ?? throw new InvalidOperationException($"{DataType} 不是字节数组");

    /// <summary>
    /// 转成装箱的 CLR 对象。仅用于序列化边界（REST / MQTT 输出），
    /// 热路径上不要调用——它会为每个点位产生一次装箱。
    /// </summary>
    public object? ToObject() => DataType switch
    {
        // Stale 保留最后已知值，其余非 Good 状态没有可用的值可给
        _ when Quality is not (TagQuality.Good or TagQuality.Stale) => null,
        TagDataType.Bool => AsBool(),
        TagDataType.Int8 => (sbyte)_bits,
        TagDataType.UInt8 => (byte)_bits,
        TagDataType.Int16 => (short)_bits,
        TagDataType.UInt16 => (ushort)_bits,
        TagDataType.Int32 => (int)_bits,
        TagDataType.UInt32 => (uint)_bits,
        TagDataType.Int64 => _bits,
        TagDataType.UInt64 => unchecked((ulong)_bits),
        TagDataType.Float32 => BitConverter.Int32BitsToSingle((int)_bits),
        TagDataType.Float64 => BitConverter.Int64BitsToDouble(_bits),
        TagDataType.String or TagDataType.Bytes => _reference,
        _ => null,
    };

    /// <inheritdoc/>
    public bool Equals(TagValue other)
        => DataType == other.DataType
        && Quality == other.Quality
        && TimestampUtc == other.TimestampUtc
        && _bits == other._bits
        && ReferenceEquals(_reference, other._reference);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TagValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(DataType, Quality, TimestampUtc, _bits, _reference);

    /// <summary>相等运算符。</summary>
    public static bool operator ==(TagValue left, TagValue right) => left.Equals(right);

    /// <summary>不等运算符。</summary>
    public static bool operator !=(TagValue left, TagValue right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString()
        => IsGood
            ? FormattableString.Invariant($"{ToObject()} [{DataType}]")
            : FormattableString.Invariant($"<{Quality}> [{DataType}]");
}
