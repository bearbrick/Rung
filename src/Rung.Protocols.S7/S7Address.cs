using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>
/// 解析后的 S7 地址。这是一个纯值类型，不含任何 IO 或状态——
/// 地址解析在编译读取计划时做一次，采集周期里只搬运它。
/// </summary>
/// <param name="Area">存储区。</param>
/// <param name="DbNumber">数据块号；非 DB 区为 0。</param>
/// <param name="ByteOffset">区内字节偏移。</param>
/// <param name="BitOffset">位偏移，0-7；非位地址为 0。</param>
/// <param name="SizeHint">地址字符串给出的宽度提示，用于配置一致性校验。</param>
public readonly record struct S7Address(
    S7Area Area,
    ushort DbNumber,
    int ByteOffset,
    byte BitOffset,
    S7SizeHint SizeHint = S7SizeHint.None)
{
    /// <summary>
    /// 报文中 Any 指针的 3 字节地址字段值：字节偏移左移 3 位再加上位偏移。
    /// 即便按字节读取，协议要求的仍然是位地址。
    /// </summary>
    public int BitAddress => (ByteOffset << 3) | BitOffset;

    /// <summary>该地址是否指向数据块（DB 或 DI），即 <see cref="DbNumber"/> 是否有意义。</summary>
    public bool IsDataBlock => Area is S7Area.DataBlock or S7Area.InstanceDataBlock;

    /// <summary>
    /// 返回同一区域内偏移 <paramref name="byteDelta"/> 字节后的地址。
    /// 批量合并算法用它把一次大读的结果拆回各个点位。
    /// </summary>
    public S7Address AtOffset(int byteDelta)
        => this with { ByteOffset = ByteOffset + byteDelta, BitOffset = 0, SizeHint = S7SizeHint.None };

    /// <inheritdoc/>
    public override string ToString() => Area switch
    {
        S7Area.DataBlock => FormattableString.Invariant($"DB{DbNumber}.DBX{ByteOffset}.{BitOffset}"),
        S7Area.InstanceDataBlock => FormattableString.Invariant($"DI{DbNumber}.DIX{ByteOffset}.{BitOffset}"),
        S7Area.Input => FormattableString.Invariant($"I{ByteOffset}.{BitOffset}"),
        S7Area.Output => FormattableString.Invariant($"Q{ByteOffset}.{BitOffset}"),
        S7Area.Memory => FormattableString.Invariant($"M{ByteOffset}.{BitOffset}"),
        S7Area.Timer => FormattableString.Invariant($"T{ByteOffset}"),
        S7Area.Counter => FormattableString.Invariant($"C{ByteOffset}"),
        _ => FormattableString.Invariant($"{Area}:{ByteOffset}.{BitOffset}"),
    };
}

/// <summary>
/// S7 地址字符串解析器。
/// <para>
/// 支持西门子常见的几种写法，包括德文助记符（E/A 对应 I/Q）和 S7-200 的 V 区。
/// 解析是纯函数、无分配（除失败信息外），可以放心在配置校验里高频调用。
/// </para>
/// </summary>
public static class S7AddressParser
{
    /// <summary>S7-200 的 V 区在协议上就是 DB1。</summary>
    private const ushort S7200VarBlockNumber = 1;

    /// <summary>
    /// 解析一个 S7 地址，失败时抛出 <see cref="AddressFormatException"/>。
    /// </summary>
    public static S7Address Parse(string address)
        => TryParse(address, out var result, out var reason)
            ? result
            : throw new AddressFormatException(address, reason);

    /// <summary>
    /// 尝试解析一个 S7 地址。
    /// </summary>
    /// <param name="address">地址字符串，大小写不敏感，允许首尾空白。</param>
    /// <param name="result">解析结果。</param>
    /// <param name="failureReason">失败原因；成功时为空字符串。</param>
    public static bool TryParse(string? address, out S7Address result, out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            failureReason = "地址为空";
            return false;
        }

        var span = address.AsSpan().Trim();

