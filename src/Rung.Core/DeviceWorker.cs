using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>
/// 一台设备的采集工作者：持有唯一的驱动实例，负责连接生命周期、
/// 周期采集、写命令执行，以及断线后的退避重连。
/// <para>
/// <b>整个工作者是单线程的。</b>所有对驱动的调用都发生在 <see cref="RunAsync"/>
/// 那一个循环里，这正好满足驱动"调用方负责串行化"的约定。
/// 写命令通过 Channel 投递进来，在下一次采集之前插队执行。
/// </para>
/// <para>
/// 采集不排队，而是按截止时间驱动。设备变慢时截止时间自然顺延并计入
/// <see cref="DeviceStatus.OverrunCount"/>，不会积压出一个越滚越大的任务队列——
/// 那种积压最后会表现成"网关内存一直涨"。
/// </para>
/// </summary>
public sealed class DeviceWorker : IAsyncDisposable
{
    private readonly IDeviceDriverFactory _factory;
    private readonly DeviceOptions _deviceOptions;
    private readonly DeviceWorkerOptions _options;
    private readonly TagCache _cache;
    private readonly IReadOnlyList<ITagSink> _sinks;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IWriteAuditLog _audit;
    private readonly PollGroup[] _groups;
    private readonly Channel<WriteCommand> _writes;

    private IDeviceDriver? _driver;
    private DeviceStatus _status;
    private bool _disposed;

