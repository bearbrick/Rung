using Rung.Abstractions;
using Xunit;

namespace Rung.Drivers.Modbus.Tests;

public class ModbusAddressParserTests
{
    [Theory]
    // 显式前缀，0 基
    [InlineData("HR0", ModbusArea.HoldingRegister, 0)]
    [InlineData("HR100", ModbusArea.HoldingRegister, 100)]
    [InlineData("IR10", ModbusArea.InputRegister, 10)]
    [InlineData("CO5", ModbusArea.Coil, 5)]
    [InlineData("DI7", ModbusArea.DiscreteInput, 7)]
    // 经典编号，1 基
    [InlineData("40001", ModbusArea.HoldingRegister, 0)]
    [InlineData("40101", ModbusArea.HoldingRegister, 100)]
    [InlineData("30001", ModbusArea.InputRegister, 0)]
    [InlineData("10001", ModbusArea.DiscreteInput, 0)]
    [InlineData("00001", ModbusArea.Coil, 0)]
    [InlineData("400001", ModbusArea.HoldingRegister, 0)]
    // 4x 写法
    [InlineData("4x0001", ModbusArea.HoldingRegister, 0)]
    [InlineData("3x0011", ModbusArea.InputRegister, 10)]
    // 大小写与空白不敏感
    [InlineData("hr100", ModbusArea.HoldingRegister, 100)]
    [InlineData("  HR100  ", ModbusArea.HoldingRegister, 100)]
    public void 解析常见地址写法(string input, ModbusArea area, int offset)
    {
        var address = ModbusAddressParser.Parse(input);

        Assert.Equal(area, address.Area);
        Assert.Equal(offset, address.Offset);
    }

    [Fact]
    public void 零基与一基的区别不会被混淆()
    {
        // 这是 Modbus 接入时最高频的错误，两种写法在语义上刻意区分得很开
        Assert.Equal(0, ModbusAddressParser.Parse("HR0").Offset);
        Assert.Equal(0, ModbusAddressParser.Parse("40001").Offset);
        Assert.Equal(1, ModbusAddressParser.Parse("HR1").Offset);
        Assert.Equal(1, ModbusAddressParser.Parse("40002").Offset);
    }

    [Fact]
    public void 默认从站号来自设备配置()
    {
        Assert.Equal(1, ModbusAddressParser.Parse("HR0").UnitId);
        Assert.Equal(9, ModbusAddressParser.Parse("HR0", defaultUnitId: 9).UnitId);
    }

    [Fact]
    public void 地址可以覆盖从站号()
    {
        // 一条 TCP 连接后面挂多个 RTU 从站是常见拓扑
        var address = ModbusAddressParser.Parse("3:HR100", defaultUnitId: 1);

        Assert.Equal(3, address.UnitId);
        Assert.Equal(100, address.Offset);
    }

    [Fact]
    public void 寄存器内的位可以单独寻址()
    {
        var address = ModbusAddressParser.Parse("HR100.3");

        Assert.Equal(100, address.Offset);
        Assert.Equal(3, address.BitOffset);
        Assert.True(address.HasBit);
    }

    [Fact]
    public void 不带位偏移时标记为未指定()
    {
        // 这个标记决定了布尔值的解释方式：取某一位，还是整寄存器非零为真
        Assert.False(ModbusAddressParser.Parse("HR100").HasBit);
    }

    [Theory]
    [InlineData("", "地址为空")]
    [InlineData("HR100.16", "0-15")]
    [InlineData("CO5.1", "本身就是位")]
    [InlineData("40000", "不能为 0")]
    [InlineData("20001", "必须是 0/1/3/4")]
    [InlineData("XY100", "无法识别的地址前缀")]
    [InlineData("999:HR0", "从站号")]
    public void 非法地址给出可读的失败原因(string input, string expectedFragment)
    {
        Assert.False(ModbusAddressParser.TryParse(input, 1, out _, out var reason));
        Assert.Contains(expectedFragment, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse失败时抛出配置类异常()
    {
        var ex = Assert.Throws<AddressFormatException>(() => ModbusAddressParser.Parse("HR100.99"));

        Assert.Equal("HR100.99", ex.Address);
    }

    [Theory]
    [InlineData(ModbusArea.Coil, true, true)]
    [InlineData(ModbusArea.DiscreteInput, true, false)]
    [InlineData(ModbusArea.HoldingRegister, false, true)]
    [InlineData(ModbusArea.InputRegister, false, false)]
    public void 区的位属性与可写性(ModbusArea area, bool isBit, bool writable)
    {
        Assert.Equal(isBit, area.IsBitArea());
        Assert.Equal(writable, area.IsWritable());
    }
}
