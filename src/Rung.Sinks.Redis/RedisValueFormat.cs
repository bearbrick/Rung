using System.Globalization;
using System.Text.Json;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Sinks.Redis;

/// <summary>
/// 点位值到 Redis 表示的转换。
/// <para>
/// 刻意存成人能直接读懂的文本，而不是二进制或某种紧凑编码：
/// 现场排障时最常用的动作就是 <c>redis-cli HGETALL rung:tag:Line1.Oven.Temp</c>，
/// 那一眼看不懂的话，这个设计就失败了。
/// </para>
/// <para>
/// 全部是纯函数，因此键名方案和取值格式都能脱离 Redis 单独测试。
/// </para>
/// </summary>
public static class RedisValueFormat
{
    /// <summary>值字段名。</summary>
    public const string ValueField = "v";

    /// <summary>质量字段名。</summary>
    public const string QualityField = "q";

    /// <summary>时间戳字段名，ISO-8601 UTC。</summary>
    public const string TimestampField = "t";

    /// <summary>来源设备字段名。</summary>
    public const string DeviceField = "dev";

    /// <summary>协议地址字段名。排障时省去回查配置的一步。</summary>
    public const string AddressField = "addr";

    /// <summary>把采集值格式化成字符串。</summary>
    public static string FormatValue(TagValue value) => value.DataType switch
    {
        TagDataType.Bool => value.AsBool() ? "true" : "false",
        // Float32 必须先窄回 float 再格式化。直接用提升后的 double 走 "R"，
        // 1014.2f 会变成 1014.2000122070312——数值没错，但没人愿意在 redis-cli 里看到这个
        TagDataType.Float32 => ((float)value.AsDouble()).ToString("R", CultureInfo.InvariantCulture),
        TagDataType.Float64 => value.AsDouble().ToString("R", CultureInfo.InvariantCulture),
        TagDataType.String => value.AsString(),
        TagDataType.Bytes => Convert.ToHexString(value.AsBytes()),
        TagDataType.UInt64 => ((ulong)value.AsInt64()).ToString(CultureInfo.InvariantCulture),
        _ => value.AsInt64().ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>时间戳一律 UTC，带 Z 后缀。展示层负责转本地时区。</summary>
    public static string FormatTimestamp(DateTime timestampUtc)
        => timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>构造 Pub/Sub 的消息体。</summary>
    public static string BuildChangeMessage(TagSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 字段名取短，是因为这条消息在高频点位上每秒会发很多次
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["n"] = snapshot.Tag.Name,
            [ValueField] = FormatValue(snapshot.Value),
            [QualityField] = snapshot.Value.Quality.ToString(),
            [TimestampField] = FormatTimestamp(snapshot.Value.TimestampUtc),
            [DeviceField] = snapshot.DeviceId,
        };

        return JsonSerializer.Serialize(payload);
    }
}