        return span[0] is 'D' or 'd' && span.Length > 1 && span[1] is 'B' or 'b'
            ? TryParseDataBlock(span, out result, out failureReason)
            : TryParseSimpleArea(span, out result, out failureReason);
    }

    /// <summary>解析 <c>DB1.DBW10</c> / <c>DB1.DBX0.5</c> / <c>DB1.0.5</c> 形式。</summary>
    private static bool TryParseDataBlock(ReadOnlySpan<char> span, out S7Address result, out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        var dot = span.IndexOf('.');
        if (dot < 0)
        {
            failureReason = "DB 地址缺少 '.' 分隔符，正确写法形如 DB1.DBW10";
            return false;
        }

        if (!TryParseUInt16(span[2..dot], out var dbNumber))
        {
            failureReason = "DB 块号不是有效的无符号整数";
            return false;
        }

        if (dbNumber == 0)
        {
            failureReason = "DB 块号不能为 0";
            return false;
        }

        var rest = span[(dot + 1)..];

        // 兼容 DB1.DBW10 与简写 DB1.10.3 两种形式
        var hint = S7SizeHint.None;
        if (rest.Length > 2 && rest[0] is 'D' or 'd' && rest[1] is 'B' or 'b')
        {
            if (!TryReadSizeLetter(rest[2], out hint))
            {
                failureReason = $"未知的宽度字母 '{rest[2]}'，应为 X/B/W/D 之一";
                return false;
            }

            rest = rest[3..];
        }

        if (!TryParseOffsetAndBit(rest, ref hint, out var byteOffset, out var bitOffset, out failureReason))
        {
            return false;
        }

        result = new S7Address(S7Area.DataBlock, dbNumber, byteOffset, bitOffset, hint);
        return true;
    }

    /// <summary>解析 <c>MW100</c> / <c>I0.0</c> / <c>QX1.3</c> / <c>VD20</c> / <c>T5</c> 形式。</summary>
    private static bool TryParseSimpleArea(ReadOnlySpan<char> span, out S7Address result, out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        var area = char.ToUpperInvariant(span[0]) switch
        {
            'I' or 'E' => S7Area.Input,      // E = Eingang，德文界面导出的地址表里很常见
            'Q' or 'A' => S7Area.Output,     // A = Ausgang
            'M' => S7Area.Memory,
            'V' => S7Area.DataBlock,         // S7-200 的 V 区即 DB1
            'T' => S7Area.Timer,
            'C' or 'Z' => S7Area.Counter,    // Z = Zähler
            'P' => S7Area.Peripheral,
            _ => (S7Area)0,
        };

        if (area == 0)
        {
            failureReason = $"未知的存储区前缀 '{span[0]}'";
            return false;
        }

        var rest = span[1..];
        if (rest.IsEmpty)
        {
            failureReason = "缺少地址偏移";
            return false;
        }

        // 定时器和计数器只有编号，没有宽度和位偏移
        if (area is S7Area.Timer or S7Area.Counter)
        {
            if (!TryParseInt32(rest, out var number))
            {
                failureReason = "定时器/计数器编号不是有效的非负整数";
                return false;
            }

            result = new S7Address(area, 0, number, 0);
            return true;
        }

        var hint = S7SizeHint.None;
        if (TryReadSizeLetter(rest[0], out var parsedHint))
        {
            hint = parsedHint;
            rest = rest[1..];
        }

        if (!TryParseOffsetAndBit(rest, ref hint, out var byteOffset, out var bitOffset, out failureReason))
        {
            return false;
        }

        var dbNumber = char.ToUpperInvariant(span[0]) == 'V' ? S7200VarBlockNumber : (ushort)0;
        result = new S7Address(area, dbNumber, byteOffset, bitOffset, hint);
        return true;
    }

    /// <summary>解析 <c>10</c> 或 <c>10.3</c> 形式的偏移部分。</summary>
    private static bool TryParseOffsetAndBit(
        ReadOnlySpan<char> span,
        ref S7SizeHint hint,
        out int byteOffset,
        out byte bitOffset,
        out string failureReason)
    {
        byteOffset = 0;
        bitOffset = 0;
        failureReason = string.Empty;

        var dot = span.IndexOf('.');
        var bytePart = dot < 0 ? span : span[..dot];

        if (!TryParseInt32(bytePart, out byteOffset))
        {
            failureReason = "字节偏移不是有效的非负整数";
            return false;
        }

        if (dot < 0)
        {
            // 没有位偏移。若宽度提示明确是位，则地址不完整
            if (hint == S7SizeHint.Bit)
            {
                failureReason = "位地址缺少位偏移，正确写法形如 DB1.DBX0.5";
                return false;
            }

            return true;
        }

        if (!TryParseInt32(span[(dot + 1)..], out var bit))
        {
            failureReason = "位偏移不是有效的非负整数";
            return false;
        }

        if (bit > 7)
        {
            failureReason = $"位偏移 {bit} 超出范围，必须是 0-7";
            return false;
        }

        // 明确写成字/双字宽度却又带位偏移，是自相矛盾的配置
        if (hint is S7SizeHint.Word or S7SizeHint.DWord)
        {
            failureReason = "字/双字地址不应带位偏移";
            return false;
        }

        bitOffset = (byte)bit;
        if (hint == S7SizeHint.None)
        {
            hint = S7SizeHint.Bit;
        }

        return true;
    }

    private static bool TryReadSizeLetter(char c, out S7SizeHint hint)
    {
        hint = char.ToUpperInvariant(c) switch
        {
            'X' => S7SizeHint.Bit,
            'B' => S7SizeHint.Byte,
            'W' => S7SizeHint.Word,
            'D' => S7SizeHint.DWord,
            _ => S7SizeHint.None,
        };

        return hint != S7SizeHint.None;
    }

    private static bool TryParseInt32(ReadOnlySpan<char> span, out int value)
        => int.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryParseUInt16(ReadOnlySpan<char> span, out ushort value)
        => ushort.TryParse(span, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
