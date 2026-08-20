using Rung.Abstractions;
using Xunit;

namespace Rung.Core.Tests;

/// <summary>
/// 调度循环与重连状态机的测试。
/// 断线、慢响应、写失败这些平时要拔网线才能复现的场景，这里都是确定性的。
/// </summary>
public class DeviceWorkerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>给任意等待加上超时上限，避免逻辑写错时测试挂死而不是失败。</summary>
    private static Task Bounded(Task task)
        => task.WaitAsync(Patience, TestContext.Current.CancellationToken);

    private static DeviceOptions Device => new()
    {
        DeviceId = "dev1",
        Protocol = "fake",
        Host = "127.0.0.1",
        TimeoutMs = 1000,
    };

    private static TagDef Tag(string name, string pollGroup = "default", double deadband = 0)
        => new()
        {
            Name = name,
            Address = "DB1.DBW0",
            DataType = TagDataType.Int32,
            PollGroup = pollGroup,
            Deadband = deadband,
            Access = TagAccess.ReadWrite,
        };

    /// <summary>测试用参数：周期和退避都压到毫秒级，抖动关掉以保证可复现。</summary>
    private static DeviceWorkerOptions FastOptions(TimeSpan? interval = null) => new()
    {
        DefaultPollInterval = interval ?? TimeSpan.FromMilliseconds(20),
        Reconnect = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(10),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            JitterRatio = 0,
        },
    };

    /// <summary>
    /// 跑到 <paramref name="until"/> 满足为止，返回<b>停机之前</b>的状态快照。
    /// RunAsync 正常退出时会把状态置为 Disconnected，所以断言必须取停机前的值。
    /// </summary>
    private static async Task<DeviceStatus> RunUntilAsync(DeviceWorker worker, Func<Task> until)
    {
        using var cancellation = new CancellationTokenSource(Patience);
        var run = worker.RunAsync(cancellation.Token);

        try
        {
            await Bounded(until());
            return worker.Status;
        }
        finally
        {
            await cancellation.CancelAsync();
            await Bounded(run);
        }
    }

    [Fact]
    public async Task 采集结果进入点位缓存()
    {
        var driver = new FakeDriver("dev1") { ValueFactory = static i => 100 + i };
        var cache = new TagCache();
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a"), Tag("b")], cache, options: FastOptions());

        await RunUntilAsync(worker, () => driver.WaitForPollsAsync(1));

        Assert.True(cache.TryGet("a", out var a));
        Assert.True(cache.TryGet("b", out var b));
        Assert.Equal(100, a.Value.AsInt64());
        Assert.Equal(101, b.Value.AsInt64());
    }

    [Fact]
    public async Task 周期性重复采集()
    {
        var driver = new FakeDriver("dev1");
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], new TagCache(), options: FastOptions());

        await RunUntilAsync(worker, () => driver.WaitForPollsAsync(3));

        Assert.True(driver.PollCount >= 3);
    }

    [Fact]
    public async Task 连接失败后按退避重连直到成功()
    {
        // 前三次连接全失败，第四次才通——网关必须自己熬过去，不需要人工介入
        var driver = new FakeDriver("dev1") { FailConnectTimes = 3 };
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], new TagCache(), options: FastOptions());

        var status = await RunUntilAsync(worker, () => driver.WaitForPollsAsync(1));

        Assert.Equal(4, driver.ConnectCount);
        Assert.Equal(DriverState.Connected, status.State);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task 运行中断线后自动恢复采集()
    {
        var driver = new FakeDriver("dev1") { FailPollAtCount = 2 };
        var cache = new TagCache();
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], cache, options: FastOptions());

        using var cancellation = new CancellationTokenSource(Patience);
        var run = worker.RunAsync(cancellation.Token);

        // 第 2 轮采集抛异常，随后重连；解除故障开关后应当恢复
        await Bounded(driver.WaitForPollsAsync(2));
        driver.FailPollAtCount = 0;
        await Bounded(driver.WaitForPollsAsync(4));

        await cancellation.CancelAsync();
        await Bounded(run);

        Assert.True(driver.ConnectCount >= 2, "应当发生过重连");
        Assert.True(worker.Status.ReconnectCount >= 1);
    }

    [Fact]
    public async Task 断线时缓存降级为陈旧但保留最后已知值()
    {
        var driver = new FakeDriver("dev1") { ValueFactory = static _ => 235 };
        var cache = new TagCache();
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("temp")], cache, options: FastOptions());

        using var cancellation = new CancellationTokenSource(Patience);
        var run = worker.RunAsync(cancellation.Token);

        await Bounded(driver.WaitForPollsAsync(1));
        driver.FailPollAtCount = driver.PollCount + 1;

        // 等到状态确实变成故障
        while (worker.Status.State != DriverState.Faulted && !cancellation.IsCancellationRequested)
        {
            await Task.Delay(5, cancellation.Token);
        }

        await cancellation.CancelAsync();
        await Bounded(run);

        Assert.True(cache.TryGet("temp", out var snapshot));
        Assert.Equal(TagQuality.Stale, snapshot.Value.Quality);
        Assert.Equal(235, snapshot.Value.AsInt64());
        Assert.NotNull(worker.Status.LastError);
    }

    [Fact]
    public async Task 写命令插队执行()
    {
        var driver = new FakeDriver("dev1");
        var tag = Tag("setpoint");
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [tag], new TagCache(),
            options: FastOptions(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource(Patience);
        var run = worker.RunAsync(cancellation.Token);

        await Bounded(driver.WaitForPollsAsync(1));

        // 采集周期设成了 30 秒；写命令若不插队，这里就会超时
        await Bounded(worker.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int32, 1234, DateTime.UtcNow), cancellation.Token));

        await cancellation.CancelAsync();
        await Bounded(run);

        var write = Assert.Single(driver.Writes);
        Assert.Equal("setpoint", write.Tag.Name);
        Assert.Equal(1234, write.Value.AsInt64());
    }

    [Fact]
    public async Task 写失败的异常传回调用方()
    {
        var driver = new FakeDriver("dev1") { FailWrites = true };
        var tag = Tag("setpoint");
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [tag], new TagCache(), options: FastOptions());

        using var cancellation = new CancellationTokenSource(Patience);
        var run = worker.RunAsync(cancellation.Token);

        await Bounded(driver.WaitForPollsAsync(1));

        await Assert.ThrowsAsync<RungException>(async () => await Bounded(worker.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int32, 1, DateTime.UtcNow), cancellation.Token)));

        await cancellation.CancelAsync();
        await Bounded(run);
    }

    [Fact]
    public async Task 未连接时拒绝写入而不是排队等待()
    {
        // 排队等一个可能永远不来的连接，比直接失败糟糕得多
        var driver = new FakeDriver("dev1");
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], new TagCache(), options: FastOptions());

        var ex = await Assert.ThrowsAsync<RungException>(async () => await worker.WriteAsync(
            Tag("a"), TagValue.FromInteger(TagDataType.Int32, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));

        Assert.Contains("未连接", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置问题反映在设备状态里()
    {
        var driver = new FakeDriver("dev1");
        driver.Issues.Add(new TagIssue(0, "broken", "地址解析失败"));

        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], new TagCache(), options: FastOptions());

        var status = await RunUntilAsync(worker, () => driver.WaitForPollsAsync(1));

        var issue = Assert.Single(status.Issues);
        Assert.Equal("broken", issue.TagName);
    }

    [Fact]
    public async Task 北向输出失败不拖停采集()
    {
        // 采集是第一优先级：Redis 挂了不能反过来把产线数据采集搞停
        var driver = new FakeDriver("dev1");
        var sink = new ThrowingSink();

        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a")], new TagCache(),
            sinks: [sink], options: FastOptions());

        var status = await RunUntilAsync(worker, () => driver.WaitForPollsAsync(3));

        Assert.True(sink.Attempts >= 1);
        Assert.True(driver.PollCount >= 3);
        Assert.Equal(DriverState.Connected, status.State);
    }

    [Fact]
    public async Task 只把越过死区的点位推给北向()
    {
        var driver = new FakeDriver("dev1") { ValueFactory = static _ => 100 };
        var sink = new RecordingSink();

        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("steady")], new TagCache(),
            sinks: [sink], options: FastOptions());

        await RunUntilAsync(worker, () => driver.WaitForPollsAsync(5));

        // 值一直不变，只有首轮该推
        Assert.Single(sink.Batches);
    }

    [Fact]
    public async Task 不同采集组按各自的周期独立调度()
    {
        var driver = new FakeDriver("dev1");
        var options = new DeviceWorkerOptions
        {
            DefaultPollInterval = TimeSpan.FromSeconds(30),
            PollGroupIntervals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
            {
                ["fast"] = TimeSpan.FromMilliseconds(10),
                ["slow"] = TimeSpan.FromSeconds(30),
            },
            Reconnect = FastOptions().Reconnect,
        };

        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device,
            [Tag("counter", "fast"), Tag("temp", "slow")], new TagCache(), options: options);

        // slow 组只会在启动时采一次；fast 组会跑很多次。
        // 总轮数远超 2，说明两组确实是各走各的
        await RunUntilAsync(worker, () => driver.WaitForPollsAsync(6));

        Assert.Equal(["fast", "slow"], worker.PollGroups);
        Assert.True(driver.PollCount >= 6);
    }

    [Fact]
    public async Task 状态里带上诊断信息()
    {
        var driver = new FakeDriver("dev1");
        await using var worker = new DeviceWorker(
            new FakeDriverFactory(driver), Device, [Tag("a"), Tag("b")], new TagCache(), options: FastOptions());

        var status = await RunUntilAsync(worker, () => driver.WaitForPollsAsync(1));

        Assert.Equal("dev1", status.DeviceId);
        Assert.Equal(240, status.MaxFrameBytes);
        Assert.Equal(2, status.ActiveTagCount);
        Assert.Equal(1, status.RequestCount);
        Assert.NotNull(status.LastSuccessUtc);
    }

    private sealed class ThrowingSink : ITagSink
    {
        public int Attempts { get; private set; }

        public ValueTask PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("模拟 Redis 挂掉");
        }
    }

    private sealed class RecordingSink : ITagSink
    {
        public List<IReadOnlyList<TagSnapshot>> Batches { get; } = [];

        public ValueTask PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken)
        {
            Batches.Add(changed);
            return ValueTask.CompletedTask;
        }
    }
}
