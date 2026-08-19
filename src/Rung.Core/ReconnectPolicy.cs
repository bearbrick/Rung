namespace Rung.Core;

/// <summary>
/// 断线重连的退避策略。
/// <para>
/// 这里的克制比激进重要得多：PLC 的连接资源非常有限（S7-300 通常只有十几条），
/// 断线后猛连会把它占满，反过来影响产线本身的通讯——
/// 网关本该是旁观者，不该因为自己连不上就去干扰生产。
/// </para>
/// </summary>
public sealed record ReconnectPolicy
{
    /// <summary>首次重试前的等待时间。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>退避上限。到顶之后就按这个间隔一直重试。</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>每次失败后的倍增系数。</summary>
    public double Multiplier { get; init; } = 2.0;

    /// <summary>
    /// 抖动比例，0 表示不抖动。
    /// <para>
    /// 这一条不是锦上添花：一台交换机重启会让挂在它下面的几十台设备<b>同时</b>断线，
    /// 没有抖动的话它们会整整齐齐地在同一毫秒发起重连，一波一波地冲击刚恢复的网络。
    /// 打散之后重连成功率明显更高。
    /// </para>
    /// </summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>默认策略：1s 起步，倍增到 30s 封顶，±20% 抖动。</summary>
    public static ReconnectPolicy Default { get; } = new();

    /// <summary>
    /// 计算第 <paramref name="attempt"/> 次重试前应当等待多久。
    /// </summary>
    /// <param name="attempt">失败次数，从 1 开始。</param>
    /// <param name="randomValue">[0,1) 区间的随机数。显式传入是为了让退避曲线可测。</param>
    public TimeSpan GetDelay(int attempt, double randomValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var exponent = Math.Min(attempt - 1, 32);
        var raw = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, exponent);

        // 先封顶再抖动：否则指数爆炸之后抖动幅度会大得离谱
        var capped = Math.Min(raw, MaxDelay.TotalMilliseconds);
        var jitter = 1.0 + (((randomValue * 2.0) - 1.0) * JitterRatio);

        return TimeSpan.FromMilliseconds(Math.Max(0, capped * jitter));
    }
}
