namespace Rung.Protocols.S7;

/// <summary>S7 存储区代码。取值直接就是协议报文中 Any 指针的区域字节。</summary>
public enum S7Area : byte
{
    /// <summary>定时器 T。</summary>
    Timer = 0x1D,

    /// <summary>计数器 C。</summary>
    Counter = 0x1C,

    /// <summary>直接外设访问 P。</summary>
    Peripheral = 0x80,

    /// <summary>输入区 I（德文 E）。</summary>
    Input = 0x81,

    /// <summary>输出区 Q（德文 A）。</summary>
    Output = 0x82,

    /// <summary>位存储区 M（Merker）。</summary>
    Memory = 0x83,

    /// <summary>数据块 DB。</summary>
    DataBlock = 0x84,

    /// <summary>背景数据块 DI。</summary>
    InstanceDataBlock = 0x85,

    /// <summary>局部变量 L。</summary>
    LocalData = 0x86,
}

/// <summary>
/// 地址字符串里的宽度字母（X/B/W/D）给出的尺寸提示。
/// <para>
/// 它<b>不</b>决定实际读取长度——长度来自点位配置的数据类型。
/// 保留它是为了做一致性校验：地址写 <c>DB1.DBW10</c>（2 字节）却把数据类型配成
/// Float32（4 字节），这是配置错误，应该在编译读取计划时就报出来，
/// 而不是等到现场读回一个乱七八糟的数。
/// </para>
/// </summary>
public enum S7SizeHint : byte
{
    /// <summary>地址未给出宽度信息。</summary>
    None = 0,

    /// <summary>位（X）。</summary>
    Bit = 1,

    /// <summary>字节（B）。</summary>
    Byte = 2,

    /// <summary>字，2 字节（W）。</summary>
    Word = 4,

    /// <summary>双字，4 字节（D）。</summary>
    DWord = 8,
}

/// <summary>与 <see cref="S7SizeHint"/> 相关的辅助方法。</summary>
public static class S7SizeHintExtensions
{
    /// <summary>提示对应的字节数；<see cref="S7SizeHint.Bit"/> 和 <see cref="S7SizeHint.None"/> 返回 0。</summary>
    public static int ToByteCount(this S7SizeHint hint) => hint switch
    {
        S7SizeHint.Byte => 1,
        S7SizeHint.Word => 2,
        S7SizeHint.DWord => 4,
        _ => 0,
    };
}
