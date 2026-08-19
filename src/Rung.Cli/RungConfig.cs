using System.Text.Json;
using System.Text.Json.Serialization;
using Rung.Abstractions;
using Rung.Core;

namespace Rung.Cli;

/// <summary>采集配置。MVP 阶段用 JSON 文件，后续会迁到 SQLite + Web UI。</summary>
public sealed record RungConfig
{
    /// <summary>配置结构版本。将来做自动迁移时靠它判断。</summary>
    public int Version { get; init; } = 1;

    /// <summary>设备连接参数。</summary>
    public required DeviceConfig Device { get; init; }

    /// <summary>默认采集周期，毫秒。</summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// 各采集组的周期，毫秒。未列出的组用 <see cref="PollIntervalMs"/>。
    /// 温度 5 秒一次、产量计数 500 ms 一次，就靠这里分开。
    /// </summary>
    public Dictionary<string, int>? PollGroupIntervalMs { get; init; }

    /// <summary>断线重连参数。</summary>
    public ReconnectConfig? Reconnect { get; init; }

    /// <summary>点位列表。</summary>
    public required IReadOnlyList<TagConfig> Tags { get; init; }

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

    /// <summary>转换成驱动需要的设备参数。</summary>
    public DeviceOptions ToDeviceOptions() => new()
    {
        DeviceId = Device.DeviceId,
        Protocol = Device.Protocol,
        Host = Device.Host,
        Port = Device.Port,
        TimeoutMs = Device.TimeoutMs,
        RetryCount = Device.RetryCount,
        Extra = Device.Extra ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>转换成采集工作者需要的参数。</summary>
    public DeviceWorkerOptions ToWorkerOptions() => new()
    {
        DefaultPollInterval = TimeSpan.FromMilliseconds(PollIntervalMs),
        PollGroupIntervals = PollGroupIntervalMs?.ToDictionary(
            static kv => kv.Key,
            static kv => TimeSpan.FromMilliseconds(kv.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, TimeSpan>(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(Reconnect?.InitialDelayMs ?? 1000),
            MaxDelay = TimeSpan.FromMilliseconds(Reconnect?.MaxDelayMs ?? 30000),
            Multiplier = Reconnect?.Multiplier ?? 2.0,
            JitterRatio = Reconnect?.JitterRatio ?? 0.2,
        },
    };

    /// <summary>转换成驱动需要的点位定义。</summary>
    public IReadOnlyList<TagDef> ToTagDefs()
        => [.. Tags.Where(static t => t.Enabled).Select(static t => new TagDef
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
}

/// <summary>点位配置。</summary>
public sealed record TagConfig
{
    /// <summary>业务点位名。</summary>
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

    /// <summary>死区。</summary>
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
