namespace Rung.Abstractions;

/// <summary>点位的数据类型。决定读取长度、字节序处理方式和 <see cref="TagValue"/> 的存储槽位。</summary>
public enum TagDataType : byte
{
    /// <summary>单个位。</summary>
    Bool = 0,

    /// <summary>8 位有符号整数。</summary>
    Int8 = 1,

    /// <summary>8 位无符号整数。</summary>
    UInt8 = 2,

    /// <summary>16 位有符号整数。</summary>
    Int16 = 3,

    /// <summary>16 位无符号整数。</summary>
    UInt16 = 4,

    /// <summary>32 位有符号整数。</summary>
    Int32 = 5,

    /// <summary>32 位无符号整数。</summary>
    UInt32 = 6,

    /// <summary>64 位有符号整数。</summary>
    Int64 = 7,

    /// <summary>64 位无符号整数。</summary>
    UInt64 = 8,

    /// <summary>32 位 IEEE-754 浮点数。</summary>
    Float32 = 9,

    /// <summary>64 位 IEEE-754 浮点数。</summary>
    Float64 = 10,

    /// <summary>字符串。长度由 <see cref="TagDef.Length"/> 指定。</summary>
    String = 11,

    /// <summary>原始字节数组。长度由 <see cref="TagDef.Length"/> 指定。</summary>
    Bytes = 12,
}

/// <summary>与 <see cref="TagDataType"/> 相关的尺寸计算。</summary>
public static class TagDataTypeExtensions
{
    /// <summary>
    /// 返回该类型占用的字节数；<see cref="TagDataType.Bool"/> 返回 1（按位读取时另行处理）。
    /// 变长类型（<see cref="TagDataType.String"/>、<see cref="TagDataType.Bytes"/>）返回 0，
    /// 调用方必须改用 <see cref="TagDef.Length"/>。
    /// </summary>
    public static int SizeInBytes(this TagDataType type) => type switch
    {
        TagDataType.Bool or TagDataType.Int8 or TagDataType.UInt8 => 1,
        TagDataType.Int16 or TagDataType.UInt16 => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 4,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 8,
        TagDataType.String or TagDataType.Bytes => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的点位数据类型"),
    };

    /// <summary>该类型是否为变长类型，需要 <see cref="TagDef.Length"/> 才能确定尺寸。</summary>
    public static bool IsVariableLength(this TagDataType type)
        => type is TagDataType.String or TagDataType.Bytes;

    /// <summary>该类型是否可以参与 <see cref="TagDef.Scale"/> / <see cref="TagDef.Offset"/> 的线性换算。</summary>
    public static bool IsNumeric(this TagDataType type)
        => type is >= TagDataType.Int8 and <= TagDataType.Float64;
}
