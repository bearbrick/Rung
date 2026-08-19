using Rung.Abstractions;
using Xunit;

namespace Rung.Drivers.Modbus.Tests;

/// <summary>端到端测试：真实 TCP、真实 Modbus 报文，对端是 FluentModbus 的服务端实现。</summary>
public class ModbusDriverTests
{
    private static DeviceOptions Options(int port, int unitId = 1) => new()
    {
        DeviceId = "plc",
        Protocol = "modbus-tcp",
        Host = "127.0.0.1",
        Port = port,
        TimeoutMs = 3000,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unitId"] = unitId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    };

    private static TagDef Tag(
        string name, string address, TagDataType type,
        ByteOrder order = ByteOrder.ABCD, double scale = 1.0, TagAccess access = TagAccess.Read)
        => new()
        {
            Name = name, Address = address, DataType = type,
            ByteOrder = order, Scale = scale, Access = access,
        };

    private static async Task<TagValue[]> ReadAsync(ModbusDriver driver, params TagDef[] tags)
    {
        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        return values;
    }

    [Fact]
    public async Task 连接后可以读保持寄存器()
    {
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x04, 0xD2); // 1234

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DriverState.Connected, driver.State);

        var values = await ReadAsync(driver, Tag("a", "HR0", TagDataType.Int16));

