using Rung.Abstractions;
using Rung.Protocols.Melsec;
using Rung.Simulator;
using Xunit;

namespace Rung.Drivers.Melsec.Tests;

/// <summary>
/// 端到端测试：真实 TCP、真实 MC 3E 报文，对端是 Rung.Simulator 里
/// 独立实现的 MELSEC 模拟器。两边不同源，因此互为对照。
/// </summary>
public class MelsecDriverTests
{
    private static DeviceOptions Options(int port) => new()
    {
        DeviceId = "melsec",
        Protocol = "melsec-mc",
        Host = "127.0.0.1",
        Port = port,
        TimeoutMs = 3000,
    };

    private static TagDef Tag(
        string name, string address, TagDataType type,
        ByteOrder order = ByteOrder.ABCD, double scale = 1.0, TagAccess access = TagAccess.Read)
        => new()
        {
            Name = name, Address = address, DataType = type,
            ByteOrder = order, Scale = scale, Access = access,
        };

    private static MelsecSimulatorServer Simulator()
        => new(new SimulatedMelsecDeviceConfig { Name = "test", Port = 0 });

    private static async Task<TagValue[]> ReadAsync(MelsecDriver driver, params TagDef[] tags)
    {
        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        return values;
    }

    [Fact]
    public async Task 连接后可以读数据寄存器()
    {
        await using var server = Simulator();
        server.PokeWords("D100", 1234);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DriverState.Connected, driver.State);

        var values = await ReadAsync(driver, Tag("a", "D100", TagDataType.Int16, ByteOrder.DCBA));

        Assert.Equal(TagQuality.Good, values[0].Quality);
        Assert.Equal(1234, values[0].AsInt64());
    }

    [Fact]
    public async Task 三十二位值低字在前()
    {
        // MELSEC 的 32 位值占两个连续寄存器，D(n) 是低 16 位。
        // 配错字节序不会报错，只会读出一个看着像那么回事的数——
        // 这是三菱接入时第二容易踩的坑（第一是 X/Y 的十六进制编号）
        await using var server = Simulator();

        // 0x12345678：低字 0x5678 在 D200，高字 0x1234 在 D201
        server.PokeWords("D200", 0x5678, 0x1234);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "D200", TagDataType.Int32, ByteOrder.DCBA));

        Assert.Equal(0x12345678, values[0].AsInt64());
    }

    [Fact]
    public async Task 浮点数跨两个寄存器()
    {
        await using var server = Simulator();

        // 42.5f = 0x422A0000，低字 0x0000 在前
        server.PokeWords("D300", 0x0000, 0x422A);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "D300", TagDataType.Float32, ByteOrder.DCBA));

        Assert.Equal(42.5, values[0].AsDouble());
    }

    [Fact]
    public async Task 读内部继电器与输入继电器()
    {
        await using var server = Simulator();
        server.PokeBit("M100", true);
        server.PokeBit("M101", false);
        server.PokeBit("X1F", true);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("on", "M100", TagDataType.Bool),
            Tag("off", "M101", TagDataType.Bool),
            Tag("input", "X1F", TagDataType.Bool));

        Assert.True(values[0].AsBool());
        Assert.False(values[1].AsBool());
        Assert.True(values[2].AsBool());
    }

    [Fact]
    public async Task 位单位响应的半字节展开正确()
    {
        // 位单位下每字节装两个点，高半字节是前一个点。搞反了会整体错一位
        await using var server = Simulator();

        for (var i = 0; i < 8; i++)
        {
            server.PokeBit($"M{200 + i}", i % 3 == 0);
        }

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tags = Enumerable.Range(0, 8)
            .Select(i => Tag($"m{i}", $"M{200 + i}", TagDataType.Bool))
            .ToArray();

        var values = await ReadAsync(driver, tags);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(i % 3 == 0, values[i].AsBool());
        }
    }

    [Fact]
    public async Task 连续寄存器合并成一次请求()
    {
        await using var server = Simulator();
        server.PokeWords("D400", 10, 20, 30);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("a", "D400", TagDataType.Int16, ByteOrder.DCBA),
            Tag("b", "D401", TagDataType.Int16, ByteOrder.DCBA),
            Tag("c", "D402", TagDataType.Int16, ByteOrder.DCBA),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[3];
        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal([10, 20, 30], values.Select(static v => v.AsInt64()));
    }

    [Fact]
    public async Task 超过九百六十个字时切分()
    {
        // MC 3E 字单位单次上限就是 960 点
        await using var server = Simulator();
        server.PokeWords("D1500", 4242);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tags = Enumerable.Range(0, 2000)
            .Select(i => Tag($"t{i}", $"D{1000 + i}", TagDataType.Int16, ByteOrder.DCBA))
            .ToArray();

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];
        var good = await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.True(plan.RequestCount > 1, "应当被拆成多次请求");
        Assert.Equal(2000, good);
        Assert.Equal(4242, values[500].AsInt64());
    }

    [Fact]
    public async Task 线性换算在采集链路上生效()
    {
        await using var server = Simulator();
        server.PokeWords("D600", 2350);

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("temp", "D600", TagDataType.Int16, ByteOrder.DCBA, scale: 0.1));

        Assert.Equal(235.0, values[0].AsDouble(), precision: 10);
    }

    [Fact]
    public async Task 写数据寄存器()
    {
        await using var server = Simulator();

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("sp", "D700", TagDataType.Int16, ByteOrder.DCBA, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1234, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal([(ushort)1234], server.PeekWords("D700", 1));
    }

    [Fact]
    public async Task 写三十二位值时低字在前()
    {
        await using var server = Simulator();

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("v", "D800", TagDataType.Int32, ByteOrder.DCBA, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int32, 0x12345678, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal([(ushort)0x5678, (ushort)0x1234], server.PeekWords("D800", 2));
    }

    [Fact]
    public async Task 写内部继电器()
    {
        await using var server = Simulator();

        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("m", "M300", TagDataType.Bool, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken);

        Assert.True(server.PeekBit("M300"));
    }

    [Fact]
    public async Task 位软元件配成非布尔类型被拦下()
    {
        await using var server = Simulator();
        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var plan = (MelsecReadPlan)driver.CreateReadPlan([Tag("bad", "M100", TagDataType.Int16)]);

        Assert.Contains("只能配 Bool", Assert.Single(plan.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 只读点位拒绝写入()
    {
        await using var server = Simulator();
        await using var driver = new MelsecDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            Tag("ro", "D900", TagDataType.Int16),
            TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 连不上时状态转为故障()
    {
        await using var driver = new MelsecDriver(Options(1));

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await driver.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public void 驱动工厂声明协议名与地址语法()
    {
        var factory = new MelsecDriverFactory();

        Assert.Equal("melsec-mc", factory.Protocol);
        Assert.Contains("D100", factory.AddressSyntaxHint, StringComparison.Ordinal);
        Assert.Contains("DCBA", factory.AddressSyntaxHint, StringComparison.Ordinal);
    }
}