    /// <summary>创建一个采集工作者。构造时不发起任何 IO。</summary>
    public DeviceWorker(
        IDeviceDriverFactory factory,
        DeviceOptions deviceOptions,
        IReadOnlyList<TagDef> tags,
        TagCache cache,
        IReadOnlyList<ITagSink>? sinks = null,
        DeviceWorkerOptions? options = null,
        ILogger<DeviceWorker>? logger = null,
        TimeProvider? timeProvider = null,
        IWriteAuditLog? auditLog = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(deviceOptions);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(cache);

        _factory = factory;
        _deviceOptions = deviceOptions;
        _cache = cache;
        _sinks = sinks ?? [];
        _options = options ?? new DeviceWorkerOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _audit = auditLog ?? NullWriteAuditLog.Instance;

        _groups = [.. tags
            .Where(static tag => tag.Enabled)
            .GroupBy(static tag => tag.PollGroup, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(group => new PollGroup(group.Key, _options.GetInterval(group.Key), [.. group]))];

        _writes = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(_options.WriteQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        _status = new DeviceStatus { DeviceId = deviceOptions.DeviceId };
    }

    /// <summary>当前运行状况。任意线程可读。</summary>
    public DeviceStatus Status => Volatile.Read(ref _status);

    /// <summary>该设备下的采集组名称。</summary>
    public IReadOnlyList<string> PollGroups => [.. _groups.Select(static g => g.Name)];

    /// <summary>
    /// 下发一个写命令，等待它执行并回读确认。
    /// <para>
    /// 写命令插队到下一次采集之前执行，写完<b>立即从设备回读同一个点位</b>，
    /// 返回的是设备上的真实值而不是刚发出去的值。这不是锦上添花：
    /// PLC 程序可能对写入值做钳位、取整，或者干脆被联锁逻辑改回去，
    /// 操作员必须看到实际生效的结果。
    /// </para>
    /// <para>
    /// 每次写都会记一条 Information 级审计日志——谁、什么时候、往哪个点位写了什么值。
    /// </para>
    /// </summary>
    /// <param name="tag">要写的点位。</param>
    /// <param name="value">工程值。</param>
    /// <param name="cancellationToken">取消信号。</param>
    /// <param name="caller">
    /// 调用方名称，写进审计日志。产线上出了事，这条日志是唯一能还原
    /// "谁、什么时候、往哪个点位写了什么"的东西——没有调用方，审计就只剩一半。
    /// </param>
    /// <returns>回读到的设备实际值。</returns>
    public async Task<TagValue> WriteAsync(
        TagDef tag,
        TagValue value,
        CancellationToken cancellationToken,
        string caller = "unknown")
    {
        ArgumentNullException.ThrowIfNull(tag);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Status.State != DriverState.Connected)
        {
            throw new RungException($"设备 {_deviceOptions.DeviceId} 未连接，无法写入点位 {tag.Name}");
        }

        var command = new WriteCommand(tag, value, caller, new TaskCompletionSource<TagValue>(
            TaskCreationOptions.RunContinuationsAsynchronously));

        if (!_writes.Writer.TryWrite(command))
        {
            throw new RungException(
                $"设备 {_deviceOptions.DeviceId} 的写队列已满（容量 {_options.WriteQueueCapacity}）");
        }

        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<TagValue>)state!).TrySetCanceled(), command.Completion);

        return await command.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 长期运行的采集循环。连接失败或中断时按退避策略重连，直到取消。
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndServeAsync(cancellationToken).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                OnFailure(ex, attempt);

                var delay = _options.Reconnect.GetDelay(attempt, Random.Shared.NextDouble());
                Log.DeviceFaulted(_logger, _deviceOptions.DeviceId, attempt, ex.Message, delay);
                Log.DeviceFaultDetail(_logger, ex, _deviceOptions.DeviceId);

                try
                {
                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await TeardownAsync().ConfigureAwait(false);
        Update(status => status with { State = DriverState.Disconnected });
    }

    private async Task ConnectAndServeAsync(CancellationToken cancellationToken)
    {
        await TeardownAsync().ConfigureAwait(false);

        Update(status => status with { State = DriverState.Connecting });

        var driver = _factory.Create(_deviceOptions);
        _driver = driver;

        await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var issues = CompilePlans(driver);
        var now = _time.GetTimestamp();

        foreach (var group in _groups)
        {
            group.NextDue = now;
        }

        Update(status => status with
        {
            State = DriverState.Connected,
            ConsecutiveFailures = 0,
            NegotiatedPduLength = driver.MaxPduLength,
            ActiveTagCount = _groups.Sum(static g => g.Plan!.Tags.Count) - issues.Count,
            RequestCount = _groups.Sum(static g => g.Plan!.RequestCount),
            Issues = issues,
            ReconnectCount = status.ReconnectCount + (status.LastFailureUtc is null ? 0 : 1),
        });

        Log.DeviceConnected(
            _logger, _deviceOptions.DeviceId, driver.MaxPduLength, Status.ActiveTagCount, Status.RequestCount);

        await ServeAsync(driver, cancellationToken).ConfigureAwait(false);
    }

    private async Task ServeAsync(IDeviceDriver driver, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // 写命令插队：读是周期性的，写是事件驱动的，让操作员的指令等一整个采集周期没有道理
            while (_writes.Reader.TryRead(out var command))
            {
                await ExecuteWriteAsync(driver, command, cancellationToken).ConfigureAwait(false);
            }

            var now = _time.GetTimestamp();
            foreach (var group in _groups)
            {
                if (group.NextDue <= now)
                {
                    await PollAsync(driver, group, cancellationToken).ConfigureAwait(false);
                    AdvanceDeadline(group);
                }
            }

            await WaitForWorkAsync(TimeUntilNextDeadline(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(IDeviceDriver driver, PollGroup group, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();

        await driver.ExecuteAsync(group.Plan!, group.Values, cancellationToken).ConfigureAwait(false);

        var elapsed = _time.GetElapsedTime(started);
        var changed = _cache.Update(_deviceOptions.DeviceId, group.Plan!.Tags, group.Values);

        Update(status => status with
        {
            LastSuccessUtc = _time.GetUtcNow().UtcDateTime,
            LastPollDuration = elapsed,
            ConsecutiveFailures = 0,
        });

        await PublishAsync(changed, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteWriteAsync(
        IDeviceDriver driver,
        WriteCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Completion.Task.IsCompleted)
        {
            return; // 调用方已经取消了
        }

        try
        {
            await driver.WriteAsync(command.Tag, command.Value, cancellationToken).ConfigureAwait(false);

            // 写审计：产线上出了事，这条日志是唯一能还原"谁动了什么"的东西
            Log.TagWritten(
                _logger, _deviceOptions.DeviceId, command.Tag.Name, command.Value,
                command.Tag.Address, command.Caller);

            var actual = await ReadBackAsync(driver, command.Tag, cancellationToken).ConfigureAwait(false);

            await AuditAsync(command, actual, error: null, cancellationToken).ConfigureAwait(false);
            command.Completion.TrySetResult(actual);
        }
        catch (Exception ex)
        {
            Log.TagWriteFailed(_logger, ex, _deviceOptions.DeviceId, command.Tag.Name);

            // 失败也要留痕：只记成功的审计，等于把"谁试图动了什么但没成"这一半丢掉了
            await AuditAsync(command, actual: null, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);

            command.Completion.TrySetException(ex);

            // 写失败往往意味着链路已经不行了，交给外层重连
            throw;
        }
    }

    /// <summary>
    /// 落一条审计记录。
    /// <para>
    /// 审计失败不阻断写操作：磁盘满了不该让操作员改不了设定值——
    /// 那个后果比丢一条审计记录严重得多。但要在普通日志里喊出来。
    /// </para>
    /// </summary>
    private async Task AuditAsync(
        WriteCommand command, TagValue? actual, string? error, CancellationToken cancellationToken)
    {
        try
        {
            await _audit.RecordAsync(new WriteAuditRecord(
                _time.GetUtcNow().UtcDateTime,
                command.Caller,
                _deviceOptions.DeviceId,
                command.Tag.Name,
                command.Tag.Address,
                command.Tag.DataType.ToString(),
                FormatForAudit(command.Value),
                actual is { } read ? FormatForAudit(read) : null,
                error is null,
                error), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.AuditFailed(_logger, ex, _deviceOptions.DeviceId, command.Tag.Name);
        }
    }

    /// <summary>审计里的值只要数值本身，类型已经单独成列了。</summary>
    private static string FormatForAudit(TagValue value)
        => value.ToObject()?.ToString() ?? string.Empty;

    /// <summary>
    /// 写完立刻回读同一个点位。
    /// <para>
    /// 每次写都临时编译一份单点计划，看着浪费，但写命令是操作员触发的低频动作，
    /// 换来的是"返回值确实来自设备"这个硬保证，很划算。
    /// </para>
    /// </summary>
    private async Task<TagValue> ReadBackAsync(
        IDeviceDriver driver,
        TagDef tag,
        CancellationToken cancellationToken)
    {
        var timestamp = _time.GetUtcNow().UtcDateTime;
        var plan = driver.CreateReadPlan([tag]);

        if (plan.Issues.Count > 0)
        {
            return TagValue.Bad(tag.DataType, TagQuality.ConfigError, timestamp);
        }

        var buffer = new TagValue[plan.Tags.Count];
        await driver.ExecuteAsync(plan, buffer, cancellationToken).ConfigureAwait(false);

        // 回读结果同样进缓存并向北推送：写完之后所有消费者立刻看到新值，
        // 不必等下一个采集周期
        var changed = _cache.Update(_deviceOptions.DeviceId, plan.Tags, buffer);
        await PublishAsync(changed, cancellationToken).ConfigureAwait(false);

        return buffer[0];
    }

    private async Task PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken)
    {
        if (changed.Count == 0 || _sinks.Count == 0)
        {
            return;
        }

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.PublishAsync(changed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 采集是第一优先级：下游挂了不能反过来把采集拖停
                Log.SinkFailed(_logger, ex, sink.GetType().Name);
            }
        }
    }

    private List<TagIssue> CompilePlans(IDeviceDriver driver)
    {
        var issues = new List<TagIssue>();

        foreach (var group in _groups)
        {
            group.Plan = driver.CreateReadPlan(group.Tags);
            group.Values = new TagValue[group.Plan.Tags.Count];
            issues.AddRange(group.Plan.Issues);
        }

        foreach (var issue in issues)
        {
            Log.TagConfigIssue(_logger, _deviceOptions.DeviceId, issue.TagName, issue.Reason);
        }

        return issues;
    }

    /// <summary>
    /// 把截止时间推进一个周期。落后超过一个周期时不追赶，
    /// 而是直接对齐到当前时刻——否则设备恢复后会瞬间连发好几轮，
    /// 反而把刚缓过来的链路再次打死。
    /// </summary>
    private void AdvanceDeadline(PollGroup group)
    {
        var intervalTicks = (long)(group.Interval.TotalSeconds * _time.TimestampFrequency);
        group.NextDue += intervalTicks;

        var now = _time.GetTimestamp();
        if (group.NextDue <= now)
        {
            group.NextDue = now + intervalTicks;
            Update(status => status with { OverrunCount = status.OverrunCount + 1 });
        }
    }

    private TimeSpan TimeUntilNextDeadline()
    {
        if (_groups.Length == 0)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var now = _time.GetTimestamp();
        var earliest = _groups.Min(static g => g.NextDue);
        var ticks = earliest - now;

        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)ticks / _time.TimestampFrequency);
    }

    /// <summary>等到下一个采集截止时间，或者被写命令提前唤醒。</summary>
    private async Task WaitForWorkAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        using var waitScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var writeReady = _writes.Reader.WaitToReadAsync(waitScope.Token).AsTask();
        var deadline = delay == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.InfiniteTimeSpan, waitScope.Token)
            : Task.Delay(delay, _time, waitScope.Token);

        try
        {
            await Task.WhenAny(writeReady, deadline).ConfigureAwait(false);
        }
        finally
        {
            await waitScope.CancelAsync().ConfigureAwait(false);

            // 两个任务都要观察，否则落败的那个会留下未观察的取消异常
            await Task.WhenAll(Observe(writeReady), Observe(deadline)).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期内：竞速中落败的一方总会被取消
        }
    }

    private void OnFailure(Exception exception, int attempt)
    {
        _cache.MarkDeviceStale(_deviceOptions.DeviceId);

        Update(status => status with
        {
            State = DriverState.Faulted,
            LastFailureUtc = _time.GetUtcNow().UtcDateTime,
            LastError = exception.Message,
            ConsecutiveFailures = attempt,
        });

        // 未完成的写命令要立刻失败，不能吊着调用方等一个永远不会来的结果
        while (_writes.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetException(
                new RungException($"设备 {_deviceOptions.DeviceId} 通讯中断，写命令未执行", exception));
        }
    }

    private async ValueTask TeardownAsync()
    {
        if (_driver is null)
        {
            return;
        }

        try
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.TeardownFailed(_logger, ex, _deviceOptions.DeviceId);
        }

        _driver = null;
    }

    private void Update(Func<DeviceStatus, DeviceStatus> mutate)
        => Volatile.Write(ref _status, mutate(Volatile.Read(ref _status)));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writes.Writer.TryComplete();

        while (_writes.Reader.TryRead(out var pending))
        {
            pending.Completion.TrySetCanceled();
        }

        await TeardownAsync().ConfigureAwait(false);
    }

    private sealed record WriteCommand(
        TagDef Tag, TagValue Value, string Caller, TaskCompletionSource<TagValue> Completion);

    private sealed class PollGroup(string name, TimeSpan interval, IReadOnlyList<TagDef> tags)
    {
        public string Name { get; } = name;

        public TimeSpan Interval { get; } = interval;

        public IReadOnlyList<TagDef> Tags { get; } = tags;

        public IReadPlan? Plan { get; set; }

        public TagValue[] Values { get; set; } = [];

        public long NextDue { get; set; }
    }
}
