namespace Rung.Host;

/// <summary>
/// 进程启动时刻。
/// <para>
/// 用显式的单例而不是静态只读字段：静态字段的 <c>beforefieldinit</c> 语义
/// 允许运行时把初始化推迟到<b>首次读取</b>，于是第一次调用健康检查时
/// uptime 恒为 0——一个看起来无害、查起来很费劲的小 bug。
/// </para>
/// </summary>
/// <param name="StartedUtc">进程启动时刻，UTC。</param>
public sealed record GatewayStartupTime(DateTime StartedUtc)
{
    /// <summary>以当前时刻创建。</summary>
    public static GatewayStartupTime Now() => new(DateTime.UtcNow);
}
