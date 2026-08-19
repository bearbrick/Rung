using Rung.Abstractions;
using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7AddressParserTests
{
    [Theory]
    // 数据块，标准写法
    [InlineData("DB1.DBX0.5", S7Area.DataBlock, 1, 0, 5, S7SizeHint.Bit)]
    [InlineData("DB1.DBB10", S7Area.DataBlock, 1, 10, 0, S7SizeHint.Byte)]
    [InlineData("DB1.DBW10", S7Area.DataBlock, 1, 10, 0, S7SizeHint.Word)]
    [InlineData("DB100.DBD200", S7Area.DataBlock, 100, 200, 0, S7SizeHint.DWord)]
    // 数据块，省略 DBx 的简写
    [InlineData("DB2.10.3", S7Area.DataBlock, 2, 10, 3, S7SizeHint.Bit)]
    [InlineData("DB2.10", S7Area.DataBlock, 2, 10, 0, S7SizeHint.None)]
    // 位存储区
    [InlineData("M100.0", S7Area.Memory, 0, 100, 0, S7SizeHint.Bit)]
    [InlineData("MB100", S7Area.Memory, 0, 100, 0, S7SizeHint.Byte)]
    [InlineData("MW100", S7Area.Memory, 0, 100, 0, S7SizeHint.Word)]
    [InlineData("MD100", S7Area.Memory, 0, 100, 0, S7SizeHint.DWord)]
    [InlineData("MX100.7", S7Area.Memory, 0, 100, 7, S7SizeHint.Bit)]
    // 输入输出
    [InlineData("I0.0", S7Area.Input, 0, 0, 0, S7SizeHint.Bit)]
    [InlineData("IW4", S7Area.Input, 0, 4, 0, S7SizeHint.Word)]
    [InlineData("Q1.3", S7Area.Output, 0, 1, 3, S7SizeHint.Bit)]
    [InlineData("QB2", S7Area.Output, 0, 2, 0, S7SizeHint.Byte)]
    // 德文助记符：从西门子德文界面导出的地址表里很常见
    [InlineData("E0.1", S7Area.Input, 0, 0, 1, S7SizeHint.Bit)]
    [InlineData("A0.2", S7Area.Output, 0, 0, 2, S7SizeHint.Bit)]
    [InlineData("EW8", S7Area.Input, 0, 8, 0, S7SizeHint.Word)]
    // 定时器与计数器
    [InlineData("T5", S7Area.Timer, 0, 5, 0, S7SizeHint.None)]
    [InlineData("C3", S7Area.Counter, 0, 3, 0, S7SizeHint.None)]
    // 大小写与空白不敏感
    [InlineData("db1.dbw10", S7Area.DataBlock, 1, 10, 0, S7SizeHint.Word)]
    [InlineData("  MW100  ", S7Area.Memory, 0, 100, 0, S7SizeHint.Word)]
    public void 能解析常见地址写法(
        string input,
        S7Area area,
        int dbNumber,
        int byteOffset,
        int bitOffset,
        S7SizeHint sizeHint)
    {
        Assert.True(S7AddressParser.TryParse(input, out var address, out var reason), reason);

        Assert.Equal(area, address.Area);
        Assert.Equal((ushort)dbNumber, address.DbNumber);
        Assert.Equal(byteOffset, address.ByteOffset);
        Assert.Equal((byte)bitOffset, address.BitOffset);
        Assert.Equal(sizeHint, address.SizeHint);
    }

    [Fact]
    public void S7200的V区映射到DB1()
    {
        var address = S7AddressParser.Parse("VW100");

        Assert.Equal(S7Area.DataBlock, address.Area);
        Assert.Equal(1, address.DbNumber);
        Assert.Equal(100, address.ByteOffset);
    }

    [Theory]
    [InlineData("", "地址为空")]
    [InlineData("   ", "地址为空")]
    [InlineData("DB1", "分隔符")]
    [InlineData("DB0.DBW0", "不能为 0")]
    [InlineData("DB1.DBQ10", "宽度字母")]
    [InlineData("DB1.DBX0.8", "0-7")]
    [InlineData("DB1.DBX0", "缺少位偏移")]
    [InlineData("DB1.DBW10.2", "不应带位偏移")]
    [InlineData("M-5.0", "字节偏移")]
    [InlineData("XW100", "存储区前缀")]
    [InlineData("MW", "字节偏移")]
    public void 非法地址给出可读的失败原因(string input, string expectedFragment)
    {
        Assert.False(S7AddressParser.TryParse(input, out _, out var reason));
        Assert.Contains(expectedFragment, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse失败时抛出配置类异常()
    {
        // 地址错属于配置错误，重试没有意义——异常类型必须能让调度器区分出这一点
        var ex = Assert.Throws<AddressFormatException>(() => S7AddressParser.Parse("DB1.DBX0.9"));

        Assert.Equal("DB1.DBX0.9", ex.Address);
        Assert.Contains("0-7", ex.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DB1.DBX0.0", 0)]
    [InlineData("DB1.DBX0.5", 5)]
    [InlineData("DB1.DBW10", 80)]
    [InlineData("DB1.DBD20", 160)]
    [InlineData("M100.3", 803)]
    public void 位地址等于字节偏移乘八加位偏移(string input, int expected)
    {
        // 即便按字节访问，Any 指针要求的也是位地址。这个换算错了会读到完全不相干的内存
        Assert.Equal(expected, S7AddressParser.Parse(input).BitAddress);
    }

    [Fact]
    public void 偏移后的地址清空位偏移和宽度提示()
    {
        var moved = S7AddressParser.Parse("DB1.DBX10.5").AtOffset(4);

        Assert.Equal(14, moved.ByteOffset);
        Assert.Equal(0, moved.BitOffset);
        Assert.Equal(S7SizeHint.None, moved.SizeHint);
        Assert.Equal(1, moved.DbNumber);
    }
}
