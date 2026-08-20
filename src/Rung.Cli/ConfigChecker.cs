using System.Globalization;
using Rung.Abstractions;
using Rung.Configuration;
using Rung.Drivers.Modbus;
using Rung.Protocols.S7;

namespace Rung.Cli;

/// <summary>一台设备的离线校验结果。</summary>
/// <param name="DeviceId">设备标识。</param>
/// <param name="Protocol">协议。</param>
/// <param name="TagCount">点位总数。</param>
/// <param name="RequestCount">编译后每轮的请求次数。</param>
/// <param name="FetchedBytes">每轮取回的字节数；Modbus 下为 0（按寄存器计，不换算）。</param>
/// <param name="Issues">配置问题。</param>
public sealed record DeviceCheckResult(
    string DeviceId,
    string Protocol,
    int TagCount,
    int RequestCount,
    int FetchedBytes,
    IReadOnlyList<TagIssue> Issues);

/// <summary>
/// 不连接设备的配置校验。
/// <para>
/// 地址解析、类型与地址宽度是否匹配、点位是否重名、批量合并成几次请求——
/// 这些全是纯逻辑，没有任何理由等到现场连上 PLC 才发现。
/// 出差前跑一遍，能省掉的是"到了现场才知道点位表有二十个地址写错"。
/// </para>
/// <para>
/// PDU 长度按最保守的 240 假设：真机协商出来只会更大，
/// 因此这里算出的请求次数是上界，不会给人过于乐观的印象。
/// </para>
/// </summary>
internal static class ConfigChecker
{
    /// <summary>S7-300 的协商值，也是所有西门子 CPU 里最小的。</summary>
    private const int ConservativePduLength = 240;

    public static IReadOnlyList<DeviceCheckResult> Check(RungConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var results = new List<DeviceCheckResult>();

        foreach (var device in config.ResolveDevices())
        {
            var tags = device.ToTagDefs();

            results.Add(device.Protocol.ToLowerInvariant() switch
            {
                "modbus-tcp" => CheckModbus(device, tags),
                "s7" => CheckS7(device, tags),
                _ => new DeviceCheckResult(device.DeviceId, device.Protocol, tags.Count, 0, 0,
                    [new TagIssue(-1, device.DeviceId, $"未知的协议 \"{device.Protocol}\"，可用：s7 / modbus-tcp")]),
            });
        }

        return results;
    }

    /// <summary>
    /// 跨设备的点位重名检查。
    /// <para>
    /// 单个设备内部的重名由各自的编译器发现，但跨设备的重名只有把全部配置
    /// 摊在一起才看得出来。重名会让写命令路由到错误的设备上——
    /// 这是产线上代价最大的一类配置错误，必须在出发前就拦住。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindDuplicateTagNames(RungConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return [.. config.ResolveDevices()
            .SelectMany(static device => (device.Tags ?? [])
                .Where(static tag => tag.Enabled)
                .Select(tag => (tag.Name, device.DeviceId)))
            .GroupBy(static pair => pair.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => string.Create(CultureInfo.InvariantCulture,
                $"{group.Key}（出现在 {string.Join("、", group.Select(static p => p.DeviceId))}）"))
            .Order(StringComparer.Ordinal)];
    }

    private static DeviceCheckResult CheckS7(DeviceConfig device, IReadOnlyList<TagDef> tags)
    {
        var maxGap = device.Extra is not null
            && device.Extra.TryGetValue("maxGapBytes", out var raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 8;

        var plan = S7ReadPlanner.Create(
            tags, ConservativePduLength, new S7ReadPlannerOptions { MaxGapBytes = maxGap });

        return new DeviceCheckResult(
            device.DeviceId, device.Protocol, tags.Count,
            plan.RequestCount, plan.TotalFetchedBytes, plan.Issues);
    }

    private static DeviceCheckResult CheckModbus(DeviceConfig device, IReadOnlyList<TagDef> tags)
    {
        var unitId = device.Extra is not null
            && device.Extra.TryGetValue("unitId", out var raw)
            && byte.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (byte)1;

        var plan = ModbusReadPlanner.Create(
            tags, new ModbusReadPlannerOptions { DefaultUnitId = unitId });

        return new DeviceCheckResult(
            device.DeviceId, device.Protocol, tags.Count, plan.RequestCount, 0, plan.Issues);
    }
}
