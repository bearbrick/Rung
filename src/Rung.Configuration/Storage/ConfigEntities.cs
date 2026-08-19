using System.ComponentModel.DataAnnotations;

namespace Rung.Configuration.Storage;

/// <summary>
/// 设备配置表。
/// <para>
/// 枚举一律存字符串而不是整数：有人拿 SQLite 浏览器打开这个文件时，
/// 看到 <c>Float32</c> 比看到 <c>9</c> 有用得多。现场排障多半就是这么干的，
/// 省下的时间远大于那几个字节。
/// </para>
/// </summary>
public sealed class DeviceRecord
{
    /// <summary>自增主键。</summary>
    public int Id { get; set; }

    /// <summary>设备唯一标识，对外可见。</summary>
    [MaxLength(128)]
    public required string DeviceId { get; set; }

    /// <summary>协议标识：s7 / modbus-tcp。</summary>
    [MaxLength(32)]
    public required string Protocol { get; set; }

    /// <summary>IP 或主机名。</summary>
    [MaxLength(256)]
    public required string Host { get; set; }

    /// <summary>端口。</summary>
    public int Port { get; set; }

    /// <summary>单次请求超时，毫秒。</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>单次请求失败后的重试次数。</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>协议特有参数的 JSON，如 <c>{"rack":"0","slot":"1"}</c>。</summary>
    [MaxLength(2048)]
    public string ExtraJson { get; set; } = "{}";

    /// <summary>覆盖全局的采集周期，毫秒；为空则用全局值。</summary>
    public int? PollIntervalMs { get; set; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>描述。</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>该设备下的点位。</summary>
    public List<TagRecord> Tags { get; set; } = [];
}

/// <summary>点位配置表。</summary>
public sealed class TagRecord
{
    /// <summary>自增主键。</summary>
    public int Id { get; set; }

    /// <summary>所属设备。</summary>
    public int DeviceRecordId { get; set; }

    /// <summary>所属设备的导航属性。</summary>
    public DeviceRecord? Device { get; set; }

    /// <summary>业务点位名，全局唯一。应用侧只认它。</summary>
    [MaxLength(256)]
    public required string Name { get; set; }

    /// <summary>协议地址。</summary>
    [MaxLength(128)]
    public required string Address { get; set; }

    /// <summary>数据类型，存字符串。</summary>
    [MaxLength(16)]
    public required string DataType { get; set; }

    /// <summary>变长类型的长度。</summary>
    public int Length { get; set; }

    /// <summary>字节序，存字符串。</summary>
    [MaxLength(8)]
    public string ByteOrder { get; set; } = "ABCD";

    /// <summary>线性换算倍率。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>线性换算偏移。</summary>
    public double Offset { get; set; }

    /// <summary>死区。</summary>
    public double Deadband { get; set; }

    /// <summary>读写权限，存字符串。</summary>
    [MaxLength(16)]
    public string Access { get; set; } = "Read";

    /// <summary>采集组。</summary>
    [MaxLength(64)]
    public string PollGroup { get; set; } = "default";

    /// <summary>描述。</summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 全局设置，按键值存放。
/// <para>
/// 采集周期、重连参数、Redis 配置这些整体存成一条 JSON，而不是拆成几十列：
/// 它们是<b>配置</b>不是<b>数据</b>，没人会按 <c>jitterRatio</c> 做查询，
/// 拆列只会让每加一个选项就要建一次迁移。
/// </para>
/// </summary>
public sealed class SettingRecord
{
    /// <summary>设置键。</summary>
    [MaxLength(64)]
    public required string Key { get; set; }

    /// <summary>设置值，通常是 JSON。</summary>
    public required string Value { get; set; }
}
