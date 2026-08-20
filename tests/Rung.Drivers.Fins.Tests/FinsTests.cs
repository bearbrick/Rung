using Rung.Abstractions;
using Rung.Protocols.Fins;
using Rung.Simulator;
using Xunit;

namespace Rung.Drivers.Fins.Tests;

public class FinsAddressParserTests
{
    [Theory]
    [InlineData("D100", FinsArea.Dm, 100, 0, false)]
    [InlineData("D100.05", FinsArea.Dm, 100, 5, true)]
    [InlineData("D100.15", FinsArea.Dm, 100, 15, true)]
    [InlineData("CIO200", FinsArea.Cio, 200, 0, false)]
    [InlineData("W10.03", FinsArea.Work, 10, 3, true)]
    [InlineData("H5", FinsArea.Holding, 5, 0, false)]
    [InlineData("A50", FinsArea.Auxiliary, 50, 0, false)]
    [InlineData("200", FinsArea.Cio, 200, 0, false)]  // 裸数字按 CIO
    [InlineData("d100", FinsArea.Dm, 100, 0, false)]
    public void 解析常见地址(string input, FinsArea area, int word, int bit, bool hasBit)
    {
        var address = FinsAddressParser.Parse(input);

        Assert.Equal(area, address.Area);
        Assert.Equal(word, address.Word);
        Assert.Equal(bit, address.Bit);
        Assert.Equal(hasBit, address.HasBit);
    }

    [Fact]
    public void 全部按十进制解析()
    {
        // 欧姆龙这点比三菱省心：不存在按软元件分进制的坑
        Assert.Equal(10, FinsAddressParser.Parse("D10").Word);
        Assert.Equal(10, FinsAddressParser.Parse("CIO10").Word);
        Assert.Equal(10, FinsAddressParser.Parse("W10").Word);
    }

    [Fact]
    public void CIO要在C之前匹配()
        => Assert.Equal(FinsArea.Cio, FinsAddressParser.Parse("CIO100").Area);

    [Theory]
    [InlineData("", "地址为空")]
    [InlineData("Q100", "未知的存储区前缀")]
    [InlineData("D", "缺少字地址")]
    [InlineData("D100.16", "0-15")]
    [InlineData("DABC", "字地址")]
    public void 非法地址给出可读的失败原因(string input, string fragment)
    {
        Assert.False(FinsAddressParser.TryParse(input, out _, out var reason));
        Assert.Contains(fragment, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 位访问与字访问用不同的存储区代码()
    {
        // FINS 的位访问是另一套代码，混用会让 CPU 返回参数错误
        Assert.Equal(0x82, FinsArea.Dm.WordCode());
        Assert.Equal(0x02, FinsArea.Dm.BitCode());
        Assert.Equal(0xB0, FinsArea.Cio.WordCode());
        Assert.Equal(0x30, FinsArea.Cio.BitCode());
    }

    [Fact]
    public void 辅助区按只读处理()
    {
        Assert.False(FinsArea.Auxiliary.IsWritable());
        Assert.True(FinsArea.Dm.IsWritable());
    }
}

/// <summary>端到端测试：真实 UDP、真实 FINS 报文，对端是独立实现的模拟器。</summary>
public class FinsDriverTests
{
    private static DeviceOptions Options(int port) => new()
    {
        DeviceId = "omron",
        Protocol = "omron-fins",
        Host = "127.0.0.1",
        Port = port,
        TimeoutMs = 2000,
    };

    private static TagDef Tag(
        string name, string address, TagDataType type,
        ByteOrder order = ByteOrder.ABCD, double scale = 1.0, TagAccess access = TagAccess.Read)
        => new()
        {
            Name = name, Address = address, DataType = type,
            ByteOrder = order, Scale = scale, Access = access,
        };

    private static FinsSimulatorServer Simulator()
        => new(new SimulatedFinsDeviceConfig { Name = "test", Port = 0 });

    private static async Task<TagValue[]> ReadAsync(FinsDriver driver, params TagDef[] tags)
    {
        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        return values;
    }

    [Fact]
    public async Task 读DM区()
    {
        await using var server = Simulator();
        server.PokeWords("D100", 1234);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "D100", TagDataType.Int16));

        Assert.Equal(1234, values[0].AsInt64());
    }

