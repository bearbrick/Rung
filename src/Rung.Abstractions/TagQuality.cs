namespace Rung.Abstractions;

/// <summary>
/// 一次采集结果的质量。北向输出必须带上它——应用侧要能区分
/// "值是 0" 和 "读不到所以是 0"，这两者在产线上的后果完全不同。
/// </summary>
public enum TagQuality : byte
{
    /// <summary>从未成功采集过，值无意义。</summary>
    Uninitialized = 0,

    /// <summary>本轮采集成功，值可信。</summary>
    Good = 1,

    /// <summary>超过允许的最大陈旧时间仍未更新，值为最后一次已知值。</summary>
    Stale = 2,

    /// <summary>通讯故障：连接断开、超时、报文损坏。整批点位通常一起变成该状态。</summary>
    CommFailure = 3,

    /// <summary>
    /// 设备针对该点位单独返回了错误码（例如 S7 的 0x0A "对象不存在"）。
    /// 与 <see cref="CommFailure"/> 的区别在于链路是好的，是这个地址本身有问题。
    /// </summary>
    DeviceError = 4,

    /// <summary>点位配置本身有问题：地址解析失败、数据类型与地址宽度不匹配等。</summary>
    ConfigError = 5,
}
