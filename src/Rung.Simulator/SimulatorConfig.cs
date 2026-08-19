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

/// <summary>模拟器配置：一个进程可以同时扮演多台设备。</summary>
public sealed record SimulatorConfig
{
    /// <summary>配置结构版本。</summary>
    public int Version { get; init; } = 1;

    /// <summary>要模拟的设备。</summary>
    public IReadOnlyList<SimulatedDeviceConfig> Devices { get; init; } = [];

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
