namespace Rung.Abstractions;

/// <summary>
/// 多字节数值在设备上的字节排列方式。
/// <para>
/// 这是现场最容易踩的坑：同一个品牌不同型号、甚至同一台 PLC 的不同功能块，
/// 32 位整数和浮点数的字节序都可能不一样。四种排列在真实产线上都见过，
/// 因此该选项必须逐点位可配，不能在驱动里写死。
/// </para>
/// <para>
/// 命名以内存中读到的 4 个字节 A B C D 为基准，字母顺序表示还原成数值时的取用顺序。
/// 对 2 字节类型只看前两个字母，8 字节类型按同样规则扩展。
/// </para>
/// </summary>
public enum ByteOrder : byte
{
    /// <summary>大端序（Big-Endian）。西门子 S7、多数 Modbus 设备的默认排列。</summary>
    ABCD = 0,

    /// <summary>字交换的大端序（Big-Endian Byte Swap）。Modbus 设备最常见的变体。</summary>
    CDAB = 1,

    /// <summary>字节交换的小端序（Little-Endian Byte Swap）。</summary>
    BADC = 2,

    /// <summary>小端序（Little-Endian）。三菱、部分欧姆龙型号。</summary>
    DCBA = 3,
}
