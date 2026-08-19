using Xunit;

namespace Rung.Simulator.Tests;

/// <summary>
/// 模拟器的地址解析是独立于 Rung 重新实现的一份。
/// 它必须自己正确，否则两边"互为对照"就无从谈起。
/// </summary>
public class SimulatedMemoryTests
{
    [Theory]
    [InlineData("DB1.DBW0", 0x84, 1, 0, 0)]
    [InlineData("DB1.DBD20", 0x84, 1, 20, 0)]
    [InlineData("DB10.DBX8.3", 0x84, 10, 8, 3)]
    [InlineData("DB2.10", 0x84, 2, 10, 0)]
    [InlineData("MW100", 0x83, 0, 100, 0)]
    [InlineData("M0.0", 0x83, 0, 0, 0)]
    [InlineData("I0.7", 0x81, 0, 0, 7)]
    [InlineData("Q1.2", 0x82, 0, 1, 2)]
    public void 解析常见地址(string address, byte area, int db, int offset, int bit)
    {
        var parsed = SimulatedMemory.ParseAddress(address);

        Assert.Equal(area, parsed.Area);
        Assert.Equal(db, parsed.DbNumber);
        Assert.Equal(offset, parsed.ByteOffset);
        Assert.Equal(bit, parsed.BitOffset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("垃圾")]
    [InlineData("DB1")]
    public void 非法地址被拒绝(string address)
        => Assert.Throws<ArgumentException>(() => SimulatedMemory.ParseAddress(address));

    [Fact]
    public void 数值按大端写入()
    {
        var memory = new SimulatedMemory();

        memory.Write(SimulatedMemory.ParseAddress("DB1.DBW0"), "Int16", 1234);

        Assert.Equal([0x04, 0xD2], memory.Read(0x84, 1, 0, 2));
    }

    [Fact]
    public void 浮点数按大端写入()
    {
        var memory = new SimulatedMemory();

        memory.Write(SimulatedMemory.ParseAddress("DB1.DBD0"), "Float32", 42.5);

        Assert.Equal([0x42, 0x2A, 0x00, 0x00], memory.Read(0x84, 1, 0, 4));
    }

    [Fact]
    public void 写位不影响同字节的其余位()
    {
        var memory = new SimulatedMemory();
        memory.GetArea(0x84, 1)[0] = 0b1010_0101;

        memory.Write(SimulatedMemory.ParseAddress("DB1.DBX0.1"), "Bool", 1);

        Assert.Equal([0b1010_0111], memory.Read(0x84, 1, 0, 1));
    }

    [Fact]
    public void 清位也只影响目标位()
    {
        var memory = new SimulatedMemory();
        memory.GetArea(0x84, 1)[0] = 0b1111_1111;

        memory.Write(SimulatedMemory.ParseAddress("DB1.DBX0.3"), "Bool", 0);

        Assert.Equal([0b1111_0111], memory.Read(0x84, 1, 0, 1));
    }
}
