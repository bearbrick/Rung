using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rung.Core;
using StackExchange.Redis;

namespace Rung.Sinks.Redis;

/// <summary>
/// 把变化的点位写进 Redis。
/// <para>
/// 键的形状是 <c>rung:tag:{业务名}</c> 的 Hash，字段为 v / q / t / dev / addr。
/// 应用侧只认业务名，不需要知道它挂在哪台 PLC 的哪个地址上。
/// </para>
/// <para>
/// 选 Redis 做主力北向接口的理由：网关和应用完全解耦，网关重启不影响应用
/// 读到最后已知值；应用侧不用轮询网关的 HTTP 接口，延迟更低；
/// 而且大多数工厂项目本来就有 Redis，不必再引入新组件。
/// </para>
/// </summary>
public sealed class RedisTagSink : ITagSink, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly bool _ownsConnection;
    private readonly RedisSinkOptions _options;
    private readonly ILogger _logger;
    private readonly RedisChannel _channel;

    private bool _disposed;

    private RedisTagSink(
        IConnectionMultiplexer connection,
        bool ownsConnection,
        RedisSinkOptions options,
        ILogger logger)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _options = options;
        _logger = logger;
        _channel = RedisChannel.Literal(options.ResolvedChannel);
    }

    /// <summary>
    /// 建立连接并创建输出。
    /// <para>
    /// 用 <c>abortConnect=false</c>：Redis 暂时不可用时网关照常采集，
    /// 数据先进本地缓存，Redis 回来之后自动续上。
    /// 采集是第一优先级，输出是尽力而为。
    /// </para>
    /// </summary>
    public static async Task<RedisTagSink> ConnectAsync(
        RedisSinkOptions options,
        ILogger<RedisTagSink>? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuration = ConfigurationOptions.Parse(options.ConnectionString);
        configuration.AbortOnConnectFail = false;

        var connection = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new RedisTagSink(
            connection, ownsConnection: true, options, (ILogger?)logger ?? NullLogger.Instance);
    }

    /// <summary>用一个已有的连接创建输出，连接的生命周期由调用方负责。</summary>
    public static RedisTagSink UseConnection(
        IConnectionMultiplexer connection,
        RedisSinkOptions options,
        ILogger<RedisTagSink>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        return new RedisTagSink(
            connection, ownsConnection: false, options, (ILogger?)logger ?? NullLogger.Instance);
    }

    /// <inheritdoc/>
    public async ValueTask PublishAsync(
        IReadOnlyList<TagSnapshot> changed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (changed.Count == 0)
        {
            return;
        }

        var database = _options.Database >= 0
            ? _connection.GetDatabase(_options.Database)
            : _connection.GetDatabase();

        // 用批处理而不是逐条 await：一轮上百个点位，逐条往返会把延迟放大到不可接受
        var batch = database.CreateBatch();
        var pending = new List<Task>(changed.Count * 2);

        foreach (var snapshot in changed)
        {
            HashEntry[] entries =
            [
                new(RedisValueFormat.ValueField, RedisValueFormat.FormatValue(snapshot.Value)),
                new(RedisValueFormat.QualityField, snapshot.Value.Quality.ToString()),
                new(RedisValueFormat.TimestampField,
                    RedisValueFormat.FormatTimestamp(snapshot.Value.TimestampUtc)),
                new(RedisValueFormat.DeviceField, snapshot.DeviceId),
                new(RedisValueFormat.AddressField, snapshot.Tag.Address),
            ];

            pending.Add(batch.HashSetAsync(_options.TagKey(snapshot.Tag.Name), entries));

            if (_options.PublishChanges)
            {
                pending.Add(batch.PublishAsync(
                    _channel, RedisValueFormat.BuildChangeMessage(snapshot)));
            }
        }

        batch.Execute();

        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);

        RedisLog.Published(_logger, changed.Count, _options.KeyPrefix);
    }

    /// <summary>
    /// 把设备运行状况写进 <c>rung:device:{id}</c>。
    /// 运维侧只要 <c>redis-cli HGETALL</c> 就能看到每台设备连没连上、上次成功采集是什么时候。
    /// </summary>
    public async Task PublishDeviceStatusAsync(
        IReadOnlyList<DeviceStatus> statuses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (statuses.Count == 0)
        {
            return;
        }

        var database = _options.Database >= 0
            ? _connection.GetDatabase(_options.Database)
            : _connection.GetDatabase();

        var batch = database.CreateBatch();
        var pending = new List<Task>(statuses.Count);

        foreach (var status in statuses)
        {
            HashEntry[] entries =
            [
                new("state", status.State.ToString()),
                new("lastSuccess", status.LastSuccessUtc is { } success
                    ? RedisValueFormat.FormatTimestamp(success)
                    : string.Empty),
                new("lastError", status.LastError ?? string.Empty),
                new("consecutiveFailures", status.ConsecutiveFailures),
                new("reconnects", status.ReconnectCount),
                new("overruns", status.OverrunCount),
                new("tags", status.ActiveTagCount),
                new("requests", status.RequestCount),
                new("lastPollMs", Math.Round(status.LastPollDuration.TotalMilliseconds, 3)),
                new("issues", status.Issues.Count),
            ];

            pending.Add(batch.HashSetAsync(_options.DeviceKey(status.DeviceId), entries));
        }

        batch.Execute();
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static partial class RedisLog
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug,
        Message = "已向 Redis 推送 {Count} 个变化点位（前缀 {Prefix}）")]
    public static partial void Published(ILogger logger, int count, string prefix);
}
