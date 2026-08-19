namespace Rung.Abstractions;

/// <summary>点位的读写权限。</summary>
public enum TagAccess : byte
{
    /// <summary>只读。</summary>
    Read = 0,

    /// <summary>只写。</summary>
    Write = 1,

    /// <summary>可读可写。</summary>
    ReadWrite = 2,
}

/// <summary>
/// 一个点位的配置。
/// <para>
/// <see cref="Name"/> 是业务名（如 <c>Line1.Oven3.Temp</c>），应用侧只认它；
/// <see cref="Address"/> 是协议地址（如 <c>DB1.DBD20</c>），只有驱动关心。
/// 这层间接是自建网关最大的价值：电气改了 PLC 程序、地址变了，
/// 改一行配置即可，上层应用一行代码不用动。
/// </para>
/// </summary>
public sealed record TagDef
{
    /// <summary>业务点位名，全局唯一。北向接口对外暴露的就是它。</summary>
    public required string Name { get; init; }

    /// <summary>协议地址字符串，格式由驱动定义。</summary>
    public required string Address { get; init; }

    /// <summary>数据类型。</summary>
    public required TagDataType DataType { get; init; }

    /// <summary>
    /// 变长类型的长度：<see cref="TagDataType.String"/> 为字符数，
    /// <see cref="TagDataType.Bytes"/> 为字节数。定长类型忽略该字段。
    /// </summary>
    public int Length { get; init; }

    /// <summary>多字节数值的字节序。</summary>
    public ByteOrder ByteOrder { get; init; } = ByteOrder.ABCD;

    /// <summary>线性换算的倍率。工程值 = 原始值 * Scale + Offset。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>线性换算的偏移量。</summary>
    public double Offset { get; init; }

    /// <summary>
    /// 死区（绝对值）。变化量小于该值时不触发北向推送，用于抑制模拟量抖动。
    /// 0 表示每轮都推送。
    /// </summary>
    public double Deadband { get; init; }

    /// <summary>读写权限。</summary>
    public TagAccess Access { get; init; } = TagAccess.Read;

    /// <summary>
    /// 采集组名。同一设备下不同组独立调度，各有各的周期——
    /// 温度 5 秒一次、产量计数 500 ms 一次，互不干扰。
    /// </summary>
    public string PollGroup { get; init; } = "default";

    /// <summary>人类可读的描述，展示在 Web UI 上。</summary>
    public string? Description { get; init; }

    /// <summary>是否启用。停用的点位不参与采集计划。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>该点位一次读取需要的字节数。</summary>
    public int ByteLength => DataType.IsVariableLength() ? Length : DataType.SizeInBytes();
}
