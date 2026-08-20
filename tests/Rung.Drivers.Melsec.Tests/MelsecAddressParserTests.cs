using Rung.Abstractions;
using Rung.Protocols.Melsec;
using Xunit;

namespace Rung.Drivers.Melsec.Tests;

public class MelsecAddressParserTests
{
    [Theory]
    // 十进制编号的软元件
    [InlineData("D100", MelsecDevice.D, 100)]
    [InlineData("M200", MelsecDevice.M, 200)]
    [InlineData("R500", MelsecDevice.R, 500)]
    [InlineData("L10", MelsecDevice.L, 10)]
    [InlineData("F5", MelsecDevice.F, 5)]
    [InlineData("TN10", MelsecDevice.TN, 10)]
    [InlineData("CN20", MelsecDevice.CN, 20)]
    // 十六进制编号的软元件
    [InlineData("X1F", MelsecDevice.X, 31)]
    [InlineData("Y2A", MelsecDevice.Y, 42)]
    [InlineData("B100", MelsecDevice.B, 256)]
    [InlineData("W1A0", MelsecDevice.W, 416)]
    [InlineData("ZR3000", MelsecDevice.ZR, 12288)]
    // 大小写与空白不敏感
    [InlineData("d100", MelsecDevice.D, 100)]
    [InlineData("  X1f  ", MelsecDevice.X, 31)]
    public void 解析常见地址(string input, MelsecDevice device, int number)
    {
        var address = MelsecAddressParser.Parse(input);

        Assert.Equal(device, address.Device);
        Assert.Equal(number, address.Number);
    }

    [Fact]
    public void XY这类软元件按十六进制解析()
    {
        // 三菱接入时最容易踩、也最难自己发现的坑：X10 是第 16 点不是第 10 点。
        // 按十进制读会读到隔壁的点，值看着"像那么回事"但就是不对
        Assert.Equal(16, MelsecAddressParser.Parse("X10").Number);
        Assert.Equal(10, MelsecAddressParser.Parse("M10").Number);
        Assert.Equal(16, MelsecAddressParser.Parse("W10").Number);
        Assert.Equal(10, MelsecAddressParser.Parse("D10").Number);
    }

    [Fact]
    public void 两字母软元件优先匹配()
    {
        // ZR100 不能被当成 Z + R100
        Assert.Equal(MelsecDevice.ZR, MelsecAddressParser.Parse("ZR100").Device);
        Assert.Equal(MelsecDevice.TN, MelsecAddressParser.Parse("TN5").Device);
    }

    [Theory]
    [InlineData("", "地址为空")]
    [InlineData("Q100", "未知的软元件前缀")]
    [InlineData("D", "缺少软元件编号")]
    [InlineData("DABC", "十进制")]
    [InlineData("XGG", "十六进制")]
    public void 非法地址给出可读的失败原因(string input, string fragment)
    {
        Assert.False(MelsecAddressParser.TryParse(input, out _, out var reason));
        Assert.Contains(fragment, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse失败时抛出配置类异常()
    {
        var ex = Assert.Throws<AddressFormatException>(() => MelsecAddressParser.Parse("Q1"));

        Assert.Equal("Q1", ex.Address);
    }

    [Theory]
    [InlineData(MelsecDevice.X, true, true)]
    [InlineData(MelsecDevice.M, true, false)]
    [InlineData(MelsecDevice.D, false, false)]
    [InlineData(MelsecDevice.W, false, true)]
    public void 软元件的位属性与进制(MelsecDevice device, bool isBit, bool isHex)
    {
        Assert.Equal(isBit, device.IsBitDevice());
        Assert.Equal(isHex, device.IsHexadecimal());
    }

    [Fact]
    public void 十六进制软元件的字符串形式也用十六进制()
    {
        // 往返一致：解析出来再打印回去，应该还是同一个地址
        Assert.Equal("X1F", MelsecAddressParser.Parse("X1F").ToString());
        Assert.Equal("D100", MelsecAddressParser.Parse("D100").ToString());
    }
}
