using Rung.Abstractions;
using Rung.Core;
using Xunit;

namespace Rung.Host.Tests;

/// <summary>暴露格式是给抓取端看的契约，格式细节值得单独测。</summary>
public class PrometheusFormatterTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 8, 0, 0, DateTimeKind.Utc);

    private static DeviceStatus Device(string id, DriverState state = DriverState.Connected) => new()
    {
        DeviceId = id,
        State = state,
        ActiveTagCount = 12,
        RequestCount = 3,
        ReconnectCount = 2,
        OverrunCount = 1,
        LastPollDuration = TimeSpan.FromMilliseconds(12.5),
        LastSuccessUtc = Now.AddSeconds(-4),
    };

    private static string Render(params DeviceStatus[] devices)
        => PrometheusFormatter.Render(devices, new TagCache(), Now);

    [Fact]
    public void 每个指标都带类型与说明()
    {
        // 少了 # TYPE，Prometheus 会把 counter 当 untyped，rate() 就不能用了
        var text = Render(Device("oven"));

        Assert.Contains("# HELP rung_device_up", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE rung_device_up gauge", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE rung_device_reconnects_total counter", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 连接状态渲染成零一()
    {
        var text = Render(Device("up1"), Device("down1", DriverState.Faulted));

        Assert.Contains("rung_device_up{device=\"up1\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("rung_device_up{device=\"down1\"} 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 耗时以秒为单位()
    {
        // Prometheus 的约定是基本单位，毫秒会让所有查询都得手工换算
        Assert.Contains("rung_device_poll_duration_seconds{device=\"oven\"} 0.0125",
            Render(Device("oven")), StringComparison.Ordinal);
    }

    [Fact]
    public void 上次成功采集渲染成距今秒数()
    {
        Assert.Contains("rung_device_last_success_age_seconds{device=\"oven\"} 4",
            Render(Device("oven")), StringComparison.Ordinal);
    }

    [Fact]
    public void 从未成功过时用负一表示()
    {
        // 用 -1 而不是 0：0 会被误读成"刚刚才采过"，正好反了
        var device = Device("new") with { LastSuccessUtc = null };

        Assert.Contains("rung_device_last_success_age_seconds{device=\"new\"} -1",
            Render(device), StringComparison.Ordinal);
    }

    [Fact]
    public void 标签值里的引号和反斜杠被转义()
    {
        // 不转义会破坏整个抓取结果，而不只是这一行
        var text = Render(Device("odd\"name\\x"));

        Assert.Contains("device=\"odd\\\"name\\\\x\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 数值用不变文化格式化()
    {
        // 在 de-DE 之类的区域下，小数点会变成逗号，抓取端直接解析失败
        var text = Render(Device("oven"));

        Assert.DoesNotContain("0,0125", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 点位统计反映缓存内容()
    {
        var cache = new TagCache();
        TagDef[] tags =
        [
            new() { Name = "a", Address = "X", DataType = TagDataType.Int32 },
            new() { Name = "b", Address = "X", DataType = TagDataType.Int32 },
        ];

        cache.Update("dev", tags,
        [
            TagValue.FromInteger(TagDataType.Int32, 1, Now),
            TagValue.Bad(TagDataType.Int32, TagQuality.CommFailure, Now),
        ]);

        var text = PrometheusFormatter.Render([], cache, Now);

        Assert.Contains("rung_tags_total 2", text, StringComparison.Ordinal);
        Assert.Contains("rung_tags_good 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有设备时仍然输出进程存活指标()
    {
        var text = PrometheusFormatter.Render([], new TagCache(), Now);

        Assert.Contains("rung_up 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 每一行都以换行结尾()
    {
        // 最后一行缺换行会让部分抓取端丢掉它
        Assert.EndsWith("\n", Render(Device("oven")), StringComparison.Ordinal);
    }
}
