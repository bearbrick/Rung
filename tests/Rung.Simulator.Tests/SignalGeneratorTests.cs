using Xunit;

namespace Rung.Simulator.Tests;

/// <summary>
/// 信号源是时间的纯函数，可以直接对拍。
/// 模拟器自己错了的话，用它跑出来的所有测试都会给出错误的安全感，
/// 所以它本身也得被测。
/// </summary>
public class SignalGeneratorTests
{
    private static SignalGenerator Create(string generator, Action<SignalConfigBuilder>? configure = null)
    {
        var builder = new SignalConfigBuilder { Generator = generator };
        configure?.Invoke(builder);

        return SignalGenerator.Create(builder.Build());
    }

    [Fact]
    public void 常量信号不覆盖存储区()
    {
        // 这条决定了客户端写进去的设定值能不能留住
        var signal = Create("constant", static b => b.Value = 42);

        Assert.False(signal.Overwrites);
        Assert.Equal(42, signal.ValueAt(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void 动态信号会持续覆盖存储区()
        => Assert.True(Create("sine").Overwrites);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 105)]
    [InlineData(2, 110)]
    [InlineData(10, 150)]
    public void 计数器按步长递增(double seconds, double expected)
    {
        var signal = Create("counter", static b =>
        {
            b.Value = 100;
            b.Step = 5;
            b.PeriodSeconds = 1;
        });

        Assert.Equal(expected, signal.ValueAt(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 50)]
    [InlineData(9.99, 99.9)]
    [InlineData(10, 0)]   // 锯齿波回落
    public void 锯齿波线性上升后归零(double seconds, double expected)
    {
        var signal = Create("ramp", static b =>
        {
            b.Min = 0;
            b.Max = 100;
            b.PeriodSeconds = 10;
        });

        Assert.Equal(expected, signal.ValueAt(TimeSpan.FromSeconds(seconds)), precision: 6);
    }

    [Theory]
    [InlineData(0, 50)]    // sin(0)=0 → 中点
    [InlineData(2.5, 100)] // 峰值
    [InlineData(7.5, 0)]   // 谷值
    public void 正弦波在配置区间内摆动(double seconds, double expected)
    {
        var signal = Create("sine", static b =>
        {
            b.Min = 0;
            b.Max = 100;
            b.PeriodSeconds = 10;
        });

        Assert.Equal(expected, signal.ValueAt(TimeSpan.FromSeconds(seconds)), precision: 6);
    }

    [Fact]
    public void 正弦波永不越界()
    {
        var signal = Create("sine", static b =>
        {
            b.Min = 2200;
            b.Max = 2500;
            b.PeriodSeconds = 7;
        });

        for (var t = 0.0; t < 30; t += 0.13)
        {
            var value = signal.ValueAt(TimeSpan.FromSeconds(t));
            Assert.InRange(value, 2200, 2500);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(8, 0)]
    public void 方波按周期翻转(double seconds, double expected)
    {
        var signal = Create("toggle", static b => b.PeriodSeconds = 4);

        Assert.Equal(expected, signal.ValueAt(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void 随机游走可复现且有界()
    {
        // 可复现很重要：排查问题时能重放出一模一样的数据序列
        var a = Create("randomwalk", static b =>
        {
            b.Min = 10;
            b.Max = 20;
            b.Step = 0.5;
            b.Seed = 999;
        });
        var c = Create("randomwalk", static b =>
        {
            b.Min = 10;
            b.Max = 20;
            b.Step = 0.5;
            b.Seed = 999;
        });

        for (var t = 0; t < 50; t++)
        {
            var moment = TimeSpan.FromSeconds(t);
            Assert.Equal(a.ValueAt(moment), c.ValueAt(moment));
            Assert.InRange(a.ValueAt(moment), 10, 20);
        }
    }

    [Fact]
    public void 不同种子给出不同的序列()
    {
        var a = Create("randomwalk", static b => b.Seed = 1);
        var c = Create("randomwalk", static b => b.Seed = 2);

        var differs = Enumerable.Range(1, 40).Any(t =>
            a.ValueAt(TimeSpan.FromSeconds(t)) != c.ValueAt(TimeSpan.FromSeconds(t)));

        Assert.True(differs);
    }

    [Fact]
    public void 未知信号类型给出可用的提示()
    {
        var ex = Assert.Throws<ArgumentException>(() => Create("perlin"));

        Assert.Contains("可用：", ex.Message, StringComparison.Ordinal);
        Assert.Contains("randomwalk", ex.Message, StringComparison.Ordinal);
    }

    private sealed class SignalConfigBuilder
    {
        public string? Generator { get; set; }

        public double Value { get; set; }

        public double Min { get; set; }

        public double Max { get; set; } = 100;

        public double Step { get; set; }

        public double PeriodSeconds { get; set; } = 1;

        public int Seed { get; set; } = 12345;

        public SignalConfig Build() => new()
        {
            Address = "DB1.DBW0",
            Generator = Generator,
            Value = Value,
            Min = Min,
            Max = Max,
            Step = Step,
            PeriodSeconds = PeriodSeconds,
            Seed = Seed,
        };
    }
}
