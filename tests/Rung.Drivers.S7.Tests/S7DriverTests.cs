using Rung.Abstractions;
using Rung.Simulator;
using Xunit;

namespace Rung.Drivers.S7.Tests;

/// <summary>
/// 端到端测试：真实 TCP、真实报文、真实半包处理，对端是 Rung.Simulator。
/// 模拟器的报文编码是独立实现的，与被测代码不同源，因此两边互为对照。
/// 这些用例覆盖的是"编译通过"和"真能采到数"之间的那段距离。
/// </summary>
public class S7DriverTests
{
    private static DeviceOptions Options(int port, byte rack = 0, byte slot = 1) => new()
    {
        DeviceId = "test-plc",
        Protocol = "s7",
        Host = "127.0.0.1",
        Port = port,
        TimeoutMs = 5000,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rack"] = rack.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
        },
    };

    private static TagDef Tag(string name, string address, TagDataType type, double scale = 1.0)
        => new() { Name = name, Address = address, DataType = type, Scale = scale };

    /// <summary>起一台空的模拟设备，端口交给系统分配。</summary>
    private static S7SimulatorServer Simulator(ushort pduLength = 240, FaultInjection? faults = null)
        => new(new SimulatedDeviceConfig { Name = "test", Port = 0, NegotiatedPduLength = pduLength, Faults = faults });

    [Fact]
    public async Task 握手成功后拿到协商的PDU长度()
    {
        await using var server = Simulator(pduLength: 480);
        await using var driver = new S7Driver(Options(server.Port));

        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DriverState.Connected, driver.State);
        Assert.Equal(480, driver.MaxPduLength);
    }

    [Fact]
    public async Task 对端拒绝连接时给出指向机架槽号的提示()
    {
        await using var server = Simulator(faults: new FaultInjection { RejectConnections = true });
        await using var driver = new S7Driver(Options(server.Port));

        var ex = await Assert.ThrowsAsync<ProtocolException>(
            async () => await driver.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Contains("机架号/槽号", ex.Message, StringComparison.Ordinal);
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public async Task 采集一批点位并正确解出各自的值()
    {
        await using var server = Simulator();

        // DB1: 偏移 0 是 REAL 42.5，偏移 4 是 INT 1234，偏移 6 的第 3 位为 1
        server.Poke("DB1.DBD0", 0x42, 0x2A, 0x00, 0x00);
        server.Poke("DB1.DBW4", 0x04, 0xD2);
        server.Poke("DB1.DBB6", 0b0000_1000);

        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("temp", "DB1.DBD0", TagDataType.Float32),
            Tag("count", "DB1.DBW4", TagDataType.Int16),
            Tag("running", "DB1.DBX6.3", TagDataType.Bool),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        var good = await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(3, good);
        Assert.Equal(42.5f, values[0].AsDouble());
        Assert.Equal(1234, values[1].AsInt64());
        Assert.True(values[2].AsBool());
        Assert.All(values, static v => Assert.Equal(TagQuality.Good, v.Quality));
    }

    [Fact]
    public async Task 连续点位合并成一次往返()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var exchangesAfterHandshake = server.ExchangeCount;

        var tags = Enumerable.Range(0, 50)
            .Select(i => Tag($"t{i}", $"DB1.DBW{i * 2}", TagDataType.Int16))
            .ToArray();

        var plan = driver.CreateReadPlan(tags);
        await driver.ExecuteAsync(plan, new TagValue[tags.Length], TestContext.Current.CancellationToken);

        // 50 个点位，逐个读要 50 次往返；合并后只用了 1 次
        Assert.Equal(1, plan.RequestCount);
        Assert.Equal(exchangesAfterHandshake + 1, server.ExchangeCount);
    }

    [Fact]
    public async Task 超出单个PDU的批次自动拆成多次往返()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        server.Poke("DB1.DBW300", 0x11, 0x22);

        // 跨度 400 字节，超过 PDU 240 下 222 字节的单次上限
        var tags = Enumerable.Range(0, 200)
            .Select(i => Tag($"t{i}", $"DB1.DBW{i * 2}", TagDataType.Int16))
            .ToArray();

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        var good = await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.True(plan.RequestCount > 1, "应当被拆成多次请求");
        Assert.Equal(200, good);

        // 拆分之后每个点位仍然落回自己的位置：偏移 300 处的值属于第 150 个点位
        Assert.Equal(0x1122, values[150].AsInt64());
    }

    [Fact]
    public async Task 单个点位读失败不影响同批次其余点位()
    {
        await using var server = Simulator(faults: new FaultInjection { FailingDbNumbers = { 9 } });
        server.Poke("DB1.DBW0", 0x00, 0x2A);

        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("ok", "DB1.DBW0", TagDataType.Int16),
            Tag("missing", "DB9.DBW0", TagDataType.Int16),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        var good = await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(1, good);
        Assert.Equal(TagQuality.Good, values[0].Quality);
        Assert.Equal(42, values[0].AsInt64());
        Assert.Equal(TagQuality.DeviceError, values[1].Quality);
    }

    [Fact]
    public async Task 地址配错的点位每轮都被标记为配置错误()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("ok", "DB1.DBW0", TagDataType.Int16),
            Tag("typo", "DB1.DBW", TagDataType.Int16),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Single(plan.Issues);
        Assert.Equal(TagQuality.ConfigError, values[1].Quality);
        Assert.Equal(TagQuality.Good, values[0].Quality);
    }

    [Fact]
    public async Task 线性换算在采集链路上生效()
    {
        await using var server = Simulator();
        server.Poke("DB1.DBW0", 0x09, 0x2E); // 2350

        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags = [Tag("temp", "DB1.DBW0", TagDataType.Int16, scale: 0.1)];
        var values = new TagValue[1];

        await driver.ExecuteAsync(driver.CreateReadPlan(tags), values, TestContext.Current.CancellationToken);

        Assert.Equal(235.0, values[0].AsDouble(), precision: 10);
    }

    [Fact]
    public async Task 写命令真的落到设备上()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = new TagDef
        {
            Name = "setpoint",
            Address = "DB1.DBW10",
            DataType = TagDataType.Int16,
            Access = TagAccess.ReadWrite,
        };

        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1234, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal([0x04, 0xD2], server.Peek("DB1.DBW10", 2));
    }

    [Fact]
    public async Task 写入布尔点位只改动目标位()
    {
        await using var server = Simulator();
        server.Poke("DB1.DBB20", 0b1010_0101);

        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = new TagDef
        {
            Name = "flag",
            Address = "DB1.DBX20.1",
            DataType = TagDataType.Bool,
            Access = TagAccess.ReadWrite,
        };

        await driver.WriteAsync(tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken);

        // 只有第 1 位被置起，其余位原样保留
        Assert.Equal([0b1010_0111], server.Peek("DB1.DBB20", 1));
    }

    [Fact]
    public async Task 只读点位拒绝写入()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("ro", "DB1.DBW0", TagDataType.Int16);

        await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未连接时操作被明确拒绝()
    {
        await using var driver = new S7Driver(Options(1));

        Assert.Throws<RungException>(() => driver.CreateReadPlan([]));
    }

    [Fact]
    public async Task 连接断开后状态转为故障交给上层重连()
    {
        await using var server = Simulator();
        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags = [Tag("t", "DB1.DBW0", TagDataType.Int16)];
        var plan = driver.CreateReadPlan(tags);

        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await driver.ExecuteAsync(
            plan, new TagValue[1], TestContext.Current.CancellationToken));

        // 驱动自己不重连——退避策略是上层的事，闷头重试会把 PLC 的连接资源占满
        Assert.Equal(DriverState.Faulted, driver.State);
    }

    [Fact]
    public async Task 重复采集复用同一份计划且结果稳定()
    {
        await using var server = Simulator();
        server.Poke("DB1.DBW0", 0x00, 0x0A);

        await using var driver = new S7Driver(Options(server.Port));
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags = [Tag("t", "DB1.DBW0", TagDataType.Int16)];
        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[1];

        for (var round = 0; round < 5; round++)
        {
            await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);
            Assert.Equal(10, values[0].AsInt64());
        }

        // 值变了就该跟着变，不能读到缓存
        server.Poke("DB1.DBW0", 0x00, 0x14);
        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(20, values[0].AsInt64());
    }

    [Fact]
    public void 驱动工厂声明协议名与地址语法()
    {
        var factory = new S7DriverFactory();

        Assert.Equal("s7", factory.Protocol);
        Assert.Contains("DB1.DBW10", factory.AddressSyntaxHint, StringComparison.Ordinal);
    }
}
