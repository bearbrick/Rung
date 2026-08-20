using System.Globalization;
using System.Text;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>
/// 一台设备的完整注册信息。
/// <para>
/// 带一个 <see cref="Signature"/> 用于热重载时的变更检测：只有签名变了的设备
/// 才需要重启工作者。没变的设备必须<b>原地继续跑</b>——重载配置时把所有设备
/// 都断一遍重连，代价比它要解决的问题还大。
/// </para>
/// </summary>
public sealed class DeviceRegistration
{
    /// <summary>创建一个注册项。</summary>
    public DeviceRegistration(
        DeviceOptions options,
        IReadOnlyList<TagDef> tags,
        DeviceWorkerOptions? workerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tags);

        Options = options;
        Tags = tags;
        WorkerOptions = workerOptions;
        Signature = ComputeSignature(options, tags, workerOptions);
    }

    /// <summary>设备连接参数。</summary>
    public DeviceOptions Options { get; }

    /// <summary>该设备的点位。</summary>
    public IReadOnlyList<TagDef> Tags { get; }

    /// <summary>采集参数。</summary>
    public DeviceWorkerOptions? WorkerOptions { get; }

    /// <summary>设备标识。</summary>
    public string DeviceId => Options.DeviceId;

    /// <summary>配置指纹。两次注册签名相同即视为无变化。</summary>
    public string Signature { get; }

    /// <summary>
    /// 计算配置指纹。
    /// <para>
    /// 手工拼字符串而不是靠 record 的结构相等：<see cref="DeviceOptions.Extra"/>
    /// 是字典、<see cref="Tags"/> 是列表，两者的默认相等都是引用比较，
    /// 结果会是"每次重载都判定成变了"，等于没做变更检测。
    /// </para>
    /// </summary>
    private static string ComputeSignature(
        DeviceOptions options, IReadOnlyList<TagDef> tags, DeviceWorkerOptions? workerOptions)
    {
        var builder = new StringBuilder(512);

        builder.Append(CultureInfo.InvariantCulture,
            $"{options.DeviceId}|{options.Protocol}|{options.Host}|{options.Port}|"
            + $"{options.TimeoutMs}|{options.RetryCount}");

        foreach (var (key, value) in options.Extra.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"|{key}={value}");
        }

        if (workerOptions is { } worker)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"|poll={worker.DefaultPollInterval.TotalMilliseconds}"
                + $"|retry={worker.Reconnect.InitialDelay.TotalMilliseconds}"
                + $"/{worker.Reconnect.MaxDelay.TotalMilliseconds}"
                + $"/{worker.Reconnect.Multiplier}/{worker.Reconnect.JitterRatio}");

            foreach (var (group, interval) in worker.PollGroupIntervals
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal))
            {
                builder.Append(CultureInfo.InvariantCulture, $"|{group}@{interval.TotalMilliseconds}");
            }
        }

        foreach (var tag in tags.OrderBy(static tag => tag.Name, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"\n{tag.Name}|{tag.Address}|{tag.DataType}|{tag.Length}|{tag.ByteOrder}|"
                + $"{tag.Scale}|{tag.Offset}|{tag.Deadband}|{tag.Access}|{tag.PollGroup}|{tag.Enabled}");
        }

        return builder.ToString();
    }
}

/// <summary>一次热重载的结果。</summary>
/// <param name="Added">新启动的设备。</param>
/// <param name="Restarted">配置变了、被重启的设备。</param>
/// <param name="Removed">被移除的设备。</param>
/// <param name="Unchanged">配置未变、原地继续跑的设备。</param>
public sealed record ReloadResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Restarted,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Unchanged)
{
    /// <summary>本次重载是否实际改动了任何东西。</summary>
    public bool HasChanges => Added.Count > 0 || Restarted.Count > 0 || Removed.Count > 0;

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"新增 {Added.Count}，重启 {Restarted.Count}，移除 {Removed.Count}，未变 {Unchanged.Count}");
}
