using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace Rung.Sinks.Mqtt.Tests;

/// <summary>收到的一条消息。</summary>
internal readonly record struct CapturedMessage(string Topic, string Payload, bool Retain, int Qos);

/// <summary>
/// 进程内的 MQTT broker，直接用 MQTTnet 自带的服务端。
/// <para>
/// 和 Modbus 那边一样：客户端和服务端本来就同源，这里不存在需要交叉验证的
/// 独立实现，用它反而省下一份没人维护的代码。价值在于开发机上不必装 broker。
/// </para>
/// </summary>
internal sealed class TestBroker : IAsyncDisposable
{
    /// <summary>
    /// 进程内单调递增的端口。绑定-释放-再绑定那套写法有竞态窗口，
    /// 在 Modbus 测试里已经吃过一次亏了。
    /// </summary>
    private static int _nextPort = Random.Shared.Next(43000, 60000);

    private readonly MqttServer _server;
    private bool _disposed;

    private TestBroker(MqttServer server, int port)
    {
        _server = server;
        Port = port;

        _server.InterceptingPublishAsync += args =>
        {
            lock (_gate)
            {
                _messages.Add(new CapturedMessage(
                    args.ApplicationMessage.Topic,
                    ReadPayload(args.ApplicationMessage),
                    args.ApplicationMessage.Retain,
                    (int)args.ApplicationMessage.QualityOfServiceLevel));
            }

            return Task.CompletedTask;
        };

        _server.ClientConnectedAsync += args =>
        {
            LastClientId = args.ClientId;
            return Task.CompletedTask;
        };
    }

    private static string ReadPayload(MqttApplicationMessage message)
    {
        ReadOnlySequence<byte> payload = message.Payload;
        return Encoding.UTF8.GetString(payload.ToArray());
    }

    public int Port { get; }

    /// <summary>
    /// 收到的消息，<b>按顺序</b>。
    /// <para>
    /// 不能用 ConcurrentBag：它的枚举顺序未定义，<c>Last()</c> 取到的是任意一条。
    /// 单条消息的主题碰巧看不出问题，但 online / offline 两条落在同一主题上时就错了。
    /// </para>
    /// </summary>
    public IReadOnlyList<CapturedMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    private readonly List<CapturedMessage> _messages = [];
    private readonly Lock _gate = new();

    /// <summary>最近一个连上来的客户端标识。</summary>
    public string? LastClientId { get; private set; }

    /// <summary>
    /// 从服务端强制踢掉客户端，模拟网关进程被杀或网络中断。
    /// <para>
    /// 客户端没机会发 DISCONNECT，按 MQTT 规范 broker 应当代发遗嘱消息。
    /// 直接验行为，比去检查"遗嘱配没配上"有说服力得多。
    /// </para>
    /// </summary>
    public async Task KillClientAsync()
    {
        if (LastClientId is { } clientId)
        {
            await _server.DisconnectClientAsync(clientId, MqttDisconnectReasonCode.UnspecifiedError);
        }
    }

    public static async Task<TestBroker> StartAsync()
    {
        var port = Interlocked.Increment(ref _nextPort);

        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(port)
            .Build();

        var server = new MqttServerFactory().CreateMqttServer(options);
        var broker = new TestBroker(server, port);

        await server.StartAsync();

        return broker;
    }

    /// <summary>
    /// 等某个主题上出现消息。
    /// <para>
    /// QoS 0 是发完即走、没有确认，<c>PublishAsync</c> 返回时 broker 未必处理完了。
    /// 断言前必须等一下——这是协议语义，不是测试写得不好。
    /// </para>
    /// </summary>
    public async Task<CapturedMessage> WaitForAsync(string topic, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (Last(topic) is { } message)
            {
                return message;
            }

            await Task.Delay(20, cancellationToken);
        }

        throw new TimeoutException(
            $"5 秒内没有等到主题 {topic} 上的消息。已收到：{string.Join("、", Messages.Select(static m => m.Topic).Distinct())}");
    }

    /// <summary>取出指定主题上最后一条消息。</summary>
    public CapturedMessage? Last(string topic)
    {
        lock (_gate)
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_messages[i].Topic, topic, StringComparison.Ordinal))
                {
                    return _messages[i];
                }
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _server.StopAsync();
        _server.Dispose();
    }
}
