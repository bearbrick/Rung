namespace Rung.Core;

/// <summary>单台设备的采集参数。</summary>
public sealed record DeviceWorkerOptions
{
    /// <summary>未在 <see cref="PollGroupIntervals"/> 中指定的采集组使用的周期。</summary>
    public TimeSpan DefaultPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 各采集组的周期。同一台设备上不同组独立调度、互不干扰——
    /// 温度 5 秒一次、产量计数 500 ms 一次，各走各的。
    /// </summary>
    public IReadOnlyDictionary<string, TimeSpan> PollGroupIntervals { get; init; }
        = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

    /// <summary>断线重连策略。</summary>
    public ReconnectPolicy Reconnect { get; init; } = ReconnectPolicy.Default;

    /// <summary>
    /// 写命令队列容量。队列满了就直接拒绝，不阻塞调用方——
    /// 写命令积压说明设备已经不正常了，让上游立刻知道比默默排队强。
    /// </summary>
    public int WriteQueueCapacity { get; init; } = 256;

    /// <summary>取该采集组的周期。</summary>
    public TimeSpan GetInterval(string pollGroup)
        => PollGroupIntervals.TryGetValue(pollGroup, out var interval) ? interval : DefaultPollInterval;
}
