using Microsoft.AspNetCore.Http.HttpResults;
using Rung.Abstractions;
using Rung.Configuration;
using Rung.Configuration.Storage;
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

        // 健康检查永远不认证：它要给监控探针和容器编排器用，
        // 那些东西不该也不方便持有密钥
        api.MapGet("/health", GetHealth)
            .WithSummary("网关整体健康状况")
            .WithDescription("有设备掉线时 status 为 degraded。适合直接接监控探针。");

        api.MapGet("/devices", GetDevices)
            .WithSummary("全部设备的运行状况")
            .RequireRead();

        api.MapGet("/devices/{deviceId}", GetDevice)
            .WithSummary("单台设备的运行状况")
            .RequireRead();

        api.MapGet("/tags", GetTags)
            .WithSummary("点位最新值")
            .WithDescription("可用 device 与 prefix 过滤。prefix 按业务名前缀匹配。")
            .RequireRead();

        api.MapGet("/tags/{tagName}", GetTag)
            .WithSummary("单个点位的最新值")
            .RequireRead();

        api.MapPost("/tags/{tagName}/write", WriteTag)
            .WithSummary("写入点位")
            .WithDescription(
                "值按点位声明的数据类型解释。写完会记审计日志，并回读设备上的真实值。"
                + " 路径上显式写出 write 而不是用 PUT：这个动作会让产线上的机器真的动起来，"
                + " 一眼看得出比符合 REST 惯例更重要。")
            .RequireWrite();

        api.MapGet("/stream/tags", StreamTags)
            .WithSummary("点位变化的实时推送（SSE）")
            .WithDescription("只推送越过死区的变化。浏览器用 EventSource 订阅，自带断线重连。")
            .RequireRead();

        api.MapPost("/config/reload", ReloadConfig)
            .WithSummary("从配置源重新加载")
            .WithDescription(
                "只有配置真的变了的设备会被重启，其余原地继续跑，采集不中断。"
                + " 校验失败时配置原封不动，不会留下一个改了一半的网关。")
            .RequireWrite();

        api.MapGet("/audit", GetAudit)
            .WithSummary("最近的写操作审计")
            .WithDescription(
                "谁、什么时候、往哪个点位写了什么值，以及设备回读到的实际值。"
                + " 失败的尝试同样留痕。")
            .RequireRead();

        // /metrics 按惯例挂在根上而不是 /api 下，抓取端默认就找这个路径
        app.MapGet("/metrics", GetMetrics)
            .WithTags("Rung")
            .WithSummary("Prometheus 指标")
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>把配置模型映射成设备注册项。</summary>
    public static IReadOnlyList<DeviceRegistration> ToRegistrations(RungConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return [.. config.ResolveDevices().Select(device => new DeviceRegistration(
            device.ToDeviceOptions(), device.ToTagDefs(), config.ToWorkerOptions(device)))];
    }

    private static async Task<Results<Ok<ReloadView>, BadRequest<string>>> ReloadConfig(
        GatewayHost gateway,
        IConfigStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var result = await gateway
                .ReloadAsync(ToRegistrations(config), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new ReloadView(
                store.Description, result.Added, result.Restarted, result.Removed, result.Unchanged));
        }
        catch (Exception ex) when (ex is RungException or IOException or InvalidDataException)
        {
            // 校验失败时网关状态没有任何改动，如实把原因回给调用方
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<WriteAuditRecord>>> GetAudit(
        IWriteAuditLog audit,
        CancellationToken cancellationToken,
        int limit = 100)
        => TypedResults.Ok(await audit
            .ReadRecentAsync(Math.Clamp(limit, 1, 1000), cancellationToken)
            .ConfigureAwait(false));

    /// <summary>记一条被拒绝的写尝试。</summary>
    private static ValueTask AuditRejectionAsync(
        IWriteAuditLog audit,
        string caller,
        string deviceId,
        string tagName,
        WriteTagRequest request,
        string reason,
        CancellationToken cancellationToken,
        TagDef? tag = null)
        => audit.RecordAsync(new WriteAuditRecord(
            DateTime.UtcNow,
            caller,
            deviceId,
            tagName,
            tag?.Address ?? string.Empty,
            tag?.DataType.ToString() ?? string.Empty,
            request.Value.ToString(),
            null,
            Success: false,
            reason), cancellationToken);

    private static ContentHttpResult GetMetrics(GatewayHost gateway)
        => TypedResults.Text(
            PrometheusFormatter.Render(gateway.DeviceStatuses, gateway.Cache, DateTime.UtcNow),
            "text/plain; version=0.0.4; charset=utf-8");

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
        HttpContext context,
        IWriteAuditLog audit,
        string tagName,
        WriteTagRequest request,
        CancellationToken cancellationToken)
    {
        var caller = context.GetCaller().Name;

        // 被拒绝的尝试同样留痕。「谁试图往一个只读点位写东西」是安全审计里
        // 最该看到的信号之一，只记成功的等于把它整个丢掉
        if (!gateway.TryGetTag(tagName, out var tag))
        {
            await AuditRejectionAsync(
                audit, caller, "unknown", tagName, request,
                $"未知的点位 \"{tagName}\"", cancellationToken).ConfigureAwait(false);

            return TypedResults.NotFound($"未知的点位 \"{tagName}\"");
        }

        if (tag.Access == TagAccess.Read)
        {
            await AuditRejectionAsync(
                audit, caller, gateway.DeviceIdOf(tagName), tagName, request,
                "点位是只读的", cancellationToken, tag).ConfigureAwait(false);

            return TypedResults.BadRequest($"点位 \"{tagName}\" 是只读的");
        }

        TagValue actual;
        try
        {
            var value = TagValueConverter.FromJson(request.Value, tag, DateTime.UtcNow);
            actual = await gateway
                .WriteAsync(tagName, value, cancellationToken, caller)
                .ConfigureAwait(false);
        }
        catch (RungException ex)
        {
            // 只有值转换失败要在这里补记；写到设备那一步失败由 DeviceWorker 自己记
            if (ex.Message.StartsWith("无法把", StringComparison.Ordinal))
            {
                await AuditRejectionAsync(
                    audit, caller, gateway.DeviceIdOf(tagName), tagName, request,
                    ex.Message, cancellationToken, tag).ConfigureAwait(false);
            }

            return TypedResults.BadRequest(ex.Message);
        }

        // 返回的是回读到的设备实际值，不是刚发出去的值。
        // PLC 可能对写入做钳位、取整，或被联锁逻辑改回去，操作员要看到真正生效的结果
        return TypedResults.Ok(TagView.From(new TagSnapshot(gateway.DeviceIdOf(tagName), tag, actual)));
    }

    private static ServerSentEventsResult<TagView> StreamTags(TagChangeBroadcaster broadcaster, CancellationToken cancellationToken)
        => TypedResults.ServerSentEvents(broadcaster.SubscribeAsync(cancellationToken), eventType: "tag");
}
