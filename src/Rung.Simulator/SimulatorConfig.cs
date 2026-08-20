using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rung.Simulator;

/// <summary>一台模拟设备的配置。</summary>
public sealed record SimulatedDeviceConfig
{
    /// <summary>设备名，用于日志与展示。</summary>
    public string Name { get; init; } = "sim";

    /// <summary>监听端口。0 表示由系统分配，便于一个进程里起很多台。</summary>
    public int Port { get; init; } = 102;

    /// <summary>协商时返回的 PDU 长度。240 对应 S7-300，480 对应 S7-1200/1500。</summary>
    public ushort NegotiatedPduLength { get; init; } = 240;

    /// <summary>信号列表。</summary>
    public IReadOnlyList<SignalConfig> Signals { get; init; } = [];

    /// <summary>故障注入开关。</summary>
    public FaultInjection? Faults { get; init; }
}

/// <summary>内置 Redis 模拟器的配置。</summary>
public sealed record RedisSimulatorConfig
{
    /// <summary>是否启用。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>监听端口。0 表示由系统分配。</summary>
    public int Port { get; init; } = 6379;
}

/// <summary>模拟器配置：一个进程可以同时扮演多台设备。</summary>
public sealed record SimulatorConfig
{
    /// <summary>配置结构版本。</summary>
    public int Version { get; init; } = 1;

    /// <summary>要模拟的西门子 S7 设备。</summary>
    public IReadOnlyList<SimulatedDeviceConfig> Devices { get; init; } = [];

    /// <summary>要模拟的 Modbus TCP 从站。</summary>
    public IReadOnlyList<SimulatedModbusDeviceConfig> ModbusDevices { get; init; } = [];

    /// <summary>要模拟的三菱 MELSEC CPU。</summary>
    public IReadOnlyList<SimulatedMelsecDeviceConfig> MelsecDevices { get; init; } = [];

    /// <summary>
    /// 是否同时起一个最小 Redis，用于验证北向输出。
    /// 开发机上不装 Redis、不装 Docker 也能把整条链路跑通。
    /// </summary>
    public RedisSimulatorConfig? Redis { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>从 JSON 文件加载。</summary>
    public static SimulatorConfig Load(string path)
    {
        using var stream = File.OpenRead(path);

        return JsonSerializer.Deserialize<SimulatorConfig>(stream, SerializerOptions)
            ?? throw new InvalidDataException($"模拟器配置 {path} 解析结果为空");
    }
}
