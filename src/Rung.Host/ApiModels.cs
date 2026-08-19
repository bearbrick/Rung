using Rung.Abstractions;
using Rung.Core;

namespace Rung.Host;

/// <summary>
/// 一个点位的对外表示。
/// <para>
/// 刻意不暴露 <see cref="TagValue"/> 本身：它是为热路径设计的紧凑结构体，
/// 换成 JSON 友好的形状之后，接口契约才能独立于内部实现演进。
/// </para>
/// </summary>
/// <param name="Name">业务点位名。</param>
/// <param name="Value">当前值。质量不佳时为 null。</param>
/// <param name="Quality">采集质量。</param>
/// <param name="TimestampUtc">采集时刻，UTC。</param>
/// <param name="DeviceId">来源设备。</param>
/// <param name="Address">协议地址，便于现场核对。</param>
/// <param name="DataType">数据类型。</param>
/// <param name="Description">描述。</param>
public sealed record TagView(
    string Name,
    object? Value,
    string Quality,
    DateTime TimestampUtc,
    string DeviceId,
    string Address,
    string DataType,
    string? Description)
{
    /// <summary>从缓存快照构造。</summary>
    public static TagView From(TagSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new TagView(
            snapshot.Tag.Name,
            snapshot.Value.ToObject(),
            snapshot.Value.Quality.ToString(),
            snapshot.Value.TimestampUtc,
            snapshot.DeviceId,
            snapshot.Tag.Address,
            snapshot.Value.DataType.ToString(),
            snapshot.Tag.Description);
    }
}

/// <summary>一台设备的对外状况。</summary>
/// <param name="DeviceId">设备标识。</param>
/// <param name="State">连接状态。</param>
/// <param name="LastSuccessUtc">最近一次成功采集时刻。</param>
/// <param name="LastFailureUtc">最近一次失败时刻。</param>
/// <param name="LastError">最近一次错误描述。</param>
/// <param name="ConsecutiveFailures">连续失败次数。</param>
/// <param name="ReconnectCount">累计重连次数。</param>
/// <param name="LastPollMs">上一轮采集耗时，毫秒。</param>
/// <param name="NegotiatedPduLength">协商的 PDU 长度。</param>
/// <param name="ActiveTagCount">参与采集的点位数。</param>
/// <param name="RequestCount">每轮请求次数。</param>
/// <param name="OverrunCount">采集超时次数。</param>
/// <param name="Issues">配置有问题的点位。</param>
public sealed record DeviceView(
    string DeviceId,
    string State,
    DateTime? LastSuccessUtc,
    DateTime? LastFailureUtc,
    string? LastError,
    int ConsecutiveFailures,
    int ReconnectCount,
    double LastPollMs,
    int NegotiatedPduLength,
    int ActiveTagCount,
    int RequestCount,
    int OverrunCount,
    IReadOnlyList<TagIssueView> Issues)
{
    /// <summary>从内部状况构造。</summary>
    public static DeviceView From(DeviceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new DeviceView(
            status.DeviceId,
            status.State.ToString(),
            status.LastSuccessUtc,
            status.LastFailureUtc,
            status.LastError,
            status.ConsecutiveFailures,
            status.ReconnectCount,
            Math.Round(status.LastPollDuration.TotalMilliseconds, 3),
            status.NegotiatedPduLength,
            status.ActiveTagCount,
            status.RequestCount,
            status.OverrunCount,
            [.. status.Issues.Select(static issue => new TagIssueView(issue.TagName, issue.Reason))]);
    }
}

/// <summary>一个点位的配置问题。</summary>
/// <param name="TagName">点位名。</param>
/// <param name="Reason">原因，应当足以让电气工程师自己改对。</param>
public sealed record TagIssueView(string TagName, string Reason);

/// <summary>网关整体健康状况。</summary>
/// <param name="Status">healthy / degraded。有设备掉线即为 degraded。</param>
/// <param name="DeviceCount">设备总数。</param>
/// <param name="ConnectedCount">已连接的设备数。</param>
/// <param name="TagCount">缓存中的点位数。</param>
/// <param name="IssueCount">配置有问题的点位总数。</param>
/// <param name="UptimeSeconds">运行时长，秒。</param>
public sealed record HealthView(
    string Status,
    int DeviceCount,
    int ConnectedCount,
    int TagCount,
    int IssueCount,
    double UptimeSeconds);

/// <summary>写点位的请求体。</summary>
/// <param name="Value">要写入的工程值。网关按点位的数据类型自行转换。</param>
public sealed record WriteTagRequest(System.Text.Json.JsonElement Value);
