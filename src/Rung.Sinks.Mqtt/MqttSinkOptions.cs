namespace Rung.Sinks.Mqtt;

/// <summary>MQTT 输出参数。</summary>
public sealed record MqttSinkOptions
{
    /// <summary>Broker 主机名或 IP。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Broker 端口。</summary>
    public int Port { get; init; } = 1883;

    /// <summary>客户端标识。留空则用 <c>rung-{机器名}</c>。</summary>
    public string? ClientId { get; init; }

    /// <summary>用户名，不需要认证时留空。</summary>
    public string? Username { get; init; }

    /// <summary>密码。</summary>
    public string? Password { get; init; }

    /// <summary>主题前缀，与服务名、Redis key 前缀保持一致。</summary>
    public string TopicPrefix { get; init; } = "rung";

    /// <summary>
    /// 点位消息的 QoS。默认 0：点位值是高频且幂等的，
    /// 丢一帧下一轮就补上了，为此付 QoS 1 的往返代价不划算。
    /// </summary>
    public int TagQos { get; init; }

    /// <summary>
    /// 点位消息是否保留。
    /// <para>
    /// 默认开启，这是 MQTT 侧对应 Redis 缓存的机制：新订阅者一连上
    /// 就能立刻拿到每个点位的最后已知值，而不必干等到下一次变化。
    /// 关掉它，一个刚启动的应用要等到点位下次变动才知道当前值是多少——
    /// 对温度这类几分钟才动一次的量，这个等待是不可接受的。
    /// </para>
    /// </summary>
    public bool RetainTags { get; init; } = true;

    /// <summary>点位主题：<c>{prefix}/tag/{业务名}</c>。</summary>
    public string TagTopic(string tagName) => $"{TopicPrefix}/tag/{tagName}";

    /// <summary>设备状态主题。</summary>
    public string DeviceTopic(string deviceId) => $"{TopicPrefix}/device/{deviceId}";

    /// <summary>
    /// 网关在线状态主题。用遗嘱消息保证：网关进程被杀、机器断电、网络中断时，
    /// broker 会替它发出 offline。
    /// </summary>
    public string StatusTopic => $"{TopicPrefix}/status";
}
