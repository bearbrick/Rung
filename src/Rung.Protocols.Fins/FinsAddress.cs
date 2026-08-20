using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.Fins;

/// <summary>欧姆龙的存储区。</summary>
public enum FinsArea : byte
{
    /// <summary>CIO / IR 区，输入输出继电器。</summary>
    Cio = 0,

    /// <summary>W 区，内部工作继电器。</summary>
    Work = 1,

    /// <summary>H 区，保持继电器，断电保持。</summary>
    Holding = 2,

    /// <summary>A 区，辅助继电器，多为只读的系统状态。</summary>
    Auxiliary = 3,

    /// <summary>DM 区，数据存储器。现场用得最多的一块。</summary>
    Dm = 4,
}

/// <summary>存储区到 FINS 存储区代码的映射。</summary>
public static class FinsAreaExtensions
{
    /// <summary>按字访问时的存储区代码。</summary>
    public static byte WordCode(this FinsArea area) => area switch
    {
        FinsArea.Cio => 0xB0,
        FinsArea.Work => 0xB1,
        FinsArea.Holding => 0xB2,
        FinsArea.Auxiliary => 0xB3,
        FinsArea.Dm => 0x82,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的存储区"),
    };

    /// <summary>按位访问时的存储区代码。FINS 的位访问用的是另一套代码。</summary>
    public static byte BitCode(this FinsArea area) => area switch
    {
        FinsArea.Cio => 0x30,
        FinsArea.Work => 0x31,
        FinsArea.Holding => 0x32,
        FinsArea.Auxiliary => 0x33,
        FinsArea.Dm => 0x02,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的存储区"),
    };

    /// <summary>该区是否可写。A 区多为系统状态，整体按只读处理。</summary>
    public static bool IsWritable(this FinsArea area) => area != FinsArea.Auxiliary;
}

/// <summary>
/// 解析后的 FINS 地址。
/// </summary>
/// <param name="Area">存储区。</param>
/// <param name="Word">字地址。</param>
/// <param name="Bit">位号，0-15。</param>
/// <param name="HasBit">地址是否显式指定了位。</param>
public readonly record struct FinsAddress(FinsArea Area, int Word, byte Bit = 0, bool HasBit = false)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var prefix = Area switch
        {
            FinsArea.Cio => "CIO",
            FinsArea.Work => "W",
            FinsArea.Holding => "H",
            FinsArea.Auxiliary => "A",
            _ => "D",
        };

        return HasBit
            ? FormattableString.Invariant($"{prefix}{Word}.{Bit:00}")
            : FormattableString.Invariant($"{prefix}{Word}");
    }

    /// <summary>返回偏移若干个字之后的地址。</summary>
    public FinsAddress AtWordOffset(int delta) => this with { Word = Word + delta };
}

/// <summary>
/// FINS 地址解析。
/// <para>
/// 支持 <c>D100</c>、<c>D100.05</c>、<c>CIO200</c>、<c>W10.03</c>、<c>H5</c>、<c>A50</c>。
/// 全部按<b>十进制</b>——欧姆龙这点比三菱省心，不存在按软元件分进制的坑。
/// </para>
/// <para>
/// 位号写成两位（<c>D100.05</c>）是欧姆龙手册里的惯例，但一位也接受。
/// </para>
/// </summary>
public static class FinsAddressParser
{
    /// <summary>解析地址，失败时抛出 <see cref="AddressFormatException"/>。</summary>
    public static FinsAddress Parse(string address)
        => TryParse(address, out var result, out var reason)
            ? result
            : throw new AddressFormatException(address, reason);

    /// <summary>尝试解析地址。</summary>
    public static bool TryParse(string? address, out FinsAddress result, out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            failureReason = "地址为空";
            return false;
        }

        var text = address.Trim().ToUpperInvariant();

        // CIO 要先匹配，否则 C 开头会被误判
        var (area, prefixLength) = text switch
        {
            _ when text.StartsWith("CIO", StringComparison.Ordinal) => (FinsArea.Cio, 3),
            _ when text.StartsWith('D') => (FinsArea.Dm, 1),
            _ when text.StartsWith('W') => (FinsArea.Work, 1),
            _ when text.StartsWith('H') => (FinsArea.Holding, 1),
            _ when text.StartsWith('A') => (FinsArea.Auxiliary, 1),
            _ when char.IsAsciiDigit(text[0]) => (FinsArea.Cio, 0), // 裸数字按 CIO 处理，欧姆龙的习惯写法
            _ => ((FinsArea)255, -1),
        };

        if (prefixLength < 0)
        {
            failureReason = $"未知的存储区前缀 \"{text[0]}\"，可用：D / CIO / W / H / A";
            return false;
        }

        var rest = text[prefixLength..];
        if (rest.Length == 0)
        {
            failureReason = "缺少字地址";
            return false;
        }

        byte bit = 0;
        var hasBit = false;

        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!byte.TryParse(rest[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out bit)
                || bit > 15)
            {
                failureReason = "位号必须是 0-15";
                return false;
            }

            hasBit = true;
            rest = rest[..dot];
        }

        if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var word))
        {
            failureReason = "字地址不是有效的非负整数";
            return false;
        }

        // FINS 的地址字段是 2 字节
        if (word > ushort.MaxValue)
        {
            failureReason = $"字地址 {word} 超出 FINS 的 2 字节上限";
            return false;
        }

        result = new FinsAddress(area, word, bit, hasBit);
        return true;
    }
}
