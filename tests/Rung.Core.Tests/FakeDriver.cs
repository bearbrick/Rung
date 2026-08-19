using Rung.Abstractions;

namespace Rung.Core.Tests;

/// <summary>
/// 可编程的假驱动。让"断线、慢响应、写失败"这些平时要靠拔网线才能复现的场景，
/// 变成一行赋值就能构造出来的确定性测试。
/// </summary>
internal sealed class FakeDriver : IDeviceDriver
{
    private readonly List<TaskCompletionSource> _pollWaiters = [];
    private readonly Lock _gate = new();

    public FakeDriver(string deviceId) => DeviceId = deviceId;

    public string DeviceId { get; }

    public DriverState State { get; private set; } = DriverState.Disconnected;

    public int MaxPduLength { get; private set; }

    public int ConnectCount { get; private set; }

    public int PollCount { get; private set; }

    public List<(TagDef Tag, TagValue Value)> Writes { get; } = [];

    /// <summary>前 N 次连接直接失败，用于验证退避重连。</summary>
    public int FailConnectTimes { get; set; }

    /// <summary>采集到第 N 次时抛异常，模拟运行中断线。0 表示不断。</summary>
    public int FailPollAtCount { get; set; }

    /// <summary>写命令一律失败。</summary>
    public bool FailWrites { get; set; }

    /// <summary>编译计划时报告的配置问题。</summary>
    public List<TagIssue> Issues { get; } = [];

    /// <summary>每个点位返回的值，按下标；缺省返回下标本身。</summary>
    public Func<int, long> ValueFactory { get; set; } = static i => i;

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;

        if (ConnectCount <= FailConnectTimes)
        {
            State = DriverState.Faulted;
            throw new RungException($"假驱动第 {ConnectCount} 次连接失败");
        }

        State = DriverState.Connected;
        MaxPduLength = 240;

        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        State = DriverState.Disconnected;
        return ValueTask.CompletedTask;
    }

    public IReadPlan CreateReadPlan(IReadOnlyList<TagDef> tags) => new FakePlan(tags, Issues);

    public ValueTask<int> ExecuteAsync(IReadPlan plan, TagValue[] destination, CancellationToken cancellationToken)
    {
        PollCount++;

        if (FailPollAtCount > 0 && PollCount >= FailPollAtCount)
        {
            State = DriverState.Faulted;
            throw new RungException("假驱动模拟采集中断线");
        }

        var timestamp = DateTime.UtcNow;
        for (var i = 0; i < plan.Tags.Count; i++)
        {
            destination[i] = TagValue.FromInteger(TagDataType.Int32, ValueFactory(i), timestamp);
        }

        ReleaseWaiters();

        return ValueTask.FromResult(plan.Tags.Count);
    }

    public ValueTask WriteAsync(TagDef tag, TagValue value, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            throw new RungException($"假驱动拒绝写入 {tag.Name}");
        }

        Writes.Add((tag, value));
        return ValueTask.CompletedTask;
    }

    /// <summary>等到至少完成 <paramref name="count"/> 轮采集。避免用 sleep 让测试变得不稳定。</summary>
    public Task WaitForPollsAsync(int count)
    {
        lock (_gate)
        {
            if (PollCount >= count)
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pollWaiters.Add(waiter);

            return WaitLoopAsync(waiter, count);
        }
    }

    private async Task WaitLoopAsync(TaskCompletionSource waiter, int count)
    {
        while (PollCount < count)
        {
            await waiter.Task.ConfigureAwait(false);

            lock (_gate)
            {
                if (PollCount < count)
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pollWaiters.Add(waiter);
                }
            }
        }
    }

    private void ReleaseWaiters()
    {
        lock (_gate)
        {
            foreach (var waiter in _pollWaiters)
            {
                waiter.TrySetResult();
            }

            _pollWaiters.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        State = DriverState.Disconnected;
        ReleaseWaiters();

        return ValueTask.CompletedTask;
    }

    private sealed class FakePlan(IReadOnlyList<TagDef> tags, IReadOnlyList<TagIssue> issues) : IReadPlan
    {
        public IReadOnlyList<TagDef> Tags { get; } = tags;

        public IReadOnlyList<TagIssue> Issues { get; } = issues;

        public int RequestCount => 1;
    }
}

/// <summary>按设备标识分发不同的假驱动，用于多设备编排的测试。</summary>
internal sealed class MultiDeviceFakeFactory : IDeviceDriverFactory
{
    private readonly Dictionary<string, FakeDriver> _drivers = new(StringComparer.Ordinal);

    public string Protocol => "fake";

    public string AddressSyntaxHint => "任意字符串";

    /// <summary>取得（必要时创建）某台设备的假驱动，以便注入故障。</summary>
    public FakeDriver this[string deviceId]
    {
        get
        {
            if (!_drivers.TryGetValue(deviceId, out var driver))
            {
                driver = new FakeDriver(deviceId);
                _drivers[deviceId] = driver;
            }

            return driver;
        }
    }

    public IDeviceDriver Create(DeviceOptions options) => this[options.DeviceId];
}

/// <summary>始终交出同一个假驱动实例，方便测试跨重连观察它的状态。</summary>
internal sealed class FakeDriverFactory(FakeDriver driver) : IDeviceDriverFactory
{
    public string Protocol => "fake";

    public string AddressSyntaxHint => "任意字符串";

    public IDeviceDriver Create(DeviceOptions options) => driver;
}
