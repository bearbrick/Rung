using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.Melsec;

/// <summary>一次 MC 成批读取请求。</summary>
/// <param name="Address">起始软元件。</param>
/// <param name="Points">点数：字软元件按字计，位软元件按位计。</param>
public readonly record struct MelsecReadRequest(MelsecAddress Address, int Points);

/// <summary>一个点位在响应中的位置。</summary>
/// <param name="RequestIndex">所属请求下标；配置有问题的点位为 -1。</param>
/// <param name="Offset">位软元件为块内点序号，字软元件为块内字节偏移。</param>
public readonly record struct MelsecTagLocation(int RequestIndex, int Offset)
{
    /// <summary>配置有问题、未纳入采集的点位。</summary>
    public static MelsecTagLocation Invalid => new(-1, 0);

    /// <summary>该点位是否参与采集。</summary>
    public bool IsValid => RequestIndex >= 0;
}

/// <summary>一份编译好的 MELSEC 读取计划。</summary>
public sealed class MelsecReadPlan : IReadPlan
{
    internal MelsecReadPlan(
        IReadOnlyList<TagDef> tags,
        IReadOnlyList<MelsecReadRequest> requests,
        IReadOnlyList<MelsecTagLocation> locations,
        IReadOnlyList<TagIssue> issues)
    {
        Tags = tags;
        Requests = requests;
        Locations = locations;
        Issues = issues;
        TagIndexesByRequest = BuildReverseIndex(requests.Count, locations);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TagDef> Tags { get; }

    /// <inheritdoc/>
    public IReadOnlyList<TagIssue> Issues { get; }

    /// <inheritdoc/>
    public int RequestCount => Requests.Count;

    /// <summary>合并切分后的请求列表。</summary>
    public IReadOnlyList<MelsecReadRequest> Requests { get; }

    /// <summary>每个点位的数据位置，与 <see cref="Tags"/> 同序等长。</summary>
    public IReadOnlyList<MelsecTagLocation> Locations { get; }

    /// <summary>每次请求覆盖了哪些点位。</summary>
    public IReadOnlyList<IReadOnlyList<int>> TagIndexesByRequest { get; }

    /// <summary>实际参与采集的点位数。</summary>
    public int ActiveTagCount => Tags.Count - Issues.Count;

    private static int[][] BuildReverseIndex(int requestCount, IReadOnlyList<MelsecTagLocation> locations)
    {
        var buckets = new List<int>[requestCount];
        for (var i = 0; i < requestCount; i++)
        {
            buckets[i] = [];
        }

        for (var tagIndex = 0; tagIndex < locations.Count; tagIndex++)
        {
            if (locations[tagIndex].IsValid)
            {
                buckets[locations[tagIndex].RequestIndex].Add(tagIndex);
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

/// <summary>MELSEC 读取计划的编译选项。</summary>
public sealed record MelsecReadPlannerOptions
{
    /// <summary>
    /// 两个点位之间允许跨越的最大空洞，单位与所在软元件一致（字或位）。
    /// <para>
    /// 和 Modbus 一样，MC 每次请求都是一个完整的 TCP 往返，
    /// 没有"一个报文塞多段地址"的机制，所以合并收益大、阈值取得比 S7 激进。
    /// </para>
    /// </summary>
    public int MaxGapPoints { get; init; } = 16;

    /// <summary>默认选项。</summary>
    public static MelsecReadPlannerOptions Default { get; } = new();
}

/// <summary>把一批点位编译成最少的 MC 读请求。</summary>
public static class MelsecReadPlanner
{
    /// <summary>编译一份读取计划。</summary>
    public static MelsecReadPlan Create(
        IReadOnlyList<TagDef> tags,
        MelsecReadPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= MelsecReadPlannerOptions.Default;

        var issues = new List<TagIssue>();
        var locations = new MelsecTagLocation[tags.Count];
        Array.Fill(locations, MelsecTagLocation.Invalid);

        var resolved = Resolve(tags, issues);
        var blocks = MergeIntoBlocks(resolved, options.MaxGapPoints);
        var requests = Emit(blocks, locations);

        return new MelsecReadPlan(tags, requests, locations, issues);
    }

    private static List<ResolvedTag> Resolve(IReadOnlyList<TagDef> tags, List<TagIssue> issues)
    {
        var resolved = new List<ResolvedTag>(tags.Count);

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];

            if (!MelsecAddressParser.TryParse(tag.Address, out var address, out var reason))
            {
                issues.Add(new TagIssue(i, tag.Name, reason));
                continue;
            }

            if (address.IsBit)
            {
                // 继电器只有 0/1，从里面读整数没有意义，多半是软元件写错了
                if (tag.DataType != TagDataType.Bool)
                {
                    issues.Add(new TagIssue(i, tag.Name,
                        $"{address.Device} 是位软元件，只能配 Bool，实际配了 {tag.DataType}"));
                    continue;
                }

                resolved.Add(new ResolvedTag(i, address, 1));
                continue;
            }

            if (tag.DataType.IsVariableLength() && tag.Length <= 0)
            {
                issues.Add(new TagIssue(i, tag.Name, $"{tag.DataType} 是变长类型，必须配置 Length"));
                continue;
            }

            // MELSEC 寄存器是 16 位的，奇数字节长度向上取整到整字
            var words = (tag.ByteLength + 1) / 2;
            if (words > MelsecProtocol.MaxWordPoints)
            {
                issues.Add(new TagIssue(i, tag.Name, string.Create(CultureInfo.InvariantCulture,
                    $"长度 {words} 个字超过单次读取上限 {MelsecProtocol.MaxWordPoints}，请拆分成多个点位")));
                continue;
            }

            resolved.Add(new ResolvedTag(i, address, words));
        }

        return resolved;
    }

    private static List<Block> MergeIntoBlocks(List<ResolvedTag> resolved, int maxGap)
    {
        resolved.Sort(static (a, b) =>
        {
            var c = ((byte)a.Address.Device).CompareTo((byte)b.Address.Device);
            if (c != 0) { return c; }

            c = a.Address.Number.CompareTo(b.Address.Number);
            if (c != 0) { return c; }

            c = b.Length.CompareTo(a.Length);
            return c != 0 ? c : a.TagIndex.CompareTo(b.TagIndex);
        });

        var blocks = new List<Block>();
        Block? current = null;

        foreach (var tag in resolved)
        {
            var start = tag.Address.Number;
            var end = start + tag.Length;
            var limit = MelsecProtocol.MaxPoints(tag.Address.Device);

            if (current is not null
                && current.Device == tag.Address.Device
                && start - current.End <= maxGap
                && Math.Max(current.End, end) - current.Start <= limit)
            {
                current.End = Math.Max(current.End, end);
                current.Members.Add(tag);
                continue;
            }

            current = new Block(tag.Address.Device, start, end);
            current.Members.Add(tag);
            blocks.Add(current);
        }

        return blocks;
    }

    private static List<MelsecReadRequest> Emit(List<Block> blocks, MelsecTagLocation[] locations)
    {
        var requests = new List<MelsecReadRequest>(blocks.Count);

        for (var requestIndex = 0; requestIndex < blocks.Count; requestIndex++)
        {
            var block = blocks[requestIndex];
            requests.Add(new MelsecReadRequest(
                new MelsecAddress(block.Device, block.Start), block.Length));

            foreach (var member in block.Members)
            {
                var relative = member.Address.Number - block.Start;

                locations[member.TagIndex] = block.Device.IsBitDevice()
                    ? new MelsecTagLocation(requestIndex, relative)
                    // 字软元件里记字节偏移：一个字两个字节
                    : new MelsecTagLocation(requestIndex, relative * 2);
            }
        }

        return requests;
    }

    private readonly record struct ResolvedTag(int TagIndex, MelsecAddress Address, int Length);

    private sealed class Block(MelsecDevice device, int start, int end)
    {
        public MelsecDevice Device { get; } = device;

        public int Start { get; } = start;

        public int End { get; set; } = end;

        public int Length => End - Start;

        public List<ResolvedTag> Members { get; } = [];
    }
}
