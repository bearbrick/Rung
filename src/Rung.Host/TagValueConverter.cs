using System.Globalization;
using System.Text.Json;
using Rung.Abstractions;

namespace Rung.Host;

/// <summary>把 REST 请求里的 JSON 值转成点位需要的 <see cref="TagValue"/>。</summary>
public static class TagValueConverter
{
    /// <summary>
    /// 按点位声明的数据类型解释传入的值。
    /// <para>
    /// 严格按 <see cref="TagDef.DataType"/> 走，不做"看起来像什么就当什么"的推断：
    /// 往产线设备上写值，宁可报错也不要猜。
    /// </para>
    /// </summary>
    /// <exception cref="RungException">值无法转成该点位的类型。</exception>
    public static TagValue FromJson(JsonElement element, TagDef tag, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(tag);

        try
        {
            return tag.DataType switch
            {
                TagDataType.Bool => TagValue.FromBool(ReadBool(element), timestampUtc),
                TagDataType.Float32 => TagValue.FromSingle((float)ReadDouble(element), timestampUtc),
                TagDataType.Float64 => TagValue.FromDouble(ReadDouble(element), timestampUtc),
                TagDataType.String => TagValue.FromString(
                    element.GetString() ?? throw new FormatException("期望字符串"), timestampUtc),
                TagDataType.Bytes => TagValue.FromBytes(
                    Convert.FromHexString(element.GetString()
                        ?? throw new FormatException("期望十六进制字符串")), timestampUtc),
                _ when tag.Scale != 1.0 || tag.Offset != 0.0
                    => TagValue.FromDouble(ReadDouble(element), timestampUtc),
                _ => TagValue.FromInteger(tag.DataType, ReadInt64(element), timestampUtc),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new RungException(
                $"无法把 {element} 解释成点位 {tag.Name} 的类型 {tag.DataType}：{ex.Message}", ex);
        }
    }

    private static bool ReadBool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.String => bool.Parse(element.GetString()!),
        _ => throw new FormatException("期望布尔值"),
    };

    private static double ReadDouble(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => double.Parse(element.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new FormatException("期望数值"),
    };

    private static long ReadInt64(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var value)
            ? value
            : (long)Math.Round(element.GetDouble()),
        JsonValueKind.String => long.Parse(element.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new FormatException("期望整数"),
    };
}
