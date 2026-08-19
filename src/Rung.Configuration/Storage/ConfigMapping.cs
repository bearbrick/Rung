using System.Text.Json;
using Rung.Abstractions;

namespace Rung.Configuration.Storage;

/// <summary>数据库记录与内存配置模型之间的映射。</summary>
internal static class ConfigMapping
{
    private static readonly JsonSerializerOptions ExtraJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>把一台设备连同其点位映射成配置模型。</summary>
    public static DeviceConfig ToConfig(DeviceRecord device) => new()
    {
        DeviceId = device.DeviceId,
        Protocol = device.Protocol,
        Host = device.Host,
        Port = device.Port,
        TimeoutMs = device.TimeoutMs,
        RetryCount = device.RetryCount,
        Extra = ParseExtra(device.ExtraJson),
        PollIntervalMs = device.PollIntervalMs,
        Tags = [.. device.Tags.Select(ToConfig)],
    };

    /// <summary>把一个点位记录映射成配置模型。</summary>
    public static TagConfig ToConfig(TagRecord tag) => new()
    {
        Name = tag.Name,
        Address = tag.Address,
        DataType = Enum.Parse<TagDataType>(tag.DataType, ignoreCase: true),
        Length = tag.Length,
        ByteOrder = Enum.Parse<ByteOrder>(tag.ByteOrder, ignoreCase: true),
        Scale = tag.Scale,
        Offset = tag.Offset,
        Deadband = tag.Deadband,
        Access = Enum.Parse<TagAccess>(tag.Access, ignoreCase: true),
        PollGroup = tag.PollGroup,
        Description = tag.Description,
        Enabled = tag.Enabled,
    };

    /// <summary>把配置模型中的设备映射成数据库记录。</summary>
    public static DeviceRecord ToRecord(DeviceConfig device) => new()
    {
        DeviceId = device.DeviceId,
        Protocol = device.Protocol,
        Host = device.Host,
        Port = device.Port,
        TimeoutMs = device.TimeoutMs,
        RetryCount = device.RetryCount,
        ExtraJson = JsonSerializer.Serialize(
            device.Extra ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExtraJsonOptions),
        PollIntervalMs = device.PollIntervalMs,
        Tags = [.. (device.Tags ?? []).Select(ToRecord)],
    };

    /// <summary>把配置模型中的点位映射成数据库记录。</summary>
    public static TagRecord ToRecord(TagConfig tag) => new()
    {
        Name = tag.Name,
        Address = tag.Address,
        DataType = tag.DataType.ToString(),
        Length = tag.Length,
        ByteOrder = tag.ByteOrder.ToString(),
        Scale = tag.Scale,
        Offset = tag.Offset,
        Deadband = tag.Deadband,
        Access = tag.Access.ToString(),
        PollGroup = tag.PollGroup,
        Description = tag.Description,
        Enabled = tag.Enabled,
    };

    private static Dictionary<string, string> ParseExtra(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ExtraJsonOptions);
            return new Dictionary<string, string>(
                parsed ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // 手工改坏的 JSON 不该让整个网关起不来：当成空参数，
            // 后果是设备用默认的机架槽号连不上，日志里看得见
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