        Assert.Equal(TagQuality.Good, values[0].Quality);
        Assert.Equal(1234, values[0].AsInt64());
    }

    [Theory]
    [InlineData(ByteOrder.ABCD, new byte[] { 0x12, 0x34, 0x56, 0x78 })]
    [InlineData(ByteOrder.CDAB, new byte[] { 0x56, 0x78, 0x12, 0x34 })]
    [InlineData(ByteOrder.BADC, new byte[] { 0x34, 0x12, 0x78, 0x56 })]
    [InlineData(ByteOrder.DCBA, new byte[] { 0x78, 0x56, 0x34, 0x12 })]
    public async Task 四种字节序都能还原成同一个数(ByteOrder order, byte[] wire)
    {
        // Modbus 的字节序问题比 S7 严重得多：CDAB 在现场极其常见，
        // 不同厂家的 32 位数排列几乎没有共识
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, wire);

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "HR0", TagDataType.Int32, order));

        Assert.Equal(0x12345678, values[0].AsInt64());
    }

    [Fact]
    public async Task 浮点数跨两个寄存器读取()
    {
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x42, 0x2A, 0x00, 0x00); // 42.5f

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "HR0", TagDataType.Float32));

        Assert.Equal(42.5, values[0].AsDouble());
    }

    [Fact]
    public async Task 线圈与离散输入()
    {
        using var server = new ModbusTestServer();
        server.SetCoil(1, 5, true);
        server.SetDiscreteInput(1, 7, true);
        server.SetDiscreteInput(1, 8, false);

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("coil", "CO5", TagDataType.Bool),
            Tag("di_on", "DI7", TagDataType.Bool),
            Tag("di_off", "DI8", TagDataType.Bool));

        Assert.True(values[0].AsBool());
        Assert.True(values[1].AsBool());
        Assert.False(values[2].AsBool());
    }

    [Fact]
    public async Task 输入寄存器()
    {
        using var server = new ModbusTestServer();
        server.SetInputRegisterBytes(1, 3, 0x00, 0x63); // 99

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "IR3", TagDataType.Int16));

        Assert.Equal(99, values[0].AsInt64());
    }

    [Fact]
    public async Task 寄存器内的单个位()
    {
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x00, 0b0000_1010); // 位 1 和 3 为真

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("bit0", "HR0.0", TagDataType.Bool),
            Tag("bit1", "HR0.1", TagDataType.Bool),
            Tag("bit3", "HR0.3", TagDataType.Bool));

        Assert.False(values[0].AsBool());
        Assert.True(values[1].AsBool());
        Assert.True(values[2].AsBool());
    }

    [Fact]
    public async Task 不指定位时整寄存器非零为真()
    {
        // 很多设备用一整个寄存器表示一个状态位，这是现场常见约定
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x00, 0x01);
        server.SetHoldingRegisterBytes(1, 1, 0x00, 0x00);

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("on", "HR0", TagDataType.Bool),
            Tag("off", "HR1", TagDataType.Bool));

        Assert.True(values[0].AsBool());
        Assert.False(values[1].AsBool());
    }

    [Fact]
    public async Task 线性换算在采集链路上生效()
    {
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x09, 0x2E); // 2350

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("temp", "HR0", TagDataType.Int16, scale: 0.1));

        Assert.Equal(235.0, values[0].AsDouble(), precision: 10);
    }

    [Fact]
    public async Task 多个从站各读各的()
    {
        using var server = new ModbusTestServer(1, 2, 3);
        server.SetHoldingRegisterBytes(1, 0, 0x00, 0x0A);
        server.SetHoldingRegisterBytes(2, 0, 0x00, 0x14);
        server.SetHoldingRegisterBytes(3, 0, 0x00, 0x1E);

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("u1", "1:HR0", TagDataType.Int16),
            Tag("u2", "2:HR0", TagDataType.Int16),
            Tag("u3", "3:HR0", TagDataType.Int16));

        Assert.Equal([10, 20, 30], values.Select(static v => v.AsInt64()));
    }

    [Fact]
    public async Task 超出上限的批次拆分后值仍落回正确位置()
    {
        using var server = new ModbusTestServer();
        server.SetHoldingRegisterBytes(1, 0, 0x00, 0x01);
        server.SetHoldingRegisterBytes(1, 130, 0x00, 0x82);   // 130
        server.SetHoldingRegisterBytes(1, 199, 0x00, 0xC7);   // 199

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tags = Enumerable.Range(0, 200)
            .Select(i => Tag($"t{i}", $"HR{i}", TagDataType.Int16))
            .ToArray();

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];
        var good = await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.True(plan.RequestCount > 1, "应当被拆成多次请求");
        Assert.Equal(200, good);
        Assert.Equal(1, values[0].AsInt64());
        Assert.Equal(130, values[130].AsInt64());
        Assert.Equal(199, values[199].AsInt64());
    }

    [Fact]
    public async Task 写保持寄存器()
    {
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("sp", "HR10", TagDataType.Int16, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1234, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal([0x04, 0xD2], server.GetHoldingRegisterBytes(1, 10, 1));
    }

    [Fact]
    public async Task 写浮点数按配置的字节序落盘()
    {
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("f", "HR20", TagDataType.Float32, ByteOrder.CDAB, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromSingle(42.5f, DateTime.UtcNow), TestContext.Current.CancellationToken);

        // 42.5f 大端是 42 2A 00 00，CDAB 换字之后是 00 00 42 2A
        Assert.Equal([0x00, 0x00, 0x42, 0x2A], server.GetHoldingRegisterBytes(1, 20, 2));
    }

    [Fact]
    public async Task 写线圈()
    {
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("c", "CO3", TagDataType.Bool, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken);

        Assert.True(server.GetCoil(1, 3));
    }

    [Fact]
    public async Task 写只读区被协议层面拒绝()
    {
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("ir", "IR0", TagDataType.Int16, access: TagAccess.ReadWrite);

        var ex = await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));

        Assert.Contains("只读", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写寄存器内的单个位被明确拒绝()
    {
        // Modbus 没有对应的写功能码，只能读改写；而读改写在并发下会丢掉
        // 别人刚写进去的位，产线上这种丢失极难排查。宁可拒绝也不要悄悄做
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("bit", "HR0.3", TagDataType.Bool, access: TagAccess.ReadWrite);

        var ex = await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken));

        Assert.Contains("读改写", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 只读点位拒绝写入()
    {
        using var server = new ModbusTestServer();

        await using var driver = new ModbusDriver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            Tag("ro", "HR0", TagDataType.Int16),
            TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 连不上时状态转为故障()
    {
        await using var driver = new ModbusDriver(Options(1));

        await Assert.ThrowsAnyAsync<Exception>(
            async () => await driver.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public async Task 链路中断后状态转为故障交给上层重连()
    {
        // 通过一个自己能掐断的转发代理来断链，而不是指望服务端停机时
        // 立刻关掉已建立的连接——后者不保证，测试会时灵时不灵
        using var server = new ModbusTestServer();
        using var proxy = new TcpLinkProxy(server.Port);

        await using var driver = new ModbusDriver(Options(proxy.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tags = new[] { Tag("a", "HR0", TagDataType.Int16) };
        var plan = driver.CreateReadPlan(tags);

        await driver.ExecuteAsync(plan, new TagValue[1], TestContext.Current.CancellationToken);
        Assert.Equal(DriverState.Connected, driver.State);

        proxy.Cut();

        await Assert.ThrowsAnyAsync<Exception>(async () => await driver.ExecuteAsync(
            plan, new TagValue[1], TestContext.Current.CancellationToken));

        // 驱动自己不重连：退避策略是上层的事
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public void 驱动工厂声明协议名与地址语法()
    {
        var factory = new ModbusDriverFactory();

        Assert.Equal("modbus-tcp", factory.Protocol);
        Assert.Contains("HR100", factory.AddressSyntaxHint, StringComparison.Ordinal);
        Assert.Contains("40001", factory.AddressSyntaxHint, StringComparison.Ordinal);
    }
}
