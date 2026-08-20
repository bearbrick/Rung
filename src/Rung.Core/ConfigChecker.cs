using System.Globalization;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>一台设备的离线校验结果。</summary>
/// <param name="DeviceId">设备标识。</param>
/// <param name="Protocol">协议。</param>
/// <param name="TagCount">点位总数。</param>
/// <param name="RequestCount">编译后每轮的请求次数。</param>
/// <param name="Issues">配置问题。</param>
public sealed record DeviceCheckResult(
    string DeviceId,
    string Protocol,
    int TagCount,
    int RequestCount,
    IReadOnlyList<TagIssue> Issues);

/// <summary>整份配置的校验结果。</summary>
/// <param name="Devices">逐设备的结果。</param>
/// <param name="DuplicateTagNames">跨设备重复的点位名。</param>
public sealed record ConfigCheckResult(
    IReadOnlyList<DeviceCheckResult> Devices,
    IReadOnlyList<string> DuplicateTagNames)
{
    /// <summary>问题总数。</summary>
    public int ProblemCount => Devices.Sum(static d => d.Issues.Count) + DuplicateTagNames.Count;

    /// <summary>点位总数。</summary>
    public int TagCount => Devices.Sum(static d => d.TagCount);

    /// <summary>每轮请求总次数。</summary>
    public int RequestCount => Devices.Sum(static d => d.RequestCount);
}

/// <summary>
/// 不连接设备的配置校验。
/// <para>
/// 走 <see cref="IDeviceDriverFactory.CompileOffline"/>，因此对所有协议通用：
/// 新增一种协议时，只要驱动工厂实现了这个方法，离线校验自动可用。
/// </para>
/// </summary>
public static class ConfigChecker
{
    /// <summary>校验一批设备注册项。</summary>
    public static ConfigCheckResult Check(
        IEnumerable<IDeviceDriverFactory> factories,
        IReadOnlyList<DeviceRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(factories);
        ArgumentNullException.ThrowIfNull(registrations);

        var byProtocol = factories.ToDictionary(
            static factory => factory.Protocol, StringComparer.OrdinalIgnoreCase);

        var results = new List<DeviceCheckResult>(registrations.Count);

        foreach (var registration in registrations)
        {
            var protocol = registration.Options.Protocol;

            if (!byProtocol.TryGetValue(protocol, out var factory))
            {
                var known = string.Join(" / ", byProtocol.Keys.Order(StringComparer.Ordinal));
                results.Add(new DeviceCheckResult(
                    registration.DeviceId, protocol, registration.Tags.Count, 0,
                    [new TagIssue(-1, registration.DeviceId,
                        $"未知的协议 \"{protocol}\"，可用：{known}")]));

                continue;
            }

            var plan = factory.CompileOffline(registration.Options, registration.Tags);

            results.Add(new DeviceCheckResult(
                registration.DeviceId, protocol, registration.Tags.Count,
                plan.RequestCount, plan.Issues));
        }

        return new ConfigCheckResult(results, FindDuplicateTagNames(registrations));
    }

    /// <summary>
    /// 跨设备的点位重名检查。
    /// <para>
    /// 单个设备内部的重名由各自的编译器发现，但跨设备的重名只有把全部配置
    /// 摊在一起才看得出来。重名会让写命令路由到错误的设备上——
    /// 这是产线上代价最大的一类配置错误，必须在出发前就拦住。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> FindDuplicateTagNames(
        IReadOnlyList<DeviceRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        return [.. registrations
            .SelectMany(static registration => registration.Tags
                .Where(static tag => tag.Enabled)
                .Select(tag => (tag.Name, registration.DeviceId)))
            .GroupBy(static pair => pair.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => string.Create(CultureInfo.InvariantCulture,
                $"{group.Key}（出现在 {string.Join("、", group.Select(static p => p.DeviceId))}）"))
            .Order(StringComparer.Ordinal)];
    }
}
