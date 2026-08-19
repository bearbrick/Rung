using System.Text.Json;
using System.Text.Json.Serialization;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Configuration;

/// <summary>
/// 采集配置。MVP 阶段用 JSON 文件，后续会迁到 SQLite + Web UI。
/// <para>
/// 支持两种写法：多设备用 <c>devices</c> 数组；只有一台设备时可以用
/// <c>device</c> + 顶层 <c>tags</c> 的简写。
/// </para>
/// </summary>
public sealed record RungConfig
{
    /// <summary>配置结构版本。将来做自动迁移时靠它判断。</summary>
    public int Version { get; init; } = 1;

    /// <summary>多设备写法。</summary>
    public IReadOnlyList<DeviceConfig>? Devices { get; init; }

    /// <summary>单设备简写，与顶层 <see cref="Tags"/> 配合使用。</summary>
    public DeviceConfig? Device { get; init; }

    /// <summary>单设备简写下的点位列表。</summary>
    public IReadOnlyList<TagConfig>? Tags { get; init; }

    /// <summary>默认采集周期，毫秒。设备可以各自覆盖。</summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// 各采集组的周期，毫秒。未列出的组用 <see cref="PollIntervalMs"/>。
    /// 温度 5 秒一次、产量计数 500 ms 一次，就靠这里分开。
    /// </summary>
    public Dictionary<string, int>? PollGroupIntervalMs { get; init; }

    /// <summary>断线重连参数。</summary>
    public ReconnectConfig? Reconnect { get; init; }

    /// <summary>Redis 北向输出。不配则只往控制台打印。</summary>
    public RedisConfig? Redis { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>从 JSON 文件加载配置。</summary>
    public static RungConfig Load(string path)
    {
        using var stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<RungConfig>(stream, SerializerOptions)
            ?? throw new InvalidDataException($"配置文件 {path} 解析结果为空");
    }

    /// <summary>
    /// 把两种写法归一成设备列表。
    /// <para>
    /// <b>空列表是合法状态</b>，不是错误：刚建好还没导入任何设备的数据库就是这样。
    /// 只有两种写法都缺席时才算配置有问题——那说明文件本身就不对。
    /// </para>
    /// </summary>
    public IReadOnlyList<DeviceConfig> ResolveDevices()
    {
        if (Devices is not null)
        {
            return Devices;
        }

        if (Device is not null)
        {
            return [Device with { Tags = Device.Tags ?? Tags ?? [] }];
        }

        throw new InvalidDataException(
            "配置里既没有 devices 数组，也没有 device 单设备段。"
            + "若用的是 SQLite，请先 rung config import 导入设备。");
    }

    /// <summary>为某台设备生成采集参数，设备级配置优先于全局。</summary>
    public DeviceWorkerOptions ToWorkerOptions(DeviceConfig device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var groups = device.PollGroupIntervalMs ?? PollGroupIntervalMs;
        var reconnect = device.Reconnect ?? Reconnect;

        return new DeviceWorkerOptions
        {
            DefaultPollInterval = TimeSpan.FromMilliseconds(device.PollIntervalMs ?? PollIntervalMs),
            PollGroupIntervals = groups?.ToDictionary(
                static kv => kv.Key,
                static kv => TimeSpan.FromMilliseconds(kv.Value),
                StringComparer.Ordinal) ?? new Dictionary<string, TimeSpan>(StringComparer.Ordinal),
            Reconnect = new ReconnectPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(reconnect?.InitialDelayMs ?? 1000),
                MaxDelay = TimeSpan.FromMilliseconds(reconnect?.MaxDelayMs ?? 30000),
                Multiplier = reconnect?.Multiplier ?? 2.0,
                JitterRatio = reconnect?.JitterRatio ?? 0.2,
            },
        };
    }
}

/// <summary>
/// Redis 北向输出配置。
/// <para>
/// 刻意只放纯数据，不提供转成 <c>RedisSinkOptions</c> 的方法——
/// 配置层反向依赖某个具体的北向实现，将来加 MQTT、InfluxDB 时会越缠越死。
/// 映射由宿主完成。
/// </para>
/// </summary>
public sealed record RedisConfig
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>连接字符串，StackExchange.Redis 格式。</summary>
    public string ConnectionString { get; init; } = "127.0.0.1:6379";

    /// <summary>键前缀，所有键长成 <c>{prefix}:tag:{业务名}</c>。</summary>
    public string KeyPrefix { get; init; } = "rung";

    /// <summary>数据库编号，-1 表示用连接字符串里的默认值。</summary>
    public int Database { get; init; } = -1;

    /// <summary>是否把变化推送到 Pub/Sub 频道。</summary>
    public bool PublishChanges { get; init; } = true;

    /// <summary>频道名，缺省为 <c>{prefix}:changes</c>。</summary>
    public string? ChannelName { get; init; }

    /// <summary>设备状态的上报周期，秒。0 表示不上报。</summary>
    public int StatusIntervalSeconds { get; init; } = 10;
}

