using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.Fins;

/// <summary>一次 FINS 读请求。</summary>
/// <param name="Address">起始地址。</param>
/// <param name="Count">点数：按字读为字数，按位读为位数。</param>
public readonly record struct FinsReadRequest(FinsAddress Address, int Count);

/// <summary>一个点位在响应中的位置。</summary>
/// <param name="RequestIndex">所属请求下标；配置有问题的点位为 -1。</param>
/// <param name="Offset">按位读为块内位序号，按字读为块内字节偏移。</param>
/// <param name="Bit">字内位号，仅按字读的布尔点位使用。</param>
/// <param name="HasBit">是否显式指定了位。</param>
public readonly record struct FinsTagLocation(int RequestIndex, int Offset, byte Bit, bool HasBit)
{
    /// <summary>配置有问题、未纳入采集的点位。</summary>
    public static FinsTagLocation Invalid => new(-1, 0, 0, false);

    /// <summary>该点位是否参与采集。</summary>
    public bool IsValid => RequestIndex >= 0;
}

/// <summary>一份编译好的 FINS 读取计划。</summary>
public sealed class FinsReadPlan : IReadPlan
{
    internal FinsReadPlan(
        IReadOnlyList<TagDef> tags,
        IReadOnlyList<FinsReadRequest> requests,
        IReadOnlyList<FinsTagLocation> locations,
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
    public IReadOnlyList<FinsReadRequest> Requests { get; }

    /// <summary>每个点位的数据位置。</summary>
    public IReadOnlyList<FinsTagLocation> Locations { get; }

    /// <summary>每次请求覆盖了哪些点位。</summary>
    public IReadOnlyList<IReadOnlyList<int>> TagIndexesByRequest { get; }

    /// <summary>实际参与采集的点位数。</summary>
    public int ActiveTagCount => Tags.Count - Issues.Count;

    private static int[][] BuildReverseIndex(int requestCount, IReadOnlyList<FinsTagLocation> locations)
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

/// <summary>FINS 读取计划的编译选项。</summary>
public sealed record FinsReadPlannerOptions
{
    /// <summary>两个点位之间允许跨越的最大空洞字数。</summary>
    public int MaxGapWords { get; init; } = 16;

    /// <summary>默认选项。</summary>
    public static FinsReadPlannerOptions Default { get; } = new();
}

/// <summary>
/// 把一批点位编译成最少的 FINS 读请求。
/// <para>
/// 与 MELSEC 的一处关键差异：欧姆龙的位地址就是"某个字的某一位"，
/// 所以<b>按字读回来再取位</b>比逐位读高效得多——
/// 一个字里的 16 个布尔点位只需一次读取。
/// </para>
/// </summary>
public static class FinsReadPlanner
{
    /// <summary>编译一份读取计划。</summary>
    public static FinsReadPlan Create(IReadOnlyList<TagDef> tags, FinsReadPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= FinsReadPlannerOptions.Default;

        var issues = new List<TagIssue>();
        var locations = new FinsTagLocation[tags.Count];
        Array.Fill(locations, FinsTagLocation.Invalid);

        var resolved = Resolve(tags, issues);
        var blocks = MergeIntoBlocks(resolved, options.MaxGapWords);
        var requests = Emit(blocks, locations);

        return new FinsReadPlan(tags, requests, locations, issues);
    }

    private static List<ResolvedTag> Resolve(IReadOnlyList<TagDef> tags, List<TagIssue> issues)
    {
        var resolved = new List<ResolvedTag>(tags.Count);

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];

            if (!FinsAddressParser.TryParse(tag.Address, out var address, out var reason))
            {
                issues.Add(new TagIssue(i, tag.Name, reason));
                continue;
            }

            if (address.HasBit)
            {
                if (tag.DataType != TagDataType.Bool)
                {
                    issues.Add(new TagIssue(i, tag.Name,
                        $"地址 {tag.Address} 指向单个位，只能配 Bool，实际配了 {tag.DataType}"));
                    continue;
                }

                // 位点位统一按字读，回来再取位。同一个字里的 16 个位只要一次读取
                resolved.Add(new ResolvedTag(i, address, 1));
                continue;
            }

            if (tag.DataType.IsVariableLength() && tag.Length <= 0)
            {
                issues.Add(new TagIssue(i, tag.Name, $"{tag.DataType} 是变长类型，必须配置 Length"));
                continue;
            }

            var words = (tag.ByteLength + 1) / 2;
            if (words > FinsProtocol.MaxWords)
            {
                issues.Add(new TagIssue(i, tag.Name, string.Create(CultureInfo.InvariantCulture,
                    $"长度 {words} 个字超过单次读取上限 {FinsProtocol.MaxWords}，请拆分成多个点位")));
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
            var c = ((byte)a.Address.Area).CompareTo((byte)b.Address.Area);
            if (c != 0) { return c; }

            c = a.Address.Word.CompareTo(b.Address.Word);
            if (c != 0) { return c; }

            c = b.Words.CompareTo(a.Words);
            return c != 0 ? c : a.TagIndex.CompareTo(b.TagIndex);
        });

        var blocks = new List<Block>();
        Block? current = null;

        foreach (var tag in resolved)
        {
            var start = tag.Address.Word;
            var end = start + tag.Words;

            if (current is not null
                && current.Area == tag.Address.Area
                && start - current.End <= maxGap
                && Math.Max(current.End, end) - current.Start <= FinsProtocol.MaxWords)
            {
                current.End = Math.Max(current.End, end);
                current.Members.Add(tag);
                continue;
            }

            current = new Block(tag.Address.Area, start, end);
            current.Members.Add(tag);
            blocks.Add(current);
        }

        return blocks;
    }

    private static List<FinsReadRequest> Emit(List<Block> blocks, FinsTagLocation[] locations)
    {
        var requests = new List<FinsReadRequest>(blocks.Count);

        for (var requestIndex = 0; requestIndex < blocks.Count; requestIndex++)
        {
            var block = blocks[requestIndex];
            requests.Add(new FinsReadRequest(new FinsAddress(block.Area, block.Start), block.Length));

            foreach (var member in block.Members)
            {
                var relative = member.Address.Word - block.Start;

                locations[member.TagIndex] = new FinsTagLocation(
                    requestIndex, relative * 2, member.Address.Bit, member.Address.HasBit);
            }
        }

        return requests;
    }

    private readonly record struct ResolvedTag(int TagIndex, FinsAddress Address, int Words);

    private sealed class Block(FinsArea area, int start, int end)
    {
        public FinsArea Area { get; } = area;

        public int Start { get; } = start;

        public int End { get; set; } = end;

        public int Length => End - Start;

        public List<ResolvedTag> Members { get; } = [];
    }
}
