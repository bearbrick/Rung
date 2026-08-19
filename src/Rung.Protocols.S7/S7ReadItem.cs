namespace Rung.Protocols.S7;

/// <summary>
/// 一次读取请求中的单个数据项。
/// <para>
/// 除单个位以外一律按字节读取（传输尺寸 BYTE），长度即字节数——
/// 这是 Snap7 / Sharp7 多年验证下来最稳妥的做法：把类型解释留给上层，
/// 协议层只负责把正确数量的字节搬回来。
/// </para>
/// </summary>
public readonly record struct S7ReadItem
{
    private S7ReadItem(S7Address address, int count, bool isBitAccess)
    {
        Address = address;
        Count = count;
        IsBitAccess = isBitAccess;
    }

    /// <summary>目标地址。</summary>
    public S7Address Address { get; }

    /// <summary>元素个数：按位读取时恒为 1，否则为字节数。</summary>
    public int Count { get; }

    /// <summary>是否为单个位的读取。</summary>
    public bool IsBitAccess { get; }

    /// <summary>该项在响应数据段中预期占用的字节数。</summary>
    public int ExpectedByteLength => IsBitAccess ? 1 : Count;

    /// <summary>构造一个按字节读取的数据项。</summary>
    public static S7ReadItem Bytes(S7Address address, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 1);
        return new S7ReadItem(address with { BitOffset = 0 }, byteCount, isBitAccess: false);
    }

    /// <summary>构造一个按位读取的数据项。</summary>
    public static S7ReadItem Bit(S7Address address)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address.BitOffset, (byte)7);
        return new S7ReadItem(address, 1, isBitAccess: true);
    }
}