/// <summary>断线重连配置。</summary>
public sealed record ReconnectConfig
{
    /// <summary>首次重试前的等待，毫秒。</summary>
    public int InitialDelayMs { get; init; } = 1000;

    /// <summary>退避上限，毫秒。</summary>
    public int MaxDelayMs { get; init; } = 30000;

    /// <summary>倍增系数。</summary>
    public double Multiplier { get; init; } = 2.0;

    /// <summary>抖动比例。多台设备同时断线时靠它错开重连。</summary>
    public double JitterRatio { get; init; } = 0.2;
}

/// <summary>设备连接配置。</summary>
public sealed record DeviceConfig
{
    /// <summary>设备唯一标识。</summary>
    public required string DeviceId { get; init; }

    /// <summary>协议标识，目前支持 s7。</summary>
    public string Protocol { get; init; } = "s7";

    /// <summary>IP 或主机名。</summary>
    public required string Host { get; init; }

    /// <summary>端口，S7 默认 102。</summary>
    public int Port { get; init; } = 102;

    /// <summary>单次请求超时，毫秒。</summary>
    public int TimeoutMs { get; init; } = 3000;

    /// <summary>单次请求失败后的重试次数。</summary>
    public int RetryCount { get; init; } = 1;

    /// <summary>协议特有参数，S7 用 rack / slot。</summary>
    public Dictionary<string, string>? Extra { get; init; }

    /// <summary>该设备的点位。</summary>
    public IReadOnlyList<TagConfig>? Tags { get; init; }

    /// <summary>覆盖全局的采集周期，毫秒。</summary>
    public int? PollIntervalMs { get; init; }

    /// <summary>覆盖全局的分组周期。</summary>
    public Dictionary<string, int>? PollGroupIntervalMs { get; init; }

    /// <summary>覆盖全局的重连参数。</summary>
    public ReconnectConfig? Reconnect { get; init; }

    /// <summary>转换成驱动需要的设备参数。</summary>
    public DeviceOptions ToDeviceOptions() => new()
    {
        DeviceId = DeviceId,
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        TimeoutMs = TimeoutMs,
        RetryCount = RetryCount,
        Extra = Extra ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>转换成驱动需要的点位定义。</summary>
    public IReadOnlyList<TagDef> ToTagDefs()
        => [.. (Tags ?? []).Where(static t => t.Enabled).Select(static t => new TagDef
        {
            Name = t.Name,
            Address = t.Address,
            DataType = t.DataType,
            Length = t.Length,
            ByteOrder = t.ByteOrder,
            Scale = t.Scale,
            Offset = t.Offset,
            Deadband = t.Deadband,
            Access = t.Access,
            PollGroup = t.PollGroup,
            Description = t.Description,
            Enabled = t.Enabled,
        })];
}

/// <summary>点位配置。</summary>
public sealed record TagConfig
{
    /// <summary>业务点位名，全局唯一。</summary>
    public required string Name { get; init; }

    /// <summary>协议地址。</summary>
    public required string Address { get; init; }

    /// <summary>数据类型。</summary>
    public required TagDataType DataType { get; init; }

    /// <summary>变长类型的长度。</summary>
    public int Length { get; init; }

    /// <summary>字节序。</summary>
    public ByteOrder ByteOrder { get; init; } = ByteOrder.ABCD;

    /// <summary>线性换算倍率。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>线性换算偏移。</summary>
    public double Offset { get; init; }

    /// <summary>死区。变化小于该值时不向北推送。</summary>
    public double Deadband { get; init; }

    /// <summary>读写权限。</summary>
    public TagAccess Access { get; init; } = TagAccess.Read;

    /// <summary>采集组。</summary>
    public string PollGroup { get; init; } = "default";

    /// <summary>描述。</summary>
    public string? Description { get; init; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; } = true;
}
