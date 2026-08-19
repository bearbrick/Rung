using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>
/// 一个请求报文所携带的数据项集合。一次请求对应一个网络往返。
/// </summary>
/// <param name="Items">本次请求要读的数据项。</param>
/// <param name="ResponseByteLength">预估的响应报文长度，含头部与填充字节。</param>
public sealed record S7ReadRequestGroup(IReadOnlyList<S7ReadItem> Items, int ResponseByteLength);

/// <summary>
/// 一个点位的数据在响应中的位置。
/// <para>
/// 合并之后一次请求可能覆盖上百个点位，拆分就靠它：
/// 找到第 <see cref="RequestIndex"/> 次请求的第 <see cref="ItemIndex"/> 项，
/// 从 <see cref="ByteOffset"/> 处取数据。
/// </para>
/// </summary>
/// <param name="RequestIndex">所属请求的下标；配置有问题的点位为 -1。</param>
/// <param name="ItemIndex">在该请求的数据项列表中的下标。</param>
/// <param name="ByteOffset">在该数据项内的字节偏移。</param>
/// <param name="BitOffset">位偏移，仅 <see cref="TagDataType.Bool"/> 有意义。</param>
/// <param name="ByteLength">该点位占用的字节数。</param>
public readonly record struct S7TagLocation(
    int RequestIndex,
    int ItemIndex,
    int ByteOffset,
    byte BitOffset,
    int ByteLength)
{
    /// <summary>配置有问题、未纳入采集的点位。</summary>
    public static S7TagLocation Invalid => new(-1, -1, 0, 0, 0);

    /// <summary>该点位是否参与采集。</summary>
    public bool IsValid => RequestIndex >= 0;
}

/// <summary>
/// 一份编译好的 S7 读取计划。
/// <para>
/// 地址解析、按连续性合并、按 PDU 上限切分，这些都在编译期做一次；
/// 采集周期里只做"发请求 - 按 <see cref="Locations"/> 拆数据"这两件事。
/// </para>
/// </summary>
public sealed class S7ReadPlan : IReadPlan
{
    internal S7ReadPlan(
        IReadOnlyList<TagDef> tags,
        IReadOnlyList<S7ReadRequestGroup> requests,
        IReadOnlyList<S7TagLocation> locations,
        IReadOnlyList<TagIssue> issues,
        int negotiatedPduLength)
    {
        Tags = tags;
        Requests = requests;
        Locations = locations;
        Issues = issues;
        NegotiatedPduLength = negotiatedPduLength;
        TagIndexesByRequest = BuildReverseIndex(requests.Count, locations);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TagDef> Tags { get; }

    /// <inheritdoc/>
    public IReadOnlyList<TagIssue> Issues { get; }

    /// <inheritdoc/>
    public int RequestCount => Requests.Count;

    /// <summary>合并切分后的请求列表。</summary>
    public IReadOnlyList<S7ReadRequestGroup> Requests { get; }

    /// <summary>每个点位的数据位置，与 <see cref="Tags"/> 同序等长。</summary>
    public IReadOnlyList<S7TagLocation> Locations { get; }

    /// <summary>编译该计划时使用的协商 PDU 长度。设备重连后若协商值变化，必须重新编译。</summary>
    public int NegotiatedPduLength { get; }

    /// <summary>
    /// 每次请求覆盖了哪些点位，按请求下标索引。
    /// <para>
    /// 采集时收到一帧响应就立刻解出这一批点位，不必回头扫描全部 Locations——
    /// 上千点位时这个反向索引省掉的是每轮 O(点位数 × 请求数) 的空转。
    /// </para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> TagIndexesByRequest { get; }

    /// <summary>实际参与采集的点位数。</summary>
    public int ActiveTagCount => Tags.Count - Issues.Count;

    /// <summary>
    /// 从设备取回的总字节数。与"点位实际需要的字节数"之差就是合并带来的浪费，
    /// Web UI 上把这两个数并排显示，现场调 <see cref="S7ReadPlannerOptions.MaxGapBytes"/> 时一目了然。
    /// </summary>
    public int TotalFetchedBytes
    {
        get
        {
            var total = 0;
            foreach (var request in Requests)
            {
                foreach (var item in request.Items)
                {
                    total += item.ExpectedByteLength;
                }
            }

            return total;
        }
    }

    private static int[][] BuildReverseIndex(
        int requestCount,
        IReadOnlyList<S7TagLocation> locations)
    {
        var buckets = new List<int>[requestCount];
        for (var i = 0; i < requestCount; i++)
        {
            buckets[i] = [];
        }

        for (var tagIndex = 0; tagIndex < locations.Count; tagIndex++)
        {
            var location = locations[tagIndex];
            if (location.IsValid)
            {
                buckets[location.RequestIndex].Add(tagIndex);
            }
        }

        var result = new int[requestCount][];
        for (var i = 0; i < requestCount; i++)
        {
            result[i] = [.. buckets[i]];
        }

        return result;
    }
}
