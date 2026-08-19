using System.Collections.Concurrent;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>一个点位的最新采集结果。</summary>
/// <param name="DeviceId">来源设备。</param>
/// <param name="Tag">点位定义。</param>
/// <param name="Value">最新值。</param>
public sealed record TagSnapshot(string DeviceId, TagDef Tag, TagValue Value);

/// <summary>
/// 点位最新值缓存。北向接口（REST / SSE / Redis）都从这里读。
/// <para>
/// 采集线程写、任意线程读，靠"整体替换不可变快照"来保证一致性：
/// 引用赋值是原子的，读方永远看到一个自洽的值，不需要加锁。
/// <see cref="TagValue"/> 是 32 字节的结构体，直接放进字典会有撕裂读的风险。
/// </para>
/// </summary>
public sealed class TagCache
{
    private readonly ConcurrentDictionary<string, TagSnapshot> _current = new(StringComparer.Ordinal);

    /// <summary>
    /// 死区判定的基准值：每个点位最近一次推送出去的读数。
    /// <para>
    /// 必须是并发字典。多设备编排下所有 <see cref="DeviceWorker"/> 共享同一个缓存，
    /// 各自在自己的任务里调用 <see cref="Update"/>——曾经"只有一个采集线程"的假设，
    /// 在加入 <see cref="GatewayHost"/> 的那一刻就失效了。
    /// </para>
    /// <para>
    /// 无需跨键的一致性：<see cref="GatewayHost"/> 强制业务点位名全局唯一，
    /// 因此两台设备不会写同一个键。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, TagValue> _lastPublished = new(StringComparer.Ordinal);

    /// <summary>当前缓存的点位数量。</summary>
    public int Count => _current.Count;

    /// <summary>按业务名读取最新值。</summary>
    public bool TryGet(string tagName, out TagSnapshot snapshot)
        => _current.TryGetValue(tagName, out snapshot!);

    /// <summary>取出全部点位的快照，按名称排序，供 Web UI 展示。</summary>
    public IReadOnlyList<TagSnapshot> Snapshot()
        => [.. _current.Values.OrderBy(static s => s.Tag.Name, StringComparer.Ordinal)];

    /// <summary>
    /// 用一轮采集结果更新缓存，并挑出值得向北推送的点位。
    /// <para>
    /// 缓存本身总是更新到最新值；死区只决定<b>要不要推送</b>。
    /// 两者分开，是为了让 Web UI 上看到的永远是真实的当前值，
    /// 而不是被死区卡住的陈旧值——现场调试时这个区别很要命。
    /// </para>
    /// </summary>
    /// <returns>越过死区、需要推送的点位。</returns>
    public IReadOnlyList<TagSnapshot> Update(
        string deviceId,
        IReadOnlyList<TagDef> tags,
        ReadOnlySpan<TagValue> values)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var changed = new List<TagSnapshot>();

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var value = values[i];
            var snapshot = new TagSnapshot(deviceId, tag, value);

            _current[tag.Name] = snapshot;

            if (ShouldPublish(tag, value))
            {
                _lastPublished[tag.Name] = value;
                changed.Add(snapshot);
            }
        }

        return changed;
    }

    /// <summary>
    /// 把该设备的所有点位降级为 <see cref="TagQuality.Stale"/>，保留最后已知值。
    /// <para>
    /// 断线时不清空缓存：应用侧读到"5 分钟前的 235 度，质量 Stale"，
    /// 比读到 null 或者 0 有用得多——至少它知道炉子刚才是热的。
    /// </para>
    /// </summary>
    public void MarkDeviceStale(string deviceId)
    {
        foreach (var (name, snapshot) in _current)
        {
            if (!string.Equals(snapshot.DeviceId, deviceId, StringComparison.Ordinal)
                || snapshot.Value.Quality == TagQuality.Stale)
            {
                continue;
            }

            _current[name] = snapshot with { Value = snapshot.Value.AsStale() };
        }
    }

    private bool ShouldPublish(TagDef tag, TagValue value)
    {
        if (!_lastPublished.TryGetValue(tag.Name, out var previous))
        {
            return true;
        }

        // 质量变化本身就是必须上报的事件，死区管不着它
        if (previous.Quality != value.Quality)
        {
            return true;
        }

        if (tag.Deadband <= 0 || !value.IsGood || !IsNumeric(value.DataType))
        {
            // 只比读数，不比时间戳——否则恒定不变的点位每轮都会被判成"变了"
            return !value.HasSameValue(previous);
        }

        return Math.Abs(value.AsDouble() - previous.AsDouble()) >= tag.Deadband;
    }

    private static bool IsNumeric(TagDataType type)
        => type.IsNumeric() || type == TagDataType.Float64;
}
