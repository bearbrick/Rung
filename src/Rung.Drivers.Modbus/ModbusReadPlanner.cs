using System.Globalization;
using Rung.Abstractions;

namespace Rung.Drivers.Modbus;

/// <summary>Modbus 读取计划的编译选项。</summary>
public sealed record ModbusReadPlannerOptions
{
    /// <summary>默认从站号，点位地址未显式指定时使用。</summary>
    public byte DefaultUnitId { get; init; } = 1;

    /// <summary>
    /// 两个点位之间允许跨越的最大空洞，单位与所在区一致（位或寄存器）。
    /// <para>
    /// Modbus 的合并收益比 S7 大得多：每次请求都是一个完整的 TCP 往返，
    /// 没有 S7 那种"一个报文塞多个数据项"的机制。因此默认值取得比 S7 激进——
    /// 多读几个寄存器的代价，远小于多一次往返。
    /// </para>
    /// </summary>
    public int MaxGapRegisters { get; init; } = 16;

    /// <summary>默认选项。</summary>
    public static ModbusReadPlannerOptions Default { get; } = new();
}

/// <summary>
/// 把一批点位编译成最少的 Modbus 读请求。
/// <para>
/// 按 (从站号, 数据区) 分组，组内按偏移排序后合并相邻区块，
/// 再按协议上限切分：寄存器 125 个、位 2000 个。
/// </para>
/// </summary>
public static class ModbusReadPlanner
{
    /// <summary>编译一份读取计划。</summary>
    public static ModbusReadPlan Create(
        IReadOnlyList<TagDef> tags,
        ModbusReadPlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= ModbusReadPlannerOptions.Default;

        var issues = new List<TagIssue>();
        var locations = new ModbusTagLocation[tags.Count];
        Array.Fill(locations, ModbusTagLocation.Invalid);

        var resolved = Resolve(tags, options, issues);
        var blocks = MergeIntoBlocks(resolved, options.MaxGapRegisters);
        var requests = Emit(blocks, locations);

        return new ModbusReadPlan(tags, requests, locations, issues);
    }

    private static List<ResolvedTag> Resolve(
        IReadOnlyList<TagDef> tags,
        ModbusReadPlannerOptions options,
        List<TagIssue> issues)
    {
        var resolved = new List<ResolvedTag>(tags.Count);

        for (var i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];

            if (!ModbusAddressParser.TryParse(tag.Address, options.DefaultUnitId, out var address, out var reason))
            {
                issues.Add(new TagIssue(i, tag.Name, reason));
                continue;
            }

            if (address.Area.IsBitArea())
            {
                // 线圈和离散输入只有 0/1，从里面读整数没有意义，多半是地址区搞错了
                if (tag.DataType != TagDataType.Bool)
                {
                    issues.Add(new TagIssue(i, tag.Name,
                        $"{address.Area} 是位区，只能配 Bool，实际配了 {tag.DataType}"));
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

            // Modbus 寄存器是 16 位的，奇数字节长度要向上取整到整寄存器
            var registers = (tag.ByteLength + 1) / 2;
            if (registers > ModbusLimits.MaxReadRegisters)
            {
                issues.Add(new TagIssue(i, tag.Name, string.Create(CultureInfo.InvariantCulture,
                    $"长度 {registers} 个寄存器超过单次读取上限 {ModbusLimits.MaxReadRegisters}，请拆分成多个点位")));
                continue;
            }

            resolved.Add(new ResolvedTag(i, address, registers));
        }

        return resolved;
    }

    private static List<Block> MergeIntoBlocks(List<ResolvedTag> resolved, int maxGap)
    {
        resolved.Sort(static (a, b) =>
        {
            var c = a.Address.UnitId.CompareTo(b.Address.UnitId);
            if (c != 0) { return c; }

            c = ((byte)a.Address.Area).CompareTo((byte)b.Address.Area);
            if (c != 0) { return c; }

            c = a.Address.Offset.CompareTo(b.Address.Offset);
            if (c != 0) { return c; }

            c = b.Length.CompareTo(a.Length);
            return c != 0 ? c : a.TagIndex.CompareTo(b.TagIndex);
        });

        var blocks = new List<Block>();
        Block? current = null;

        foreach (var tag in resolved)
        {
            var start = tag.Address.Offset;
            var end = start + tag.Length;
            var limit = ModbusLimits.MaxReadCount(tag.Address.Area);

            if (current is not null
                && current.UnitId == tag.Address.UnitId
                && current.Area == tag.Address.Area
                && start - current.End <= maxGap
                && Math.Max(current.End, end) - current.Start <= limit)
            {
                current.End = Math.Max(current.End, end);
                current.Members.Add(tag);
                continue;
            }

            current = new Block(tag.Address.UnitId, tag.Address.Area, start, end);
            current.Members.Add(tag);
            blocks.Add(current);
        }

        return blocks;
    }

    /// <summary>
    /// 每个区块就是一次请求。
    /// <para>
    /// 与 S7 不同，Modbus 没有"一个报文塞多个数据项"的机制——
    /// 一次请求只能读一段连续地址，所以区块和请求是一一对应的。
    /// </para>
    /// </summary>
    private static List<ModbusReadRequest> Emit(List<Block> blocks, ModbusTagLocation[] locations)
    {
        var requests = new List<ModbusReadRequest>(blocks.Count);

        for (var requestIndex = 0; requestIndex < blocks.Count; requestIndex++)
        {
            var block = blocks[requestIndex];
            requests.Add(new ModbusReadRequest(
                block.UnitId, block.Area, (ushort)block.Start, (ushort)block.Length));

            foreach (var member in block.Members)
            {
                var relative = member.Address.Offset - block.Start;

                locations[member.TagIndex] = block.Area.IsBitArea()
                    ? new ModbusTagLocation(requestIndex, relative, 0)
                    // 寄存器区里记字节偏移：一个寄存器两个字节
                    : new ModbusTagLocation(
                        requestIndex, relative * 2, member.Address.BitOffset, member.Address.HasBit);
            }
        }

        return requests;
    }

    private readonly record struct ResolvedTag(int TagIndex, ModbusAddress Address, int Length);

    private sealed class Block(byte unitId, ModbusArea area, int start, int end)
    {
        public byte UnitId { get; } = unitId;

        public ModbusArea Area { get; } = area;

        public int Start { get; } = start;

        public int End { get; set; } = end;

        public int Length => End - Start;

        public List<ResolvedTag> Members { get; } = [];
    }
}