    [Fact]
    public async Task 三十二位值是低字在前字内大端()
    {
        // 欧姆龙是 CDAB，三菱是 DCBA——两家都低字在前，但字内字节序相反。
        // 一个仓库里同时放这两种驱动，这类差异最容易带错
        await using var server = Simulator();
        server.PokeWords("D200", 0x5678, 0x1234);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "D200", TagDataType.Int32, ByteOrder.CDAB));

        Assert.Equal(0x12345678, values[0].AsInt64());
    }

    [Fact]
    public async Task 浮点数跨两个字()
    {
        await using var server = Simulator();
        server.PokeWords("D300", 0x0000, 0x422A);  // 42.5f

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "D300", TagDataType.Float32, ByteOrder.CDAB));

        Assert.Equal(42.5, values[0].AsDouble());
    }

    [Fact]
    public async Task 一个字里的多个位只读一次()
    {
        // 欧姆龙的位就是"某个字的某一位"，按字读回来再取位比逐位读高效得多
        await using var server = Simulator();
        server.PokeBit("D400.00", true);
        server.PokeBit("D400.05", true);
        server.PokeBit("D400.15", true);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("b0", "D400.00", TagDataType.Bool),
            Tag("b1", "D400.01", TagDataType.Bool),
            Tag("b5", "D400.05", TagDataType.Bool),
            Tag("b15", "D400.15", TagDataType.Bool),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];
        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.RequestCount);
        Assert.True(values[0].AsBool());
        Assert.False(values[1].AsBool());
        Assert.True(values[2].AsBool());
        Assert.True(values[3].AsBool());
    }

    [Fact]
    public async Task 读CIO与W区()
    {
        await using var server = Simulator();
        server.PokeWords("CIO50", 777);
        server.PokeBit("W10.03", true);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("cio", "CIO50", TagDataType.Int16),
            Tag("w", "W10.03", TagDataType.Bool));

        Assert.Equal(777, values[0].AsInt64());
        Assert.True(values[1].AsBool());
    }

    [Fact]
    public async Task 连续地址合并成一次请求()
    {
        await using var server = Simulator();
        server.PokeWords("D500", 10, 20, 30);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("a", "D500", TagDataType.Int16),
            Tag("b", "D501", TagDataType.Int16),
            Tag("c", "D502", TagDataType.Int16),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[3];
        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal([10, 20, 30], values.Select(static v => v.AsInt64()));
    }

    [Fact]
    public async Task 写DM区()
    {
        await using var server = Simulator();

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("sp", "D600", TagDataType.Int16, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 4321, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal([(ushort)4321], server.PeekWords("D600", 1));
    }

    [Fact]
    public async Task 写单个位()
    {
        await using var server = Simulator();

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("b", "D700.07", TagDataType.Bool, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken);

        Assert.True(server.PeekBit("D700.07"));
    }

    [Fact]
    public async Task 写辅助区被拒绝()
    {
        await using var server = Simulator();
        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("a", "A50", TagDataType.Int16, access: TagAccess.ReadWrite);

        var ex = await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));

        Assert.Contains("只读", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 丢包时超时而不是读到脏数据()
    {
        // UDP 不保证送达。丢了就该超时，绝不能把上一轮的数据当成本轮结果
        await using var server = Simulator();
        server.PokeWords("D800", 42);

        await using var driver = new FinsDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tags = new[] { Tag("a", "D800", TagDataType.Int16) };
        var plan = driver.CreateReadPlan(tags);

        // 先正常读一次，让链路上确实有过一个响应
        await driver.ExecuteAsync(plan, new TagValue[1], TestContext.Current.CancellationToken);

        server.DropNextRequests = 1;

        await Assert.ThrowsAnyAsync<Exception>(async () => await driver.ExecuteAsync(
            plan, new TagValue[1], TestContext.Current.CancellationToken));

        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public void 服务号不匹配时拒绝该响应()
    {
        // UDP 不保证顺序，上一次超时的响应可能迟到。不核对服务号
        // 就会把它当成本次结果，读出一个属于上一轮的旧值
        var frame = new byte[14];
        frame[0] = 0xC0;
        frame[9] = 7;   // 响应里的服务号

        var ex = Assert.Throws<ProtocolException>(
            () => FinsFrame.ReadResponseData(frame, expectedServiceId: 8));

        Assert.Contains("服务号不匹配", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 驱动工厂声明协议名与地址语法()
    {
        var factory = new FinsDriverFactory();

        Assert.Equal("omron-fins", factory.Protocol);
        Assert.Contains("D100.05", factory.AddressSyntaxHint, StringComparison.Ordinal);
        Assert.Contains("CDAB", factory.AddressSyntaxHint, StringComparison.Ordinal);
    }
}
