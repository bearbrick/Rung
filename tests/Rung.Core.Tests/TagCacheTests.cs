using Rung.Abstractions;
using Xunit;

namespace Rung.Core.Tests;

public class TagCacheTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 3, 0, 0, DateTimeKind.Utc);

    private static TagDef Tag(string name, double deadband = 0)
        => new() { Name = name, Address = "DB1.DBW0", DataType = TagDataType.Float64, Deadband = deadband };

    private static TagValue Value(double v) => TagValue.FromDouble(v, Now);

    [Fact]
    public void 首次采集全部视为变化()
    {
        var cache = new TagCache();
        TagDef[] tags = [Tag("a"), Tag("b")];

        var changed = cache.Update("dev", tags, [Value(1), Value(2)]);

        Assert.Equal(2, changed.Count);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void 值没变时不推送()
    {
        var cache = new TagCache();
        TagDef[] tags = [Tag("a")];

        cache.Update("dev", tags, [Value(1)]);
        var changed = cache.Update("dev", tags, [Value(1)]);

        Assert.Empty(changed);
    }

    [Fact]
    public void 时间戳每轮都变但读数不变时仍不推送()
    {
        // 变化检测若把时间戳算进去，一个恒定不变的点位会被判成每轮都在变，
        // 死区形同虚设，下游被无意义的推送淹掉
        var cache = new TagCache();
        TagDef[] tags = [Tag("steady")];

        cache.Update("dev", tags, [TagValue.FromDouble(100, Now)]);

        Assert.Empty(cache.Update("dev", tags, [TagValue.FromDouble(100, Now.AddSeconds(1))]));
        Assert.Empty(cache.Update("dev", tags, [TagValue.FromDouble(100, Now.AddSeconds(2))]));
    }

    [Fact]
    public void 死区之内的抖动被抑制()
    {
        var cache = new TagCache();
        TagDef[] tags = [Tag("temp", deadband: 0.5)];

        cache.Update("dev", tags, [Value(100.0)]);

        Assert.Empty(cache.Update("dev", tags, [Value(100.3)]));
        Assert.Empty(cache.Update("dev", tags, [Value(100.4)]));
        Assert.Single(cache.Update("dev", tags, [Value(100.5)]));
    }

    [Fact]
    public void 死区以最近一次推送的值为基准()
    {
        // 若以"上一轮的值"为基准，缓慢漂移的模拟量会永远推不出去——
        // 每次都只差 0.3，但半小时后已经偏了 50 度
        var cache = new TagCache();
        TagDef[] tags = [Tag("temp", deadband: 1.0)];

        cache.Update("dev", tags, [Value(100.0)]);
        cache.Update("dev", tags, [Value(100.6)]);

        Assert.Single(cache.Update("dev", tags, [Value(101.1)]));
    }

    [Fact]
    public void 缓存永远保存最新值即使被死区抑制()
    {
        // Web UI 上要看到真实的当前值，不能是被死区卡住的陈旧值
        var cache = new TagCache();
        TagDef[] tags = [Tag("temp", deadband: 10)];

        cache.Update("dev", tags, [Value(100.0)]);
        cache.Update("dev", tags, [Value(101.0)]);

        Assert.True(cache.TryGet("temp", out var snapshot));
        Assert.Equal(101.0, snapshot.Value.AsDouble());
    }

    [Fact]
    public void 质量变化一定推送不受死区影响()
    {
        var cache = new TagCache();
        TagDef[] tags = [Tag("temp", deadband: 1000)];

        cache.Update("dev", tags, [Value(100.0)]);
        var changed = cache.Update("dev", tags, [TagValue.Bad(TagDataType.Float64, TagQuality.DeviceError, Now)]);

        Assert.Single(changed);
    }

    [Fact]
    public void 断线时保留最后已知值并标记为陈旧()
    {
        // 应用侧读到"5 分钟前的 235 度，质量 Stale"，比读到 null 或 0 有用得多
        var cache = new TagCache();
        TagDef[] tags = [Tag("temp")];

        cache.Update("dev", tags, [Value(235.0)]);
        cache.MarkDeviceStale("dev");

        Assert.True(cache.TryGet("temp", out var snapshot));
        Assert.Equal(TagQuality.Stale, snapshot.Value.Quality);
        Assert.Equal(235.0, snapshot.Value.AsDouble());
    }

    [Fact]
    public void 标记陈旧只影响指定设备()
    {
        var cache = new TagCache();

        cache.Update("dev1", [Tag("a")], [Value(1)]);
        cache.Update("dev2", [Tag("b")], [Value(2)]);
        cache.MarkDeviceStale("dev1");

        Assert.True(cache.TryGet("a", out var stale));
        Assert.True(cache.TryGet("b", out var fresh));
        Assert.Equal(TagQuality.Stale, stale.Value.Quality);
        Assert.Equal(TagQuality.Good, fresh.Value.Quality);
    }

    [Fact]
    public void 快照按名称排序便于展示()
    {
        var cache = new TagCache();

        cache.Update("dev", [Tag("zebra"), Tag("apple")], [Value(1), Value(2)]);

        Assert.Equal(["apple", "zebra"], cache.Snapshot().Select(static s => s.Tag.Name));
    }
}
