namespace Rung.Core;

/// <summary>
/// 北向输出目标。Redis、MQTT、SSE 各实现一个，互不影响。
/// <para>
/// 只推送越过死区的点位，不是每轮全量推——
/// 一台设备上千个点位、500ms 一轮，全量推会把下游淹掉。
/// </para>
/// </summary>
public interface ITagSink
{
    /// <summary>
    /// 推送一批变化的点位。
    /// <para>
    /// <b>实现必须自己扛住失败</b>：Redis 挂了不能反过来把采集拖停。
    /// 采集是第一优先级，输出是尽力而为。
    /// </para>
    /// </summary>
    ValueTask PublishAsync(IReadOnlyList<TagSnapshot> changed, CancellationToken cancellationToken);
}
