using Rung.Abstractions;
using Rung.Core;
using Xunit;

namespace Rung.Sinks.Redis.Tests;

/// <summary>取值格式是对外契约的一部分：应用侧要照着它解析，不能随意变。</summary>
public class RedisValueFormatTests
{
    private static readonly DateTime Moment = new(2026, 8, 19, 5, 30, 15, 250, DateTimeKind.Utc);

    [Fact]
    public void 布尔值存成小写英文()
    {
        Assert.Equal("true", RedisValueFormat.FormatValue(TagValue.FromBool(true, Moment)));
        Assert.Equal("false", RedisValueFormat.FormatValue(TagValue.FromBool(false, Moment)));
    }

    [Fact]
    public void 整数按不变文化格式化()
        => Assert.Equal("1234",
            RedisValueFormat.FormatValue(TagValue.FromInteger(TagDataType.Int32, 1234, Moment)));

    [Fact]
    public void 六十四位无符号数不被写成负数()
    {
        var value = TagValue.FromInteger(TagDataType.UInt64, unchecked((long)ulong.MaxValue), Moment);

        Assert.Equal("18446744073709551615", RedisValueFormat.FormatValue(value));
    }

    [Fact]
    public void 浮点数往返不丢精度()
    {
        // 用 R 格式：0.1 + 0.2 这类值若被截断，应用侧算出来的结果就和网关对不上
        var original = 1013.2500001;
        var text = RedisValueFormat.FormatValue(TagValue.FromDouble(original, Moment));

        Assert.Equal(original, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 单精度浮点不带提升后的尾巴()
    {
        // 直接用提升成 double 的值走 "R"，1014.2f 会变成 1014.2000122070312。
        // 数值没错，但这是对外契约，没人愿意在 redis-cli 里看到这个
        var value = TagValue.FromSingle(1014.2f, Moment);

        Assert.Equal("1014.2", RedisValueFormat.FormatValue(value));
    }

    [Fact]
    public void 字节数组存成十六进制()
        => Assert.Equal("DEADBEEF",
            RedisValueFormat.FormatValue(TagValue.FromBytes([0xDE, 0xAD, 0xBE, 0xEF], Moment)));

    [Fact]
    public void 时间戳带Z后缀且为UTC()
        => Assert.Equal("2026-08-19T05:30:15.250Z", RedisValueFormat.FormatTimestamp(Moment));

    [Fact]
    public void 变化消息包含应用侧需要的全部字段()
    {
        var tag = new TagDef
        {
            Name = "Line1.Oven.Temp",
            Address = "DB1.DBW0",
            DataType = TagDataType.Float64,
        };
        var snapshot = new TagSnapshot("oven", tag, TagValue.FromDouble(235.4, Moment));

        var json = RedisValueFormat.BuildChangeMessage(snapshot);

        Assert.Contains("\"n\":\"Line1.Oven.Temp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"v\":\"235.4\"", json, StringComparison.Ordinal);
        Assert.Contains("\"q\":\"Good\"", json, StringComparison.Ordinal);
        Assert.Contains("2026-08-19T05:30:15.250Z", json, StringComparison.Ordinal);
        Assert.Contains("\"dev\":\"oven\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rung", "Line1.Oven.Temp", "rung:tag:Line1.Oven.Temp")]
    [InlineData("factory-a", "X", "factory-a:tag:X")]
    public void 键名方案(string prefix, string tagName, string expected)
        => Assert.Equal(expected, new RedisSinkOptions { KeyPrefix = prefix }.TagKey(tagName));

    [Fact]
    public void 频道名默认跟随前缀()
    {
        Assert.Equal("rung:changes", new RedisSinkOptions().ResolvedChannel);
        Assert.Equal("custom", new RedisSinkOptions { ChannelName = "custom" }.ResolvedChannel);
    }
}
