using Rung.Abstractions;

namespace Rung.Drivers.Modbus;

/// <summary>Modbus 协议的容量上限。超过就会被从站以异常码拒绝。</summary>
public static class ModbusLimits
{
    /// <summary>单次读取的最大寄存器数（功能码 03/04）。</summary>
    public const int MaxReadRegisters = 125;

    /// <summary>单次读取的最大位数（功能码 01/02）。</summary>
    public const int MaxReadBits = 2000;

    /// <summary>单次写入的最大寄存器数（功能码 16）。</summary>
    public const int MaxWriteRegisters = 123;

    /// <summary>该数据区单次读取的元素上限。</summary>
    public static int MaxReadCount(ModbusArea area)
        => area.IsBitArea() ? MaxReadBits : MaxReadRegisters;
}

/// <summary>一次 Modbus 读请求。</summary>
/// <param name="UnitId">从站号。</param>
/// <param name="Area">数据区。</param>
/// <param name="Start">起始偏移，0 基。</param>
/// <param name="Count">元素个数：位区按位计，寄存器区按寄存器计。</param>
public readonly record struct ModbusReadRequest(byte UnitId, ModbusArea Area, ushort Start, ushort Count);

/// <summary>一个点位在某次请求结果中的位置。</summary>
/// <param name="RequestIndex">所属请求下标；配置有问题的点位为 -1。</param>
/// <param name="Offset">位区为块内位序号，寄存器区为块内字节偏移。</param>
/// <param name="BitOffset">寄存器内的位偏移，仅寄存器区的布尔点位使用。</param>
/// <param name="HasBit">
/// 地址是否显式指定了位。寄存器里的布尔点位靠它区分两种语义：
/// 写了位就取那一位，没写就整个寄存器非零为真。
/// </param>
public readonly record struct ModbusTagLocation(
    int RequestIndex, int Offset, byte BitOffset, bool HasBit = false)
{
    /// <summary>配置有问题、未纳入采集的点位。</summary>
    public static ModbusTagLocation Invalid => new(-1, 0, 0);

    /// <summary>该点位是否参与采集。</summary>
    public bool IsValid => RequestIndex >= 0;
}

/// <summary>一份编译好的 Modbus 读取计划。</summary>
public sealed class ModbusReadPlan : IReadPlan
{
    internal ModbusReadPlan(
        IReadOnlyList<TagDef> tags,
        IReadOnlyList<ModbusReadRequest> requests,
        IReadOnlyList<ModbusTagLocation> locations,
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
    public IReadOnlyList<ModbusReadRequest> Requests { get; }

    /// <summary>每个点位的数据位置，与 <see cref="Tags"/> 同序等长。</summary>
    public IReadOnlyList<ModbusTagLocation> Locations { get; }

    /// <summary>每次请求覆盖了哪些点位。</summary>
    public IReadOnlyList<IReadOnlyList<int>> TagIndexesByRequest { get; }

    /// <summary>实际参与采集的点位数。</summary>
    public int ActiveTagCount => Tags.Count - Issues.Count;

    private static int[][] BuildReverseIndex(int requestCount, IReadOnlyList<ModbusTagLocation> locations)
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
