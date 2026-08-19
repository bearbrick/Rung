using Rung.Core;

namespace Rung.Host;

/// <summary>宿主自己的日志。和采集内核一样走源生成，禁用时零开销。</summary>
internal static partial class HostLog
{
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "采集网关启动，共 {DeviceCount} 台设备")]
    public static partial void GatewayStarting(ILogger logger, int deviceCount);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "采集网关已停止")]
    public static partial void GatewayStopped(ILogger logger);
}

/// <summary>
/// 承载采集网关的后台服务。
/// <para>
/// Web 接口和采集循环跑在同一个进程里——多一个进程就多一份部署和排障成本，
/// 而采集本身是纯 IO 密集的，和 Kestrel 抢不到什么资源。
/// </para>
/// </summary>
public sealed class GatewayService(
    GatewayHost gateway,
    ILogger<GatewayService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HostLog.GatewayStarting(logger, gateway.DeviceCount);

        try
        {
            await gateway.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            HostLog.GatewayStopped(logger);
        }
    }
}
