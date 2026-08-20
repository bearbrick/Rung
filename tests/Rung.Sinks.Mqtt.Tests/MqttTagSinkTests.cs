using Rung.Abstractions;
using Rung.Core;
using Xunit;

namespace Rung.Sinks.Mqtt.Tests;

/// <summary>端到端测试：真实的 MQTTnet 客户端对进程内 broker。</summary>
public class MqttTagSinkTests
{
    private static readonly DateTime Moment = new(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc);

    private static TagSnapshot Snapshot(string name, TagValue value, string device = "oven")
        => new(device, new TagDef { Name = name, Address = "DB1.DBW0", DataType = value.DataType }, value);

    private static async Task<MqttTagSink> ConnectAsync(TestBroker broker, MqttSinkOptions? options = null)
        => await MqttTagSink.ConnectAsync(
            (options ?? new MqttSinkOptions()) with { Host = "127.0.0.1", Port = broker.Port });

    [Fact]
    public async Task 连接后宣告上线()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        Assert.True(sink.IsConnected);

        var status = await broker.WaitForAsync("rung/status", TestContext.Current.CancellationToken);
        Assert.Equal("online", status.Payload);
        Assert.True(status.Retain);
    }

    [Fact]
    public async Task 异常断线时broker代发下线()
    {
        // 没有遗嘱，订阅方无法区分「值一直没变」和「网关早就死了」——
        // 这两件事在产线上的处置方式完全相反。
        // 这里直接验行为：强制踢掉客户端，看 broker 是否真的代发 offline
        await using var broker = await TestBroker.StartAsync();
        var sink = await ConnectAsync(broker);

        await broker.KillClientAsync();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (broker.Last("rung/status")?.Payload == "offline")
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("异常断线之后 broker 应当代发遗嘱消息 offline");
    }

    [Fact]
    public async Task 点位发布到约定主题且字段齐全()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        await sink.PublishAsync(
            [Snapshot("Line1.Oven.Temp", TagValue.FromDouble(235.4, Moment))],
            TestContext.Current.CancellationToken);

        var message = await broker.WaitForAsync(
            "rung/tag/Line1.Oven.Temp", TestContext.Current.CancellationToken);

        var payload = message.Payload;
        Assert.Contains("\"v\":235.4", payload, StringComparison.Ordinal);
        Assert.Contains("\"q\":\"Good\"", payload, StringComparison.Ordinal);
        Assert.Contains("2026-08-20T06:00:00.000Z", payload, StringComparison.Ordinal);
        Assert.Contains("\"dev\":\"oven\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"addr\":\"DB1.DBW0\"", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 点位默认以保留消息发布()
    {
        // 这是 MQTT 侧对应 Redis 缓存的机制：新订阅者一连上就拿到最后已知值，
        // 不必干等到下一次变化。对几分钟才动一次的温度量，这个等待不可接受
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 1, Moment))],
            TestContext.Current.CancellationToken);

        Assert.True((await broker.WaitForAsync("rung/tag/A", TestContext.Current.CancellationToken)).Retain);
    }

    [Fact]
    public async Task 保留标志可以关掉()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker, new MqttSinkOptions { RetainTags = false });

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 1, Moment))],
            TestContext.Current.CancellationToken);

        Assert.False((await broker.WaitForAsync("rung/tag/A", TestContext.Current.CancellationToken)).Retain);
    }

    [Fact]
    public async Task 主题前缀可配()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker, new MqttSinkOptions { TopicPrefix = "factory-a" });

        await sink.PublishAsync(
            [Snapshot("A", TagValue.FromInteger(TagDataType.Int32, 1, Moment))],
            TestContext.Current.CancellationToken);

        await broker.WaitForAsync("factory-a/tag/A", TestContext.Current.CancellationToken);
        await broker.WaitForAsync("factory-a/status", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task 坏质量的点位也会发出去()
    {
        // 应用侧必须能区分「值是 0」和「读不到所以是 0」
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        await sink.PublishAsync(
            [Snapshot("Broken", TagValue.Bad(TagDataType.Int32, TagQuality.CommFailure, Moment))],
            TestContext.Current.CancellationToken);

        var message = await broker.WaitForAsync("rung/tag/Broken", TestContext.Current.CancellationToken);
        Assert.Contains("\"q\":\"CommFailure\"", message.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 设备状态发布到独立主题()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        await sink.PublishDeviceStatusAsync(
            [new DeviceStatus
            {
                DeviceId = "line1-oven",
                State = DriverState.Connected,
                ActiveTagCount = 12,
                ReconnectCount = 3,
            }],
            TestContext.Current.CancellationToken);

        var payload = (await broker.WaitForAsync(
            "rung/device/line1-oven", TestContext.Current.CancellationToken)).Payload;

        Assert.Contains("\"state\":\"Connected\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"tags\":12", payload, StringComparison.Ordinal);
        Assert.Contains("\"reconnects\":3", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 空批次不产生任何消息()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var sink = await ConnectAsync(broker);

        var before = broker.Messages.Count;
        await sink.PublishAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(before, broker.Messages.Count);
    }

    [Fact]
    public async Task 正常停机时主动发下线()
    {
        // 遗嘱只在异常断线时由 broker 代发，正常停机这条路也得覆盖
        await using var broker = await TestBroker.StartAsync();
        var sink = await ConnectAsync(broker);

        await sink.DisposeAsync();

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (broker.Last("rung/status")?.Payload == "offline")
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail("正常停机时应当主动发 offline");
    }
}
