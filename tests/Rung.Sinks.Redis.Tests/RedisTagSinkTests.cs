using Rung.Abstractions;
using Rung.Core;
using Rung.Simulator;
using Xunit;

namespace Rung.Sinks.Redis.Tests;

/// <summary>
/// 端到端测试：真实的 StackExchange.Redis 客户端，对端是 Rung.Simulator 里的
/// 最小 Redis 服务器。开发机上不需要装 Redis，也不需要 Docker。
/// </summary>
public class RedisTagSinkTests
{
    private static readonly DateTime Moment = new(2026, 8, 19, 6, 0, 0, DateTimeKind.Utc);

    private static TagSnapshot Snapshot(string name, TagValue value, string device = "oven")
        => new(device, new TagDef { Name = name, Address = "DB1.DBW0", DataType = value.DataType }, value);

    private static async Task<(RedisSimulatorServer Server, RedisTagSink Sink)> ConnectAsync(
        RedisSinkOptions? options = null)
    {
        var server = new RedisSimulatorServer();
        var sink = await RedisTagSink.ConnectAsync(
            (options ?? new RedisSinkOptions()) with { ConnectionString = server.ConnectionString });

        return (server, sink);
    }

    [Fact]
    public async Task 点位写入约定的键和字段()
    {
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("Line1.Oven.Temp", TagValue.FromDouble(235.4, Moment))],
            TestContext.Current.CancellationToken);

        var hash = server.GetHash("rung:tag:Line1.Oven.Temp");

        Assert.Equal("235.4", hash["v"]);
        Assert.Equal("Good", hash["q"]);
        Assert.Equal("2026-08-19T06:00:00.000Z", hash["t"]);
        Assert.Equal("oven", hash["dev"]);
        Assert.Equal("DB1.DBW0", hash["addr"]);
    }

    [Fact]
    public async Task 协议地址一并写入省去回查配置()
    {
        // 排障时手头只有 redis-cli，能直接看到地址会省很多事
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        var tag = new TagDef { Name = "T", Address = "DB7.DBD100", DataType = TagDataType.Float32 };
        await sink.PublishAsync(
            [new TagSnapshot("robot", tag, TagValue.FromSingle(1.5f, Moment))],
            TestContext.Current.CancellationToken);

        Assert.Equal("DB7.DBD100", server.GetHash("rung:tag:T")["addr"]);
    }

    [Fact]
    public async Task 变化推送到频道()
    {
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 7, Moment))],
            TestContext.Current.CancellationToken);

        var published = Assert.Single(server.Published());

        Assert.Equal("rung:changes", published.Channel);
        Assert.Contains("\"n\":\"A\"", published.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 可以关掉频道推送()
    {
        var (server, sink) = await ConnectAsync(new RedisSinkOptions { PublishChanges = false });
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 7, Moment))],
            TestContext.Current.CancellationToken);

        Assert.Empty(server.Published());
        Assert.NotEmpty(server.GetHash("rung:tag:A"));
    }

    [Fact]
    public async Task 键前缀可配()
    {
        var (server, sink) = await ConnectAsync(new RedisSinkOptions { KeyPrefix = "factory-a" });
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 1, Moment))],
            TestContext.Current.CancellationToken);

        Assert.Contains("factory-a:tag:A", server.Keys(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task 一批点位走一次往返而不是逐条()
    {
        // 一轮上百个点位，逐条 await 会把延迟放大到不可接受
        var (server, sink) = await ConnectAsync(new RedisSinkOptions { PublishChanges = false });
        await using var _ = server;
        await using var __ = sink;

        var before = server.CommandCount;

        var batch = Enumerable.Range(0, 50)
            .Select(i => Snapshot($"tag{i}", TagValue.FromInteger(TagDataType.Int32, i, Moment)))
            .ToArray();

        await sink.PublishAsync(batch, TestContext.Current.CancellationToken);

        // 50 个点位应当只产生 50 条 HSET，而不是 50 次网络往返；
        // 命令数不该被额外的握手或探活放大
        Assert.Equal(50, server.CommandCount - before);
        Assert.Equal(50, server.Keys().Count(static k => k.StartsWith("rung:tag:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task 空批次不产生任何命令()
    {
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        var before = server.CommandCount;
        await sink.PublishAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(before, server.CommandCount);
    }

    [Fact]
    public async Task 坏质量的点位也会写出去()
    {
        // 应用侧必须能区分"值是 0"和"读不到所以是 0"
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("Broken", TagValue.Bad(TagDataType.Int32, TagQuality.CommFailure, Moment))],
            TestContext.Current.CancellationToken);

        Assert.Equal("CommFailure", server.GetHash("rung:tag:Broken")["q"]);
    }

    [Fact]
    public async Task 设备状态写进独立的键()
    {
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        DeviceStatus[] statuses =
        [
            new()
            {
                DeviceId = "line1-oven",
                State = DriverState.Connected,
                LastSuccessUtc = Moment,
                ActiveTagCount = 12,
                RequestCount = 2,
                ReconnectCount = 3,
            },
        ];

        await sink.PublishDeviceStatusAsync(statuses, TestContext.Current.CancellationToken);

        var hash = server.GetHash("rung:device:line1-oven");

        Assert.Equal("Connected", hash["state"]);
        Assert.Equal("12", hash["tags"]);
        Assert.Equal("2", hash["requests"]);
        Assert.Equal("3", hash["reconnects"]);
        Assert.Equal("2026-08-19T06:00:00.000Z", hash["lastSuccess"]);
    }

    [Fact]
    public async Task 重复推送覆盖同一个键()
    {
        var (server, sink) = await ConnectAsync();
        await using var _ = server;
        await using var __ = sink;

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 1, Moment))],
            TestContext.Current.CancellationToken);
        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 2, Moment.AddSeconds(1)))],
            TestContext.Current.CancellationToken);

        Assert.Equal("2", server.GetHash("rung:tag:A")["v"]);
        Assert.Single(server.Keys());
    }
}
