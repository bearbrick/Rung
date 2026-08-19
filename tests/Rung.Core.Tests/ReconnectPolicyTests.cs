using Xunit;

namespace Rung.Core.Tests;

public class ReconnectPolicyTests
{
    /// <summary>0.5 对应抖动区间的正中，即不抖动。</summary>
    private const double NoJitter = 0.5;

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 16000)]
    public void 退避时间按倍数增长(int attempt, double expectedMs)
    {
        var delay = ReconnectPolicy.Default.GetDelay(attempt, NoJitter);

        Assert.Equal(expectedMs, delay.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void 退避时间不超过上限()
    {
        // 断线一整夜也不能让重试间隔涨到几小时——恢复后要能及时接上
        var policy = ReconnectPolicy.Default;

        for (var attempt = 6; attempt < 200; attempt++)
        {
            Assert.True(policy.GetDelay(attempt, NoJitter) <= policy.MaxDelay);
        }
    }

    [Fact]
    public void 大量重试也不会数值溢出()
    {
        // 指数计算里 Math.Pow(2, 10000) 会变成无穷大，封顶必须在那之前生效
        var delay = ReconnectPolicy.Default.GetDelay(int.MaxValue, NoJitter);

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(1.0, 1.2)]
    public void 抖动落在配置的比例区间内(double randomValue, double expectedRatio)
    {
        var delay = ReconnectPolicy.Default.GetDelay(1, randomValue);

        Assert.Equal(1000 * expectedRatio, delay.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void 抖动在封顶之后施加()
    {
        // 先抖后封顶的话，指数爆炸阶段的抖动幅度会大得离谱
        var policy = ReconnectPolicy.Default;

        var low = policy.GetDelay(50, 0.0);
        var high = policy.GetDelay(50, 1.0);

        Assert.Equal(24000, low.TotalMilliseconds, precision: 3);
        Assert.Equal(36000, high.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void 关闭抖动时结果完全确定()
    {
        var policy = ReconnectPolicy.Default with { JitterRatio = 0 };

        Assert.Equal(policy.GetDelay(3, 0.0), policy.GetDelay(3, 0.99));
    }

    [Fact]
    public void 尝试次数必须从一开始()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ReconnectPolicy.Default.GetDelay(0, NoJitter));
}
