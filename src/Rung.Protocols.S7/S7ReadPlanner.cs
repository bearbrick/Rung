using System.Globalization;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>读取计划的编译选项。</summary>
public sealed record S7ReadPlannerOptions
{
    /// <summary>
    /// 两个点位之间允许跨越的最大空洞字节数。空洞内的字节会被一并读回后丢弃。
    /// <para>
    /// <b>这个数字怎么来的：</b>响应报文里每多一个数据项，固定开销是 4 字节
    /// （返回码 + 传输尺寸 + 长度）。把两段相隔 g 字节的数据合成一项，
    /// 省下 4 字节开销、多读 g 字节废数据，因此 <c>g &lt; 4</c> 时纯粹赚。
    /// </para>
    /// <para>
    /// 默认值取 8 而不是 4，是因为还有第二个约束：单个请求最多 19 个数据项
    /// （PDU 240 时）。点位又多又碎的时候，先撞上限的是项数而不是字节数，
    /// 此时多花几个字节换掉一个数据项是划算的。
    /// </para>
    /// <para>
    /// 这个取舍没有普适最优解，取决于点位在 DB 里排得有多密。
    /// 因此它是<b>每台设备可配</b>的，配合 <see cref="S7ReadPlan.TotalFetchedBytes"/>
    /// 和 <see cref="IReadPlan.RequestCount"/> 在现场调。
    /// </para>
    /// </summary>
    public int MaxGapBytes { get; init; } = 8;

    /// <summary>默认选项。</summary>
    public static S7ReadPlannerOptions Default { get; } = new();
}

/// <summary>
/// 把一批点位编译成最少的读请求。
/// <para>
/// 做不做这个合并，同样点位数下采集周期能差一个数量级：
/// 128 个散布在 DB 里的点位，逐个读是 128 次网络往返，
/// 合并后通常 2-3 次就够了。
/// </para>
/// <para>
/// 整个过程是纯函数——给定同样的点位和 PDU 长度，输出永远逐字节相同。
/// 这既让它可以被完整测试，也让 Web UI 上显示的"128 个点位 → 3 次请求"是可复现的。
/// </para>
/// </summary>
public static class S7ReadPlanner
{
    /// <summary>
    /// 编译一份读取计划。
    /// </summary>
    /// <param name="tags">要采集的点位。调用方负责先剔除已停用的点位。</param>
    /// <param name="negotiatedPduLength">连接协商得到的 PDU 长度。</param>
    /// <param name="options">编译选项。</param>
    public static S7ReadPlan Create(
        IReadOnlyList<TagDef> tags,
        int negotiatedPduLength,
        S7ReadPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentOutOfRangeException.ThrowIfLessThan(negotiatedPduLength, 240);

        options ??= S7ReadPlannerOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxGapBytes);

        var maxReadBytes = S7Protocol.MaxReadBytes(negotiatedPduLength);
        var maxItems = S7Protocol.MaxReadItems(negotiatedPduLength);

        var issues = new List<TagIssue>();
        var locations = new S7TagLocation[tags.Count];
        Array.Fill(locations, S7TagLocation.Invalid);

        var resolved = Resolve(tags, maxReadBytes, issues);
        var blocks = MergeIntoBlocks(resolved, maxReadBytes, options.MaxGapBytes);
        var requests = PackIntoRequests(blocks, negotiatedPduLength, maxItems, locations);

