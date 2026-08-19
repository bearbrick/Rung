using System.Globalization;

namespace Rung.Abstractions;

/// <summary>
/// 设备连接参数。通用字段直接给出，协议特有的参数（S7 的机架槽号、Modbus 的站号等）
/// 放进 <see cref="Extra"/>——这样新增协议不必修改本契约层。
/// </summary>
public sealed record DeviceOptions
{
    /// <summary>设备唯一标识，用于日志、指标和北向 key。</summary>
    public required string DeviceId { get; init; }

    /// <summary>协议标识，与 <see cref="IDeviceDriverFactory.Protocol"/> 匹配。</summary>
    public required string Protocol { get; init; }

    /// <summary>设备地址：IP 或主机名；串口协议下为串口名（如 <c>/dev/ttyUSB0</c>）。</summary>
    public required string Host { get; init; }

    /// <summary>端口号。串口协议忽略。</summary>
    public int Port { get; init; }

    /// <summary>单次请求超时（毫秒）。</summary>
    public int TimeoutMs { get; init; } = 3000;

    /// <summary>单次请求失败后的重试次数。连接级故障由重连状态机负责，不在此计数。</summary>
    public int RetryCount { get; init; } = 1;

    /// <summary>协议特有参数。</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取一个整型的协议特有参数，缺失或格式错误时返回 <paramref name="defaultValue"/>。</summary>
    public int GetInt32(string key, int defaultValue)
        => Extra.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    /// <summary>读取一个字符串型的协议特有参数。</summary>
    public string? GetString(string key)
        => Extra.TryGetValue(key, out var raw) ? raw : null;
}
