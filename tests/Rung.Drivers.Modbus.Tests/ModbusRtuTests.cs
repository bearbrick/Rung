using System.Globalization;
using FluentModbus;
using Rung.Abstractions;
using Rung.Simulator;
using Xunit;

namespace Rung.Drivers.Modbus.Tests;

/// <summary>
/// Modbus RTU 端到端测试。
/// <para>
/// 串口设备进不了 CI，虚拟串口工具也不是每台机器都有。这里用一对内存串口
/// 把 FluentModbus 的客户端和服务端对接起来——<b>走的是真实的 RTU 帧，含 CRC</b>，
/// 只有物理层换成了内存。
/// </para>
/// <para>
/// 能测到的：帧格式、CRC、从站寻址、多从站共线、只读区拒绝。
/// <b>测不到的</b>：波特率、校验位、RS-485 收发切换时序、线缆干扰——
/// 那些只有真串口能验，README 里如实写明了。
/// </para>
/// </summary>
public sealed class ModbusRtuTests : IDisposable
{
    private readonly InMemorySerialPortPair _wire = new("rtu-test");
    private readonly ModbusRtuServer _server;
    private bool _disposed;

    public ModbusRtuTests()
    {
        // 一条总线上挂三个从站，正是 RS-485 的典型拓扑
        _server = new ModbusRtuServer([1, 3, 7], isAsynchronous: true);
        _server.Start(_wire.B);
    }

    private static DeviceOptions Options(int unitId = 1) => new()
    {
        DeviceId = "rtu-bus",
        Protocol = "modbus-rtu",
        Host = "mem",            // RTU 下 Host 放的是串口名
        TimeoutMs = 3000,
        Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transport"] = "rtu",
            ["unitId"] = unitId.ToString(CultureInfo.InvariantCulture),
        },
    };

    private ModbusDriver CreateDriver(int unitId = 1)
        => new(Options(unitId), () => _wire.A);

    private static TagDef Tag(
        string name, string address, TagDataType type,
        ByteOrder order = ByteOrder.ABCD, TagAccess access = TagAccess.Read)
        => new() { Name = name, Address = address, DataType = type, ByteOrder = order, Access = access };

    private void SetRegister(byte unitId, int offset, params byte[] wireBytes)
    {
        lock (_server.Lock)
        {
            wireBytes.CopyTo(_server.GetHoldingRegisterBuffer(unitId)[(offset * 2)..]);
        }
    }

    private static async Task<TagValue[]> ReadAsync(ModbusDriver driver, params TagDef[] tags)
    {
        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[tags.Length];

        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        return values;
    }

    [Fact]
    public async Task 通过串口读保持寄存器()
    {
        SetRegister(1, 0, 0x04, 0xD2); // 1234

        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DriverState.Connected, driver.State);

        var values = await ReadAsync(driver, Tag("a", "HR0", TagDataType.Int16));

        Assert.Equal(TagQuality.Good, values[0].Quality);
        Assert.Equal(1234, values[0].AsInt64());
    }

    [Fact]
    public async Task 一条总线上的多个从站各读各的()
    {
        // RS-485 的典型拓扑：一条线挂多个从站，靠地址前缀区分。
        // 这也是 RTU 与 TCP 最大的配置差异——总线配成一台 Rung 设备，
        // 而不是每个从站一台，否则会有多个工作者去抢同一个串口
        SetRegister(1, 0, 0x00, 0x0A);
        SetRegister(3, 0, 0x00, 0x14);
        SetRegister(7, 0, 0x00, 0x1E);

        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver,
            Tag("u1", "1:HR0", TagDataType.Int16),
            Tag("u3", "3:HR0", TagDataType.Int16),
            Tag("u7", "7:HR0", TagDataType.Int16));

        Assert.Equal([10, 20, 30], values.Select(static v => v.AsInt64()));
    }

    [Theory]
    [InlineData(ByteOrder.ABCD, new byte[] { 0x12, 0x34, 0x56, 0x78 })]
    [InlineData(ByteOrder.CDAB, new byte[] { 0x56, 0x78, 0x12, 0x34 })]
    public async Task 字节序在串口上同样生效(ByteOrder order, byte[] wire)
    {
        SetRegister(1, 10, wire);

        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var values = await ReadAsync(driver, Tag("a", "HR10", TagDataType.Int32, order));

        Assert.Equal(0x12345678, values[0].AsInt64());
    }

    [Fact]
    public async Task 连续寄存器合并成一次请求()
    {
        SetRegister(1, 20, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03);

        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        TagDef[] tags =
        [
            Tag("a", "HR20", TagDataType.Int16),
            Tag("b", "HR21", TagDataType.Int16),
            Tag("c", "HR22", TagDataType.Int16),
        ];

        var plan = driver.CreateReadPlan(tags);
        var values = new TagValue[3];
        await driver.ExecuteAsync(plan, values, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal([1, 2, 3], values.Select(static v => v.AsInt64()));
    }

    [Fact]
    public async Task 通过串口写保持寄存器()
    {
        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("sp", "HR30", TagDataType.Int16, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1234, DateTime.UtcNow),
            TestContext.Current.CancellationToken);

        lock (_server.Lock)
        {
            Assert.Equal(
                [(byte)0x04, (byte)0xD2],
                _server.GetHoldingRegisterBuffer(1).Slice(30 * 2, 2).ToArray());
        }
    }

    [Fact]
    public async Task 写线圈()
    {
        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("c", "CO5", TagDataType.Bool, access: TagAccess.ReadWrite);
        await driver.WriteAsync(
            tag, TagValue.FromBool(true, DateTime.UtcNow), TestContext.Current.CancellationToken);

        lock (_server.Lock)
        {
            Assert.NotEqual(0, _server.GetCoilBuffer(1)[0] & (1 << 5));
        }
    }

    [Fact]
    public async Task 写只读区被协议层面拒绝()
    {
        await using var driver = CreateDriver();
        await driver.ConnectAsync(TestContext.Current.CancellationToken);

        var tag = Tag("ir", "IR0", TagDataType.Int16, access: TagAccess.ReadWrite);

        var ex = await Assert.ThrowsAsync<RungException>(async () => await driver.WriteAsync(
            tag, TagValue.FromInteger(TagDataType.Int16, 1, DateTime.UtcNow),
            TestContext.Current.CancellationToken));

        Assert.Contains("只读", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RTU工厂声明串口相关的配置方式()
    {
        var factory = new ModbusRtuDriverFactory();

        Assert.Equal("modbus-rtu", factory.Protocol);
        Assert.Contains("/dev/ttyUSB0", factory.AddressSyntaxHint, StringComparison.Ordinal);
        Assert.Contains("一条总线配成一台设备", factory.AddressSyntaxHint, StringComparison.Ordinal);
    }

    [Fact]
    public void RTU工厂产出的驱动一定走串口()
    {
        // 配置里就算写着 transport=tcp，这个工厂也必须产出 RTU
        var options = Options() with
        {
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["transport"] = "tcp",
            },
        };

        var driver = new ModbusRtuDriverFactory().Create(options);

        Assert.IsType<ModbusDriver>(driver);
        Assert.Equal(DriverState.Disconnected, driver.State);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _server.Stop();
        _server.Dispose();
        _wire.Dispose();
    }
}