        return new S7ReadPlan(tags, requests, locations, issues, negotiatedPduLength);
    }

    /// <summary>解析地址并校验配置，返回可参与合并的点位。</summary>
    private static List<ResolvedTag> Resolve(IReadOnlyList<TagDef> tags, int maxReadBytes, List<TagIssue> issues)
    {
        var resolved = new List<ResolvedTag>(tags.Count);

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];

            if (!S7AddressParser.TryParse(tag.Address, out var address, out var reason))
            {
                issues.Add(new TagIssue(i, tag.Name, reason));
                continue;
            }

            var byteLength = tag.ByteLength;
            if (byteLength <= 0)
            {
                issues.Add(new TagIssue(i, tag.Name,
                    $"{tag.DataType} 是变长类型，必须配置 Length"));
                continue;
            }

            if (byteLength > maxReadBytes)
            {
                // 跨请求分片会让拆包逻辑复杂一大截，而这种点位很少见。
                // 给一句能直接照做的提示，比默默读回半截数据强得多
                issues.Add(new TagIssue(i, tag.Name, string.Create(CultureInfo.InvariantCulture,
                    $"长度 {byteLength} 字节超过单次读取上限 {maxReadBytes} 字节，请拆分成多个点位")));
                continue;
            }

            if (!ValidateSizeHint(tag, address, out var mismatch))
            {
                issues.Add(new TagIssue(i, tag.Name, mismatch));
                continue;
            }

            resolved.Add(new ResolvedTag(i, address, byteLength));
        }

        return resolved;
    }

    /// <summary>
    /// 校验地址里的宽度字母与点位数据类型是否自洽。
    /// 地址写 <c>DB1.DBW10</c>（2 字节）却把类型配成 Float32（4 字节），
    /// 是纯粹的配置错误，应该在这里拦下而不是等现场读回一个乱码。
    /// </summary>
    private static bool ValidateSizeHint(TagDef tag, S7Address address, out string mismatch)
    {
        mismatch = string.Empty;

        // 变长类型的长度由 Length 决定，地址上的宽度字母只是起始位置的写法
        if (address.SizeHint == S7SizeHint.None || tag.DataType.IsVariableLength())
        {
            return true;
        }

        if (address.SizeHint == S7SizeHint.Bit)
        {
            if (tag.DataType != TagDataType.Bool)
            {
                mismatch = $"地址 {tag.Address} 是位地址，数据类型却是 {tag.DataType}";
                return false;
            }

            return true;
        }

        var hintBytes = address.SizeHint.ToByteCount();
        var typeBytes = tag.DataType.SizeInBytes();
        if (hintBytes != typeBytes)
        {
            mismatch = string.Create(CultureInfo.InvariantCulture,
                $"地址 {tag.Address} 宽度为 {hintBytes} 字节，数据类型 {tag.DataType} 需要 {typeBytes} 字节");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 按 (存储区, DB 号) 分组，组内按地址排序后把相邻的点位合并成连续区块。
    /// </summary>
    private static List<Block> MergeIntoBlocks(List<ResolvedTag> resolved, int maxReadBytes, int maxGapBytes)
    {
        // 排序键包含点位下标，保证同地址点位的顺序稳定——计划必须可复现
        resolved.Sort(static (a, b) =>
        {
            var c = ((byte)a.Address.Area).CompareTo((byte)b.Address.Area);
            if (c != 0) { return c; }

            c = a.Address.DbNumber.CompareTo(b.Address.DbNumber);
            if (c != 0) { return c; }

            c = a.Address.ByteOffset.CompareTo(b.Address.ByteOffset);
            if (c != 0) { return c; }

            c = b.ByteLength.CompareTo(a.ByteLength);
            return c != 0 ? c : a.TagIndex.CompareTo(b.TagIndex);
        });

        var blocks = new List<Block>();
        Block? current = null;

        foreach (var tag in resolved)
        {
            var start = tag.Address.ByteOffset;
            var end = start + tag.ByteLength;

            if (current is not null
                && current.Area == tag.Address.Area
                && current.DbNumber == tag.Address.DbNumber
                && start - current.End <= maxGapBytes
                && Math.Max(current.End, end) - current.Start <= maxReadBytes)
            {
                current.End = Math.Max(current.End, end);
                current.Members.Add(tag);
                continue;
            }

            current = new Block(tag.Address.Area, tag.Address.DbNumber, start, end);
            current.Members.Add(tag);
            blocks.Add(current);
        }

        return blocks;
    }

    /// <summary>
    /// 把区块装进请求：贪心地往当前请求里塞，塞不下就开新的一次请求。
    /// <para>
    /// 项数和响应字节数两个上限必须同时满足——只看其中一个，
    /// 报文会在真机上被静默截断或拒收。
    /// </para>
    /// </summary>
    private static List<S7ReadRequestGroup> PackIntoRequests(
        List<Block> blocks,
        int negotiatedPduLength,
        int maxItems,
        S7TagLocation[] locations)
    {
        var requests = new List<S7ReadRequestGroup>();
        var pendingItems = new List<S7ReadItem>();
        var pendingLengths = new List<int>();
        var pendingBlocks = new List<Block>();

        foreach (var block in blocks)
        {
            var length = block.Length;

            var wouldExceedItems = pendingItems.Count + 1 > maxItems;
            var wouldExceedBytes = !FitsWithExtraItem(negotiatedPduLength, pendingLengths, length);

            if (pendingItems.Count > 0 && (wouldExceedItems || wouldExceedBytes))
            {
                Flush(requests, pendingItems, pendingLengths, pendingBlocks, locations);
            }

            pendingItems.Add(S7ReadItem.Bytes(new S7Address(block.Area, block.DbNumber, block.Start, 0), length));
            pendingLengths.Add(length);
            pendingBlocks.Add(block);
        }

        if (pendingItems.Count > 0)
        {
            Flush(requests, pendingItems, pendingLengths, pendingBlocks, locations);
        }

        return requests;
    }

    private static bool FitsWithExtraItem(int negotiatedPduLength, List<int> existing, int extra)
    {
        var candidate = new int[existing.Count + 1];
        existing.CopyTo(candidate);
        candidate[^1] = extra;

        return S7Protocol.ResponseFitsInPdu(negotiatedPduLength, candidate);
    }

    /// <summary>把攒好的数据项固化成一次请求，并回填其中每个点位的位置。</summary>
    private static void Flush(
        List<S7ReadRequestGroup> requests,
        List<S7ReadItem> items,
        List<int> lengths,
        List<Block> blocks,
        S7TagLocation[] locations)
    {
        var requestIndex = requests.Count;

        for (var itemIndex = 0; itemIndex < blocks.Count; itemIndex++)
        {
            var block = blocks[itemIndex];
            foreach (var member in block.Members)
            {
                locations[member.TagIndex] = new S7TagLocation(
                    requestIndex,
                    itemIndex,
                    member.Address.ByteOffset - block.Start,
                    member.Address.BitOffset,
                    member.ByteLength);
            }
        }

        requests.Add(new S7ReadRequestGroup(
            items.ToArray(),
            EstimateResponseLength(lengths)));

        items.Clear();
        lengths.Clear();
        blocks.Clear();
    }

    private static int EstimateResponseLength(List<int> lengths)
    {
        var total = S7Protocol.IsoHeaderLength + S7Protocol.AckDataHeaderLength + 2;
        for (var i = 0; i < lengths.Count; i++)
        {
            total += S7Protocol.DataItemHeaderLength + lengths[i];
            if (i != lengths.Count - 1 && (lengths[i] & 1) == 1)
            {
                total++;
            }
        }

        return total;
    }

    private readonly record struct ResolvedTag(int TagIndex, S7Address Address, int ByteLength);

    private sealed class Block(S7Area area, ushort dbNumber, int start, int end)
    {
        public S7Area Area { get; } = area;

        public ushort DbNumber { get; } = dbNumber;

        public int Start { get; } = start;

        public int End { get; set; } = end;

        public int Length => End - Start;

        public List<ResolvedTag> Members { get; } = [];
    }
}
