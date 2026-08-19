using System.Globalization;

namespace Rung.Simulator;

/// <summary>
/// 随时间变化的信号源。
/// <para>
/// 模拟器的价值一大半在这里：死值只能验证"链路通不通"，
/// 活的信号才能验证死区过滤、变化推送、趋势展示这些真正会出问题的地方。
/// </para>
/// <para>
/// 全部是时间的纯函数，因此可以直接对拍期望值，也保证多次运行结果可复现。
/// </para>
/// </summary>
public abstract class SignalGenerator
{
    /// <summary>求 <paramref name="elapsed"/> 时刻的值。</summary>
    public abstract double ValueAt(TimeSpan elapsed);

    /// <summary>
    /// 该信号是否会持续覆盖存储区。
    /// <para>
    /// 常量信号返回 false，于是客户端写进去的值能留住——
    /// 这正好对应现场的"设定值"点位。动态信号返回 true，模拟被 PLC 程序驱动的过程量。
    /// </para>
    /// </summary>
    public virtual bool Overwrites => true;

    /// <summary>按名字构造一个信号源。</summary>
    public static SignalGenerator Create(SignalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var period = TimeSpan.FromSeconds(config.PeriodSeconds <= 0 ? 1 : config.PeriodSeconds);

        return config.Generator?.ToLowerInvariant() switch
        {
            null or "constant" => new ConstantSignal(config.Value),
            "counter" => new CounterSignal(config.Value, config.Step == 0 ? 1 : config.Step, period),
            "ramp" => new RampSignal(config.Min, config.Max, period),
            "sine" => new SineSignal(config.Min, config.Max, period),
            "toggle" => new ToggleSignal(period),
            "randomwalk" => new RandomWalkSignal(
                config.Min, config.Max, config.Step == 0 ? 1 : config.Step, period, config.Seed),
            _ => throw new ArgumentException(
                $"未知的信号类型 \"{config.Generator}\"，可用：constant / counter / ramp / sine / toggle / randomwalk",
                nameof(config)),
        };
    }
}

/// <summary>恒定值。客户端写入后会保留，适合模拟设定值点位。</summary>
public sealed class ConstantSignal(double value) : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed) => value;

    /// <inheritdoc/>
    public override bool Overwrites => false;
}

/// <summary>单调递增的计数器，模拟产量、工件数这类只增不减的量。</summary>
public sealed class CounterSignal(double start, double step, TimeSpan period) : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed)
        => start + (step * Math.Floor(elapsed.TotalSeconds / period.TotalSeconds));
}

/// <summary>锯齿波：从 min 线性升到 max 后瞬间回落，模拟批次进度。</summary>
public sealed class RampSignal(double min, double max, TimeSpan period) : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed)
    {
        var phase = (elapsed.TotalSeconds % period.TotalSeconds) / period.TotalSeconds;
        return min + ((max - min) * phase);
    }
}

/// <summary>正弦波，模拟温度、压力这类连续波动的过程量。</summary>
public sealed class SineSignal(double min, double max, TimeSpan period) : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed)
    {
        var phase = 2 * Math.PI * elapsed.TotalSeconds / period.TotalSeconds;
        var normalized = (Math.Sin(phase) + 1) / 2;

        return min + ((max - min) * normalized);
    }
}

/// <summary>方波，模拟运行/停止这类布尔状态。</summary>
public sealed class ToggleSignal(TimeSpan period) : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed)
        => (long)(elapsed.TotalSeconds / period.TotalSeconds) % 2 == 0 ? 0 : 1;
}

/// <summary>
/// 有界随机游走。用固定种子由步数推导，因此<b>可复现</b>——
/// 排查问题时能重放出一模一样的数据序列。
/// </summary>
public sealed class RandomWalkSignal(double min, double max, double step, TimeSpan period, int seed)
    : SignalGenerator
{
    /// <inheritdoc/>
    public override double ValueAt(TimeSpan elapsed)
    {
        var steps = (long)(elapsed.TotalSeconds / period.TotalSeconds);
        var value = (min + max) / 2;
        var state = (uint)seed;

        for (long i = 0; i < steps && i < 100000; i++)
        {
            // xorshift：便宜且确定，不需要真随机的统计性质
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            value += (state & 1) == 0 ? step : -step;
            value = Math.Clamp(value, min, max);
        }

        return value;
    }
}

/// <summary>单个模拟信号的配置。</summary>
public sealed record SignalConfig
{
    /// <summary>S7 地址，如 <c>DB1.DBW0</c>。</summary>
    public required string Address { get; init; }

    /// <summary>数据类型：Bool / Int16 / UInt16 / Int32 / Float32 / Float64。</summary>
    public string Type { get; init; } = "Int16";

    /// <summary>信号类型：constant / counter / ramp / sine / toggle / randomwalk。</summary>
    public string? Generator { get; init; }

    /// <summary>常量值或计数器起点。</summary>
    public double Value { get; init; }

    /// <summary>波动下限。</summary>
    public double Min { get; init; }

    /// <summary>波动上限。</summary>
    public double Max { get; init; } = 100;

    /// <summary>计数器步长或随机游走步幅。</summary>
    public double Step { get; init; }

    /// <summary>周期，秒。</summary>
    public double PeriodSeconds { get; init; } = 1;

    /// <summary>随机游走的种子，保证可复现。</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>说明文字，只用于展示。</summary>
    public string? Description { get; init; }

    /// <inheritdoc/>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Address} {Type} {Generator ?? "constant"}");
}
