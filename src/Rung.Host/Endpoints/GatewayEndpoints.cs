using Microsoft.AspNetCore.Http.HttpResults;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Host.Endpoints;

/// <summary>网关的 REST 与 SSE 接口。</summary>
public static class GatewayEndpoints
{
    /// <summary>把全部接口挂到路由上。</summary>
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api").WithTags("Rung");

        api.MapGet("/health", GetHealth)
            .WithSummary("网关整体健康状况")
            .WithDescription("有设备掉线时 status 为 degraded。适合直接接监控探针。");

        api.MapGet("/devices", GetDevices)
            .WithSummary("全部设备的运行状况");

        api.MapGet("/devices/{deviceId}", GetDevice)
            .WithSummary("单台设备的运行状况");

        api.MapGet("/tags", GetTags)
            .WithSummary("点位最新值")
            .WithDescription("可用 device 与 prefix 过滤。prefix 按业务名前缀匹配。");

        api.MapGet("/tags/{tagName}", GetTag)
            .WithSummary("单个点位的最新值");

        api.MapPost("/tags/{tagName}/write", WriteTag)
            .WithSummary("写入点位")
            .WithDescription(
                "值按点位声明的数据类型解释。写完会记审计日志，并回读设备上的真实值。"
                + " 路径上显式写出 write 而不是用 PUT：这个动作会让产线上的机器真的动起来，"
                + " 一眼看得出比符合 REST 惯例更重要。");

        api.MapGet("/stream/tags", StreamTags)
            .WithSummary("点位变化的实时推送（SSE）")
            .WithDescription("只推送越过死区的变化。浏览器用 EventSource 订阅，自带断线重连。");

        return app;
    }

    private static Ok<HealthView> GetHealth(GatewayHost gateway, GatewayStartupTime startup)
    {
        var statuses = gateway.DeviceStatuses;
        var connected = statuses.Count(static s => s.State == DriverState.Connected);

        return TypedResults.Ok(new HealthView(
            connected == statuses.Count ? "healthy" : "degraded",
            statuses.Count,
            connected,
            gateway.Cache.Count,
            statuses.Sum(static s => s.Issues.Count),
            Math.Round((DateTime.UtcNow - startup.StartedUtc).TotalSeconds, 1)));
    }

    private static Ok<IReadOnlyList<DeviceView>> GetDevices(GatewayHost gateway)
        => TypedResults.Ok<IReadOnlyList<DeviceView>>(
            [.. gateway.DeviceStatuses.Select(DeviceView.From)]);

    private static Results<Ok<DeviceView>, NotFound<string>> GetDevice(
        GatewayHost gateway, string deviceId)
        => gateway.TryGetStatus(deviceId, out var status)
            ? TypedResults.Ok(DeviceView.From(status))
            : TypedResults.NotFound($"未知的设备 \"{deviceId}\"");

    private static Ok<IReadOnlyList<TagView>> GetTags(
        GatewayHost gateway, string? device = null, string? prefix = null)
    {
        var snapshots = gateway.Cache.Snapshot().AsEnumerable();

        if (!string.IsNullOrEmpty(device))
        {
            snapshots = snapshots.Where(s => string.Equals(s.DeviceId, device, StringComparison.Ordinal));
        }

        if (!string.IsNullOrEmpty(prefix))
        {
            snapshots = snapshots.Where(s => s.Tag.Name.StartsWith(prefix, StringComparison.Ordinal));
        }

        return TypedResults.Ok<IReadOnlyList<TagView>>([.. snapshots.Select(TagView.From)]);
    }

    private static Results<Ok<TagView>, NotFound<string>> GetTag(GatewayHost gateway, string tagName)
        => gateway.Cache.TryGet(tagName, out var snapshot)
            ? TypedResults.Ok(TagView.From(snapshot))
            : TypedResults.NotFound($"未知的点位 \"{tagName}\"");

    private static async Task<Results<Ok<TagView>, NotFound<string>, BadRequest<string>>> WriteTag(
        GatewayHost gateway,
        string tagName,
        WriteTagRequest request,
        CancellationToken cancellationToken)
    {
        if (!gateway.TryGetTag(tagName, out var tag))
        {
            return TypedResults.NotFound($"未知的点位 \"{tagName}\"");
        }

        if (tag.Access == TagAccess.Read)
        {
            return TypedResults.BadRequest($"点位 \"{tagName}\" 是只读的");
        }

        TagValue actual;
        try
        {
            var value = TagValueConverter.FromJson(request.Value, tag, DateTime.UtcNow);
            actual = await gateway.WriteAsync(tagName, value, cancellationToken).ConfigureAwait(false);
        }
        catch (RungException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }

        // 返回的是回读到的设备实际值，不是刚发出去的值。
        // PLC 可能对写入做钳位、取整，或被联锁逻辑改回去，操作员要看到真正生效的结果
        return TypedResults.Ok(TagView.From(new TagSnapshot(gateway.DeviceIdOf(tagName), tag, actual)));
    }

    private static ServerSentEventsResult<TagView> StreamTags(TagChangeBroadcaster broadcaster, CancellationToken cancellationToken)
        => TypedResults.ServerSentEvents(broadcaster.SubscribeAsync(cancellationToken), eventType: "tag");
}
