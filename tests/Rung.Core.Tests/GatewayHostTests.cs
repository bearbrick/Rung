using Rung.Abstractions;
using Xunit;

namespace Rung.Core.Tests;

/// <summary>多设备编排的测试。核心要验证的是<b>隔离</b>：一台设备出问题不能影响其他设备。</summary>
public class GatewayHostTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static Task Bounded(Task task)
        => task.WaitAsync(Patience, TestContext.Current.CancellationToken);

    private static DeviceOptions Device(string id) => new()
    {
        DeviceId = id,
        Protocol = "fake",
        Host = "127.0.0.1",
        TimeoutMs = 1000,
    };

    private static TagDef Tag(string name) => new()
    {
        Name = name,
        Address = "DB1.DBW0",
        DataType = TagDataType.Int32,
        Access = TagAccess.ReadWrite,
    };

    private static DeviceWorkerOptions FastOptions => new()
    {
        DefaultPollInterval = TimeSpan.FromMilliseconds(20),
        Reconnect = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(10),
            MaxDelay = TimeSpan.FromMilliseconds(40),
            JitterRatio = 0,
        },
    };

    [Fact]
    public async Task 同时采集多台设备()
    {
        var factory = new MultiDeviceFakeFactory();
        var cache = new TagCache();
        await using var host = new GatewayHost([factory], cache);

        host.AddDevice(Device("oven"), [Tag("Line1.Oven.Temp")], FastOptions);
        host.AddDevice(Device("robot"), [Tag("Line1.Robot.Angle")], FastOptions);
        host.AddDevice(Device("press"), [Tag("Line1.Press.Force")], FastOptions);

        using var cancellation = new CancellationTokenSource(Patience);
        var run = host.RunAsync(cancellation.Token);

        await Bounded(Task.WhenAll(
            factory["oven"].WaitForPollsAsync(1),
            factory["robot"].WaitForPollsAsync(1),
            factory["press"].WaitForPollsAsync(1)));

        Assert.Equal(3, host.DeviceCount);
        Assert.Equal(3, cache.Count);
        Assert.All(host.DeviceStatuses, static s => Assert.Equal(DriverState.Connected, s.State));

        await cancellation.CancelAsync();
        await Bounded(run);
    }

    [Fact]
    public async Task 一台设备掉线不影响其余设备()
    {
        // 产线上总有那么一两台设备状态不好，不能让它们拖垮整个采集服务
        var factory = new MultiDeviceFakeFactory();
        factory["broken"].FailConnectTimes = int.MaxValue;

        var cache = new TagCache();
        await using var host = new GatewayHost([factory], cache);

        host.AddDevice(Device("healthy"), [Tag("Good.Tag")], FastOptions);
        host.AddDevice(Device("broken"), [Tag("Bad.Tag")], FastOptions);

        using var cancellation = new CancellationTokenSource(Patience);
        var run = host.RunAsync(cancellation.Token);

        await Bounded(factory["healthy"].WaitForPollsAsync(3));

        Assert.True(host.TryGetStatus("healthy", out var healthy));
        Assert.True(host.TryGetStatus("broken", out var broken));
        Assert.Equal(DriverState.Connected, healthy.State);
        Assert.Equal(DriverState.Faulted, broken.State);
        Assert.True(broken.ConsecutiveFailures > 0);

        // 好设备的数据照常进缓存，坏设备的点位从未出现过
        Assert.True(cache.TryGet("Good.Tag", out _));
        Assert.False(cache.TryGet("Bad.Tag", out _));

        await cancellation.CancelAsync();
        await Bounded(run);
    }

    [Fact]
    public async Task 写命令按业务名路由到正确的设备()
    {
        // 应用侧只认业务名，不该知道它挂在哪台 PLC 上
        var factory = new MultiDeviceFakeFactory();
        await using var host = new GatewayHost([factory], new TagCache());

        host.AddDevice(Device("oven"), [Tag("Line1.Oven.Setpoint")], FastOptions);
        host.AddDevice(Device("robot"), [Tag("Line1.Robot.Speed")], FastOptions);

        using var cancellation = new CancellationTokenSource(Patience);
        var run = host.RunAsync(cancellation.Token);

        await Bounded(Task.WhenAll(
            factory["oven"].WaitForPollsAsync(1), factory["robot"].WaitForPollsAsync(1)));

        await Bounded(host.WriteAsync(
            "Line1.Robot.Speed", TagValue.FromInteger(TagDataType.Int32, 77, DateTime.UtcNow),
            cancellation.Token));

        Assert.Empty(factory["oven"].Writes);
        var write = Assert.Single(factory["robot"].Writes);
        Assert.Equal(77, write.Value.AsInt64());

        await cancellation.CancelAsync();
        await Bounded(run);
    }

    [Fact]
    public async Task 点位名全局重复时拒绝注册()
    {
        // 重名会让写命令落到错误的设备上，这种事故在产线上代价很大
        await using var host = new GatewayHost([new MultiDeviceFakeFactory()], new TagCache());

        host.AddDevice(Device("a"), [Tag("Shared.Name")], FastOptions);

        var ex = Assert.Throws<RungException>(
            () => host.AddDevice(Device("b"), [Tag("Shared.Name")], FastOptions));

        Assert.Contains("全局唯一", ex.Message, StringComparison.Ordinal);

        // 注册失败不能留下半个设备
        Assert.Equal(1, host.DeviceCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 设备标识重复时拒绝注册()
    {
        await using var host = new GatewayHost([new MultiDeviceFakeFactory()], new TagCache());

        host.AddDevice(Device("dup"), [Tag("A")], FastOptions);

        Assert.Throws<RungException>(() => host.AddDevice(Device("dup"), [Tag("B")], FastOptions));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 未注册的协议给出已知协议列表()
    {
        await using var host = new GatewayHost([new MultiDeviceFakeFactory()], new TagCache());

        var ex = Assert.Throws<RungException>(() => host.AddDevice(
            Device("x") with { Protocol = "modbus-tcp" }, [Tag("A")], FastOptions));

        Assert.Contains("modbus-tcp", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fake", ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 写入未知点位名被拒绝()
    {
        await using var host = new GatewayHost([new MultiDeviceFakeFactory()], new TagCache());
        host.AddDevice(Device("a"), [Tag("Known")], FastOptions);

        var ex = await Assert.ThrowsAsync<RungException>(async () => await host.WriteAsync(
            "Unknown", TagValue.FromInteger(TagDataType.Int32, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));

        Assert.Contains("Unknown", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 没有设备时启动被拒绝()
    {
        await using var host = new GatewayHost([new MultiDeviceFakeFactory()], new TagCache());

        await Assert.ThrowsAsync<RungException>(
            async () => await host.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 运行中不允许增删设备()
    {
        var factory = new MultiDeviceFakeFactory();
        await using var host = new GatewayHost([factory], new TagCache());
        host.AddDevice(Device("a"), [Tag("A")], FastOptions);

        using var cancellation = new CancellationTokenSource(Patience);
        var run = host.RunAsync(cancellation.Token);

        await Bounded(factory["a"].WaitForPollsAsync(1));

        Assert.Throws<RungException>(() => host.AddDevice(Device("b"), [Tag("B")], FastOptions));

        await cancellation.CancelAsync();
        await Bounded(run);
    }
}
