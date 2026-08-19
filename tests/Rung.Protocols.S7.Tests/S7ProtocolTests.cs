using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7ProtocolTests
{
    [Theory]
    [InlineData(240, 222)]  // S7-300 典型协商值
    [InlineData(480, 462)]  // S7-1200/1500 典型协商值
    [InlineData(960, 942)]
    public void 单次可读字节数与西门子社区经验值一致(int pduLength, int expected)
        => Assert.Equal(expected, S7Protocol.MaxReadBytes(pduLength));

    [Theory]
    [InlineData(240, 205)]
    [InlineData(480, 445)]
    public void 单次可写字节数与Snap7保持一致(int pduLength, int expected)
        => Assert.Equal(expected, S7Protocol.MaxWriteBytes(pduLength));

    [Theory]
    [InlineData(240, 19)]
    [InlineData(480, 39)]
    public void 单个请求的数据项个数上限(int pduLength, int expected)
        => Assert.Equal(expected, S7Protocol.MaxReadItems(pduLength));

    [Fact]
    public void 容量判定把填充字节算进去()
    {
        // 12(头) + 2(参数) + 3 项各 (4 + 1) + 前两项各 1 字节填充 = 31
        int[] lengths = [1, 1, 1];

        Assert.True(S7Protocol.ResponseFitsInPdu(31, lengths));
        Assert.False(S7Protocol.ResponseFitsInPdu(30, lengths));
    }

    [Fact]
    public void 最后一项不产生填充字节()
    {
        // 单项奇数长度：12 + 2 + 4 + 1 = 19，末项不补填充
        int[] single = [1];

        Assert.True(S7Protocol.ResponseFitsInPdu(19, single));
        Assert.False(S7Protocol.ResponseFitsInPdu(18, single));
    }

    [Theory]
    [InlineData(S7DataTransportSize.Bit, true)]
    [InlineData(S7DataTransportSize.Byte, true)]
    [InlineData(S7DataTransportSize.Int, true)]
    [InlineData(S7DataTransportSize.DInt, false)]
    [InlineData(S7DataTransportSize.Real, false)]
    [InlineData(S7DataTransportSize.OctetString, false)]
    public void 长度单位随传输尺寸变化(S7DataTransportSize size, bool bitCounted)
        => Assert.Equal(bitCounted, S7Protocol.IsBitCountedLength(size));
}
