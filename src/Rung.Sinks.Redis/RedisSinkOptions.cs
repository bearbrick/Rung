namespace Rung.Sinks.Redis;

/// <summary>Redis 输出参数。</summary>
public sealed record RedisSinkOptions
{
    /// <summary>连接字符串，StackExchange.Redis 格式。</summary>
    public string ConnectionString { get; init; } = "127.0.0.1:6379";

    /// <summary>
    /// 键前缀。所有键都长成 <c>{prefix}:tag:{业务名}</c> 的样子。
    /// <para>
    /// 前缀要和仓库名、服务名、安装目录保持一致——这套东西一旦各叫各的，排障时会很难受。
    /// </para>
    /// </summary>
    public string KeyPrefix { get; init; } = "rung";

    /// <summary>数据库编号，-1 表示用连接字符串里的默认值。</summary>
    public int Database { get; init; } = -1;

    /// <summary>是否把变化推送到 Pub/Sub 频道。</summary>
    public bool PublishChanges { get; init; } = true;

    /// <summary>频道名，缺省为 <c>{prefix}:changes</c>。</summary>
    public string? ChannelName { get; init; }

    /// <summary>点位键的完整形式。</summary>
    public string TagKey(string tagName) => $"{KeyPrefix}:tag:{tagName}";

    /// <summary>设备状态键的完整形式。</summary>
    public string DeviceKey(string deviceId) => $"{KeyPrefix}:device:{deviceId}";

    /// <summary>变化推送频道。</summary>
    public string ResolvedChannel => ChannelName ?? $"{KeyPrefix}:changes";
}
