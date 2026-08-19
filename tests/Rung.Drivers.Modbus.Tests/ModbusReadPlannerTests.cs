using Rung.Abstractions;
using Xunit;

namespace Rung.Drivers.Modbus.Tests;

public class ModbusReadPlannerTests
{
    private static TagDef Tag(string name, string address, TagDataType type, int length = 0)
        => new() { Name = name, Address = address, DataType = type, Length = length };

    [Fact]
    public void 相邻寄存器合并成一次请求()
    {
        TagDef[] tags =
        [
            Tag("a", "HR0", TagDataType.Int16),
            Tag("b", "HR1", TagDataType.Int16),
            Tag("c", "HR2", TagDataType.Float32), // 占两个寄存器
        ];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Empty(plan.Issues);
        Assert.Equal(1, plan.RequestCount);

        var request = plan.Requests[0];
        Assert.Equal(ModbusArea.HoldingRegister, request.Area);
        Assert.Equal(0, request.Start);
        Assert.Equal(4, request.Count);

        // 寄存器区里记的是字节偏移
        Assert.Equal(0, plan.Locations[0].Offset);
        Assert.Equal(2, plan.Locations[1].Offset);
        Assert.Equal(4, plan.Locations[2].Offset);
    }

    [Fact]
    public void 不同数据区不合并()
    {
        TagDef[] tags = [Tag("a", "HR0", TagDataType.Int16), Tag("b", "IR0", TagDataType.Int16)];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(2, plan.RequestCount);
    }

    [Fact]
    public void 不同从站不合并()
    {
        // 一条 TCP 连接后面挂多个 RTU 从站，各自是独立的地址空间
        TagDef[] tags = [Tag("a", "1:HR0", TagDataType.Int16), Tag("b", "2:HR0", TagDataType.Int16)];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(2, plan.RequestCount);
        Assert.Equal(1, plan.Requests[0].UnitId);
        Assert.Equal(2, plan.Requests[1].UnitId);
    }

    [Theory]
    [InlineData(16, 1)] // 空洞 16 个寄存器，正好等于默认阈值：合并
    [InlineData(4, 2)]  // 阈值调小：不合并
    public void 空洞大小决定是否合并(int maxGap, int expectedRequests)
    {
        // HR0 占 [0,1)，HR17 占 [17,18)，中间空洞 16 个寄存器
        TagDef[] tags = [Tag("a", "HR0", TagDataType.Int16), Tag("b", "HR17", TagDataType.Int16)];

        var plan = ModbusReadPlanner.Create(tags, new ModbusReadPlannerOptions { MaxGapRegisters = maxGap });

        Assert.Equal(expectedRequests, plan.RequestCount);
    }

    [Fact]
    public void 超过一百二十五个寄存器时切分()
    {
        // Modbus 单次读寄存器上限就是 125，超了会被从站以异常码拒绝
        var tags = Enumerable.Range(0, 200)
            .Select(i => Tag($"t{i}", $"HR{i}", TagDataType.Int16))
            .ToArray();

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(2, plan.RequestCount);
        Assert.All(plan.Requests, static r => Assert.True(r.Count <= ModbusLimits.MaxReadRegisters));
        Assert.Equal(125, plan.Requests[0].Count);
        Assert.Equal(75, plan.Requests[1].Count);
    }

    [Fact]
    public void 位区的上限是两千()
    {
        var tags = Enumerable.Range(0, 2500)
            .Select(i => Tag($"c{i}", $"CO{i}", TagDataType.Bool))
            .ToArray();

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(2, plan.RequestCount);
        Assert.Equal(2000, plan.Requests[0].Count);
        Assert.All(plan.Requests, static r => Assert.True(r.Count <= ModbusLimits.MaxReadBits));
    }

    [Fact]
    public void 位区里记的是位序号()
    {
        TagDef[] tags = [Tag("a", "CO10", TagDataType.Bool), Tag("b", "CO13", TagDataType.Bool)];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(10, plan.Requests[0].Start);
        Assert.Equal(0, plan.Locations[0].Offset);
        Assert.Equal(3, plan.Locations[1].Offset);
    }

    [Fact]
    public void 位区配成非布尔类型会被拦下()
    {
        // 线圈只有 0/1，从里面读整数没有意义，多半是地址区搞错了
        TagDef[] tags = [Tag("bad", "CO0", TagDataType.Int16)];

        var plan = ModbusReadPlanner.Create(tags);

        var issue = Assert.Single(plan.Issues);
        Assert.Contains("只能配 Bool", issue.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Requests);
    }

    [Fact]
    public void 地址写错的点位被隔离而不影响其余点位()
    {
        TagDef[] tags =
        [
            Tag("good", "HR0", TagDataType.Int16),
            Tag("broken", "垃圾", TagDataType.Int16),
            Tag("good2", "HR1", TagDataType.Int16),
        ];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Single(plan.Issues);
        Assert.Equal(1, plan.RequestCount);
        Assert.Equal(2, plan.ActiveTagCount);
        Assert.False(plan.Locations[1].IsValid);
    }

    [Fact]
    public void 单点位超过读取上限时给出可照做的提示()
    {
        TagDef[] tags = [Tag("huge", "HR0", TagDataType.Bytes, length: 400)];

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Contains("拆分成多个点位", Assert.Single(plan.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 输入顺序不影响编译结果()
    {
        TagDef[] ordered = [Tag("a", "HR0", TagDataType.Int16), Tag("b", "HR1", TagDataType.Int16)];
        TagDef[] shuffled = [ordered[1], ordered[0]];

        var planA = ModbusReadPlanner.Create(ordered);
        var planB = ModbusReadPlanner.Create(shuffled);

        Assert.Equal(planA.RequestCount, planB.RequestCount);
        Assert.Equal(planA.Locations[0], planB.Locations[1]);
    }

    [Fact]
    public void 一百个连续点位只需一次请求()
    {
        // Modbus 的合并收益比 S7 还大：每次请求都是一个完整的 TCP 往返
        var tags = Enumerable.Range(0, 100)
            .Select(i => Tag($"t{i}", $"HR{i}", TagDataType.Int16))
            .ToArray();

        var plan = ModbusReadPlanner.Create(tags);

        Assert.Equal(1, plan.RequestCount);
        Assert.Equal(100, plan.Requests[0].Count);
    }
}
