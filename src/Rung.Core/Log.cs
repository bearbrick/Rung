using Microsoft.Extensions.Logging;
using Rung.Abstractions;

namespace Rung.Core;

/// <summary>
/// 源生成的日志方法。
/// <para>
/// 采集循环每秒都在跑，用它而不是 <c>LogInformation(...)</c> 是有意义的：
/// 源生成器把参数直接写进结构化日志，禁用该级别时连字符串插值都不会发生，
/// 也不产生 object[] 装箱。设备一多，这个差别在 GC 上看得见。
/// </para>
/// </summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "设备 {DeviceId} 已连接，单帧上限 {MaxFrameBytes} 字节，{TagCount} 个点位编译成 {RequestCount} 次请求")]
    public static partial void DeviceConnected(
        ILogger logger, string deviceId, int maxFrameBytes, int tagCount, int requestCount);

    /// <summary>
    /// 重连警告只带错误<b>信息</b>，不带异常对象。
    /// <para>
    /// PLC 夜间维护重启、交换机抖一下，重连在产线上是家常便饭。
    /// 每次都甩一整个堆栈进日志，会把真正需要注意的东西淹掉。
    /// 完整异常降到 Debug 级，需要深挖时再打开。
    /// </para>
    /// </summary>
    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "设备 {DeviceId} 通讯中断（连续第 {Attempt} 次）：{Reason}，{Delay} 后重连")]
    public static partial void DeviceFaulted(
        ILogger logger, string deviceId, int attempt, string reason, TimeSpan delay);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug,
        Message = "设备 {DeviceId} 通讯中断的完整异常")]
    public static partial void DeviceFaultDetail(ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "设备 {DeviceId} 点位配置有误 —— {TagName}: {Reason}")]
    public static partial void TagConfigIssue(
        ILogger logger, string deviceId, string tagName, string reason);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug,
        Message = "释放设备 {DeviceId} 的驱动时出错")]
    public static partial void TeardownFailed(ILogger logger, Exception exception, string deviceId);

    /// <summary>
    /// 写命令审计。产线上出了事，这条日志是唯一能还原"谁、什么时候、往哪个点位写了什么"的东西。
    /// 级别刻意定为 Information，确保默认配置下就会被记下来。
    /// </summary>
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "写入 {DeviceId}/{TagName} = {Value}（地址 {Address}，调用方 {Caller}）")]
    public static partial void TagWritten(
        ILogger logger, string deviceId, string tagName, TagValue value, string address, string caller);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "写审计落盘失败 {DeviceId}/{TagName}——操作已执行但没有留痕，请立刻检查磁盘")]
    public static partial void AuditFailed(
        ILogger logger, Exception exception, string deviceId, string tagName);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error,
        Message = "写入 {DeviceId}/{TagName} 失败")]
    public static partial void TagWriteFailed(
        ILogger logger, Exception exception, string deviceId, string tagName);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Critical,
        Message = "设备 {DeviceId} 的工作循环意外退出，该设备将停止采集")]
    public static partial void DeviceLoopCrashed(ILogger logger, Exception exception, string deviceId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information,
        Message = "配置已在线重载：新增 {Added}，重启 {Restarted}，移除 {Removed}，未变 {Unchanged}")]
    public static partial void ConfigReloaded(
        ILogger logger, int added, int restarted, int removed, int unchanged);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Error,
        Message = "北向输出 {SinkName} 推送失败，采集不受影响")]
    public static partial void SinkFailed(ILogger logger, Exception exception, string sinkName);
}
