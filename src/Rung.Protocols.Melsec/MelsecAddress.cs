using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.Melsec;

/// <summary>
/// MELSEC 软元件。
/// <para>
/// 枚举值就是 MC 3E 二进制帧里的软元件代码，便于对照报文。
/// </para>
/// </summary>
public enum MelsecDevice : byte
{
    /// <summary>输入继电器 X，<b>十六进制</b>编号。</summary>
    X = 0x9C,

    /// <summary>输出继电器 Y，<b>十六进制</b>编号。</summary>
    Y = 0x9D,

    /// <summary>内部继电器 M，十进制编号。</summary>
    M = 0x90,

    /// <summary>锁存继电器 L，十进制编号。</summary>
    L = 0x92,

    /// <summary>报警器 F，十进制编号。</summary>
    F = 0x93,

    /// <summary>链接继电器 B，<b>十六进制</b>编号。</summary>
    B = 0xA0,

    /// <summary>数据寄存器 D，十进制编号。</summary>
    D = 0xA8,

    /// <summary>链接寄存器 W，<b>十六进制</b>编号。</summary>
    W = 0xB4,

    /// <summary>文件寄存器 R，十进制编号。</summary>
    R = 0xAF,

    /// <summary>扩展文件寄存器 ZR，<b>十六进制</b>编号。</summary>
    ZR = 0xB0,

    /// <summary>定时器当前值 TN，十进制编号。</summary>
    TN = 0xC2,

    /// <summary>计数器当前值 CN，十进制编号。</summary>
    CN = 0xC5,
}

/// <summary>与 <see cref="MelsecDevice"/> 相关的判定。</summary>
public static class MelsecDeviceExtensions
{
    /// <summary>
    /// 该软元件的编号是<b>十六进制</b>还是十进制。
    /// <para>
    /// 这是三菱接入时最容易踩、也最难自己发现的坑：X/Y/B/W/ZR 的编号是十六进制，
    /// 所以 <c>X10</c> 指的是第 16 点而不是第 10 点。地址表上写着 X10，
    /// 按十进制去读会读到隔壁的点，值看着"像那么回事"但就是不对。
    /// </para>
    /// </summary>
    public static bool IsHexadecimal(this MelsecDevice device)
        => device is MelsecDevice.X or MelsecDevice.Y or MelsecDevice.B
            or MelsecDevice.W or MelsecDevice.ZR;

    /// <summary>该软元件是位软元件（继电器）还是字软元件（寄存器）。</summary>
    public static bool IsBitDevice(this MelsecDevice device)
        => device is MelsecDevice.X or MelsecDevice.Y or MelsecDevice.M
            or MelsecDevice.L or MelsecDevice.F or MelsecDevice.B;
}

/// <summary>解析后的 MELSEC 地址。</summary>
/// <param name="Device">软元件。</param>
/// <param name="Number">软元件编号，已按各自的进制解析成数值。</param>
public readonly record struct MelsecAddress(MelsecDevice Device, int Number)
{
    /// <summary>该地址是否指向位软元件。</summary>
    public bool IsBit => Device.IsBitDevice();

    /// <inheritdoc/>
    public override string ToString()
        => Device.IsHexadecimal()
            ? FormattableString.Invariant($"{Device}{Number:X}")
            : FormattableString.Invariant($"{Device}{Number}");

    /// <summary>返回偏移若干个软元件之后的地址。</summary>
    public MelsecAddress AtOffset(int delta) => this with { Number = Number + delta };
}

/// <summary>
/// MELSEC 地址解析。
/// <para>
/// 支持 <c>D100</c>、<c>M200</c>、<c>X1F</c>、<c>W1A0</c>、<c>ZR3000</c> 等写法。
/// <b>X/Y/B/W/ZR 按十六进制解析，其余按十进制</b>——这是协议规定，不是可选项。
/// </para>
/// </summary>
public static class MelsecAddressParser
{
    /// <summary>解析地址，失败时抛出 <see cref="AddressFormatException"/>。</summary>
    public static MelsecAddress Parse(string address)
        => TryParse(address, out var result, out var reason)
            ? result
            : throw new AddressFormatException(address, reason);

    /// <summary>尝试解析地址。</summary>
    public static bool TryParse(string? address, out MelsecAddress result, out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            failureReason = "地址为空";
            return false;
        }

        var text = address.Trim().ToUpperInvariant();

        // 两字母的软元件要先匹配，否则 ZR100 会被当成 Z + R100
        var (device, prefixLength) = text switch
        {
            _ when text.StartsWith("ZR", StringComparison.Ordinal) => (MelsecDevice.ZR, 2),
            _ when text.StartsWith("TN", StringComparison.Ordinal) => (MelsecDevice.TN, 2),
            _ when text.StartsWith("CN", StringComparison.Ordinal) => (MelsecDevice.CN, 2),
            _ when text.StartsWith('X') => (MelsecDevice.X, 1),
            _ when text.StartsWith('Y') => (MelsecDevice.Y, 1),
            _ when text.StartsWith('M') => (MelsecDevice.M, 1),
            _ when text.StartsWith('L') => (MelsecDevice.L, 1),
            _ when text.StartsWith('F') => (MelsecDevice.F, 1),
            _ when text.StartsWith('B') => (MelsecDevice.B, 1),
            _ when text.StartsWith('D') => (MelsecDevice.D, 1),
            _ when text.StartsWith('W') => (MelsecDevice.W, 1),
            _ when text.StartsWith('R') => (MelsecDevice.R, 1),
            _ => ((MelsecDevice)0, 0),
        };

        if (prefixLength == 0)
        {
            failureReason = $"未知的软元件前缀 \"{text[0]}\"，"
                + "可用：X / Y / M / L / F / B / D / W / R / ZR / TN / CN";
            return false;
        }

        var digits = text[prefixLength..];
        if (digits.Length == 0)
        {
            failureReason = "缺少软元件编号";
            return false;
        }

        var hex = device.IsHexadecimal();
        var style = hex ? NumberStyles.HexNumber : NumberStyles.None;

        if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            failureReason = hex
                ? $"软元件 {device} 的编号是十六进制，\"{digits}\" 不是有效的十六进制数"
                : $"软元件 {device} 的编号是十进制，\"{digits}\" 不是有效的非负整数";

            return false;
        }

        // 3E 帧里的起始软元件号只有 3 字节
        if (number > 0xFFFFFF)
        {
            failureReason = $"软元件编号 {number} 超出 MC 3E 帧的 3 字节上限";
            return false;
        }

        result = new MelsecAddress(device, number);
        return true;
    }
}
