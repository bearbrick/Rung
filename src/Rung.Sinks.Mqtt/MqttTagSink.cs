using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using Rung.Core;

namespace Rung.Sinks.Mqtt;

/// <summary>
/// 把变化的点位发布到 MQTT。
/// <para>
/// 与 Redis 输出的分工：Redis 是「拉」，应用要值的时候去读；MQTT 是「推」，
/// 适合订阅方分散、或者跨网段只能单向出流量的场景。两者可以同时开。
/// </para>
/// <para>
/// 两个 MQTT 特有的设计点在 <see cref="MqttSinkOptions.RetainTags"/> 和
/// <see cref="MqttSinkOptions.StatusTopic"/> 的注释里——保留消息和遗嘱消息，
/// 这两件事做不做，决定了订阅方能不能分清「值没变」和「网关死了」。
/// </para>
/// </summary>
public sealed class MqttTagSink : ITagSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private readonly IMqttClient _client;
    private readonly MqttSinkOptions _options;
    private readonly ILogger _logger;

    private bool _disposed;

    private MqttTagSink(IMqttClient client, MqttSinkOptions options, ILogger logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <summary>当前是否与 broker 保持连接。</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>连接 broker 并宣告上线。</summary>
    public static async Task<MqttTagSink> ConnectAsync(
        MqttSinkOptions options,
        ILogger<MqttTagSink>? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId(options.ClientId ?? $"rung-{Environment.MachineName}")
            .WithCleanSession()
            // 遗嘱消息：网关进程被杀、机器断电、网线被拔时，broker 替它发 offline。
            // 没有这条，订阅方无法区分「值一直没变」和「网关早就死了」——
            // 而这两件事在产线上的处置方式完全相反
            .WithWillTopic(options.StatusTopic)
            .WithWillPayload("offline"u8.ToArray())
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

        if (!string.IsNullOrEmpty(options.Username))
        {
            builder = builder.WithCredentials(options.Username, options.Password);
        }

        await client.ConnectAsync(builder.Build(), cancellationToken).ConfigureAwait(false);

        var sink = new MqttTagSink(client, options, (ILogger?)logger ?? NullLogger.Instance);
        await sink.PublishStatusAsync("online", cancellationToken).ConfigureAwait(false);

        return sink;
    }

    /// <inheritdoc/>
    public async ValueTask PublishAsync(
        IReadOnlyList<TagSnapshot> changed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (changed.Count == 0 || !_client.IsConnected)
        {
            return;
        }

        foreach (var snapshot in changed)
        {
            var payload = JsonSerializer.Serialize(new
            {
                v = snapshot.Value.ToObject(),
                q = snapshot.Value.Quality.ToString(),
                t = snapshot.Value.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                    CultureInfo.InvariantCulture),
                dev = snapshot.DeviceId,
                addr = snapshot.Tag.Address,
            }, PayloadOptions);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_options.TagTopic(snapshot.Tag.Name))
                .WithPayload(payload)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)_options.TagQos)
                .WithRetainFlag(_options.RetainTags)
                .Build();

            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }

        MqttLog.Published(_logger, changed.Count, _options.TopicPrefix);
    }

    /// <summary>把设备运行状况发布到 <c>{prefix}/device/{id}</c>。</summary>
    public async Task PublishDeviceStatusAsync(
        IReadOnlyList<DeviceStatus> statuses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_client.IsConnected)
        {
            return;
        }

        foreach (var status in statuses)
        {
            var payload = JsonSerializer.Serialize(new
            {
                state = status.State.ToString(),
                lastSuccess = status.LastSuccessUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                    CultureInfo.InvariantCulture),
                lastError = status.LastError,
                consecutiveFailures = status.ConsecutiveFailures,
                reconnects = status.ReconnectCount,
                overruns = status.OverrunCount,
                tags = status.ActiveTagCount,
                requests = status.RequestCount,
                issues = status.Issues.Count,
            }, PayloadOptions);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(_options.DeviceTopic(status.DeviceId))
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag()
                .Build();

            await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishStatusAsync(string status, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(_options.StatusTopic)
            .WithPayload(status)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();

        await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_client.IsConnected)
            {
                // 正常停机时主动发 offline 并干净断开。
                // 遗嘱消息只在异常断线时才由 broker 代发，两条路都要覆盖
                await PublishStatusAsync("offline", CancellationToken.None).ConfigureAwait(false);
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MQTTnet.Exceptions.MqttCommunicationException or IOException)
        {
            // 停机路径上连不上 broker 无所谓，遗嘱消息会兜底
        }

        _client.Dispose();
    }
}

internal static partial class MqttLog
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Debug,
        Message = "已向 MQTT 推送 {Count} 个变化点位（主题前缀 {Prefix}）")]
    public static partial void Published(ILogger logger, int count, string prefix);
}
