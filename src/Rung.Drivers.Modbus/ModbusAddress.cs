using System.Globalization;
using Rung.Abstractions;

namespace Rung.Drivers.Modbus;

/// <summary>Modbus 的四类数据区。枚举值取经典地址的首位数字，便于对照。</summary>
public enum ModbusArea : byte
{
    /// <summary>线圈，可读可写的位。</summary>
    Coil = 0,

    /// <summary>离散输入，只读的位。</summary>
    DiscreteInput = 1,

    /// <summary>输入寄存器，只读的 16 位字。</summary>
    InputRegister = 3,

    /// <summary>保持寄存器，可读可写的 16 位字。</summary>
    HoldingRegister = 4,
}

/// <summary>与 <see cref="ModbusArea"/> 相关的判定。</summary>
public static class ModbusAreaExtensions
{
    /// <summary>该区是按位寻址（线圈/离散输入）还是按寄存器寻址。</summary>
    public static bool IsBitArea(this ModbusArea area)
        => area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    /// <summary>该区是否可写。输入寄存器和离散输入在协议上就是只读的。</summary>
    public static bool IsWritable(this ModbusArea area)
        => area is ModbusArea.Coil or ModbusArea.HoldingRegister;
}

/// <summary>
/// 解析后的 Modbus 地址。
/// </summary>
/// <param name="UnitId">从站号。同一条 TCP 连接后面挂多个 RTU 从站时必须区分。</param>
/// <param name="Area">数据区。</param>
/// <param name="Offset">区内偏移，<b>0 基</b>。</param>
/// <param name="BitOffset">寄存器内的位偏移，0-15。</param>
/// <param name="HasBit">地址是否显式指定了位。</param>
public readonly record struct ModbusAddress(
    byte UnitId,
    ModbusArea Area,
    ushort Offset,
    byte BitOffset = 0,
    bool HasBit = false)
{
    /// <inheritdoc/>
    public override string ToString()
    {
        var prefix = Area switch
        {
            ModbusArea.Coil => "CO",
            ModbusArea.DiscreteInput => "DI",
            ModbusArea.InputRegister => "IR",
            _ => "HR",
        };

        var bit = HasBit ? FormattableString.Invariant($".{BitOffset}") : string.Empty;
        return FormattableString.Invariant($"{UnitId}:{prefix}{Offset}{bit}");
    }
}

/// <summary>
/// Modbus 地址解析。
/// <para>
/// 现场的地址表格式五花八门，这里支持两套：显式前缀（推荐，0 基）和经典编号（1 基）。
/// <b>0 基与 1 基混淆是 Modbus 接入时最高频的错误</b>，所以两种写法在语义上
/// 刻意区分得很开——看到 <c>HR0</c> 就知道是 0 基，看到 <c>40001</c> 就知道是 1 基。
/// </para>
/// <list type="bullet">
///   <item><description><c>HR100</c> / <c>IR10</c> / <c>CO5</c> / <c>DI7</c>：0 基，推荐</description></item>
///   <item><description><c>40001</c> / <c>30001</c> / <c>10001</c> / <c>00001</c>：经典 1 基</description></item>
///   <item><description><c>4x0001</c>：同经典 1 基</description></item>
///   <item><description><c>HR100.3</c>：寄存器内的第 3 位</description></item>
///   <item><description><c>3:HR100</c>：指定从站号 3</description></item>
/// </list>
/// </summary>
public static class ModbusAddressParser
{
    /// <summary>解析地址，失败时抛出 <see cref="AddressFormatException"/>。</summary>
    public static ModbusAddress Parse(string address, byte defaultUnitId = 1)
        => TryParse(address, defaultUnitId, out var result, out var reason)
            ? result
            : throw new AddressFormatException(address, reason);

    /// <summary>尝试解析地址。</summary>
    public static bool TryParse(
        string? address,
        byte defaultUnitId,
        out ModbusAddress result,
        out string failureReason)
    {
        result = default;
        failureReason = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            failureReason = "地址为空";
            return false;
        }

        var text = address.Trim().ToUpperInvariant();
        var unitId = defaultUnitId;

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            if (!byte.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out unitId))
            {
                failureReason = "从站号不是 0-255 的整数";
                return false;
            }

            text = text[(colon + 1)..];
        }

        byte bitOffset = 0;
        var hasBit = false;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!byte.TryParse(text[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out bitOffset)
                || bitOffset > 15)
            {
                failureReason = "位偏移必须是 0-15";
                return false;
            }

            hasBit = true;
            text = text[..dot];
        }

        if (!TryParseAreaAndOffset(text, out var area, out var offset, out failureReason))
        {
            return false;
        }

        if (hasBit && area.IsBitArea())
        {
            failureReason = "线圈和离散输入本身就是位，不应再带位偏移";
            return false;
        }

        result = new ModbusAddress(unitId, area, offset, bitOffset, hasBit);
        return true;
    }

    private static bool TryParseAreaAndOffset(
        string text,
        out ModbusArea area,
        out ushort offset,
        out string failureReason)
    {
        area = ModbusArea.HoldingRegister;
        offset = 0;
        failureReason = string.Empty;

        if (text.Length < 2)
        {
            failureReason = "地址过短";
            return false;
        }

        // 显式前缀，0 基
        var prefix = text[..2];
        var explicitArea = prefix switch
        {
            "HR" => ModbusArea.HoldingRegister,
            "IR" => ModbusArea.InputRegister,
            "CO" => ModbusArea.Coil,
            "DI" => ModbusArea.DiscreteInput,
            _ => (ModbusArea?)null,
        };

        if (explicitArea is { } known)
        {
            if (!ushort.TryParse(text[2..], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                failureReason = "偏移不是 0-65535 的整数";
                return false;
            }

            area = known;
            return true;
        }

        // 4x0001 形式，去掉 x 之后按经典编号处理
        var digits = text.Length > 1 && text[1] == 'X' ? text[0] + text[2..] : text;

        if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var classic))
        {
            failureReason = $"无法识别的地址前缀 \"{text[..Math.Min(2, text.Length)]}\"，"
                + "可用 HR/IR/CO/DI，或经典编号如 40001";
            return false;
        }

        // 经典编号：首位是区号，其余是 1 基偏移
        var leading = digits[0];
        var body = digits[1..];

        area = leading switch
        {
            '0' => ModbusArea.Coil,
            '1' => ModbusArea.DiscreteInput,
            '3' => ModbusArea.InputRegister,
            '4' => ModbusArea.HoldingRegister,
            _ => ModbusArea.HoldingRegister,
        };

        if (leading is not ('0' or '1' or '3' or '4'))
        {
            failureReason = $"经典编号的首位必须是 0/1/3/4，实际是 {leading}";
            return false;
        }

        if (!ulong.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var oneBased)
            || oneBased == 0)
        {
            failureReason = "经典编号是 1 基的，偏移部分不能为 0";
            return false;
        }

        if (oneBased - 1 > ushort.MaxValue)
        {
            failureReason = $"偏移 {oneBased - 1} 超出 Modbus 地址空间（最大 65535）";
            return false;
        }

        _ = classic;
        offset = (ushort)(oneBased - 1);
        return true;
    }
}
