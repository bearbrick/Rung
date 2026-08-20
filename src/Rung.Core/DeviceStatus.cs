using Rung.Abstractions;

namespace Rung.Core;

/// <summary>
/// 一台设备的运行状况快照。
/// <para>
/// 可观测性决定这东西好不好用。现场调试时，能看到"最后一次成功采集在什么时候、
/// 连续失败了几次、上一轮耗时多久、合并成了几次请求"，能省掉一半的排查时间。
/// </para>
/// </summary>
public sealed record DeviceStatus
{
    /// <summary>设备标识。</summary>
    public required string DeviceId { get; init; }

    /// <summary>当前连接状态。</summary>
    public DriverState State { get; init; } = DriverState.Disconnected;

    /// <summary>最近一次成功采集的时刻，UTC。</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>最近一次失败的时刻，UTC。</summary>
    public DateTime? LastFailureUtc { get; init; }

    /// <summary>最近一次错误的描述。连上之后不清空——排查时经常要回看上一次是怎么断的。</summary>
    public string? LastError { get; init; }

    /// <summary>连续失败次数。连接成功后归零。</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>累计重连次数。</summary>
    public int ReconnectCount { get; init; }

    /// <summary>最近一轮采集的耗时。</summary>
    public TimeSpan LastPollDuration { get; init; }

    /// <summary>
    /// 单次报文能承载的最大字节数。S7 是协商出来的，其余协议是写死的上限。
    /// </summary>
    public int MaxFrameBytes { get; init; }

    /// <summary>参与采集的点位数。</summary>
    public int ActiveTagCount { get; init; }

    /// <summary>每轮实际发出的请求次数。和点位数一起看，就知道合并效果如何。</summary>
    public int RequestCount { get; init; }

    /// <summary>
    /// 采集超时次数：一轮还没跑完，下一轮的时间就到了。
    /// 这个数持续增长说明采集周期设得太快，或者点位太多需要拆组。
    /// </summary>
    public int OverrunCount { get; init; }

    /// <summary>配置有问题的点位。Web UI 必须把它显著地展示出来。</summary>
    public IReadOnlyList<TagIssue> Issues { get; init; } = [];
}
