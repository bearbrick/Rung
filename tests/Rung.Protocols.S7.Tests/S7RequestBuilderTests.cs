using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7RequestBuilderTests
{
    [Theory]
    [InlineData(0, 1, "cotp-connect-request-rack0-slot1")]
    [InlineData(1, 2, "cotp-connect-request-rack1-slot2")]
    public void 连接请求逐字节匹配夹具(byte rack, byte slot, string fixture)
    {
        var expected = HexFixture.Load(fixture);
        var buffer = new byte[expected.Length];

        var written = S7RequestBuilder.WriteConnectionRequest(buffer, rack, slot);

        Assert.Equal(expected.Length, written);
        Assert.Equal(HexFixture.ToHex(expected), HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 通讯建立请求逐字节匹配夹具()
    {
        var expected = HexFixture.Load("setup-communication-request");
        var buffer = new byte[S7RequestBuilder.SetupCommunicationLength];

        var written = S7RequestBuilder.WriteSetupCommunication(buffer, pduReference: 1);

        Assert.Equal(expected.Length, written);
        Assert.Equal(HexFixture.ToHex(expected), HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 单项读请求逐字节匹配夹具()
    {
        var expected = HexFixture.Load("read-request-single-db1-dbd20");
        var buffer = new byte[S7RequestBuilder.GetReadRequestLength(1)];

        var items = new[] { S7ReadItem.Bytes(S7AddressParser.Parse("DB1.DBD20"), 4) };
        var written = S7RequestBuilder.WriteReadRequest(buffer, pduReference: 2, items);

        Assert.Equal(expected.Length, written);
        Assert.Equal(HexFixture.ToHex(expected), HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 多项混合读请求逐字节匹配夹具()
    {
        // 一次请求里同时有按字节读和按位读，两者的传输尺寸字节不同（0x02 / 0x01）
        var expected = HexFixture.Load("read-request-two-items-mixed");
        var buffer = new byte[S7RequestBuilder.GetReadRequestLength(2)];

        var items = new[]
        {
            S7ReadItem.Bytes(S7AddressParser.Parse("MW100"), 2),
            S7ReadItem.Bit(S7AddressParser.Parse("DB2.DBX0.3")),
        };
        var written = S7RequestBuilder.WriteReadRequest(buffer, pduReference: 3, items);

        Assert.Equal(expected.Length, written);
        Assert.Equal(HexFixture.ToHex(expected), HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 写请求逐字节匹配夹具()
    {
        var expected = HexFixture.Load("write-request-db1-dbw10");
        var buffer = new byte[S7RequestBuilder.GetWriteRequestLength(2)];

        var payload = new byte[] { 0x04, 0xD2 }; // 1234，大端
        var written = S7RequestBuilder.WriteWriteRequest(
            buffer, pduReference: 9, S7AddressParser.Parse("DB1.DBW10"), payload);

        Assert.Equal(expected.Length, written);
        Assert.Equal(HexFixture.ToHex(expected), HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 报文长度计算与实际写入一致()
    {
        // 长度算错会让 TPKT 声明和实际字节数对不上，对端直接丢包
        for (var itemCount = 1; itemCount <= 16; itemCount++)
        {
            var items = Enumerable.Range(0, itemCount)
                .Select(i => S7ReadItem.Bytes(new S7Address(S7Area.DataBlock, 1, i * 4, 0), 4))
                .ToArray();

            var expectedLength = S7RequestBuilder.GetReadRequestLength(itemCount);
            var buffer = new byte[expectedLength];

            var written = S7RequestBuilder.WriteReadRequest(buffer, 1, items);

            Assert.Equal(expectedLength, written);
            Assert.Equal(expectedLength, (buffer[2] << 8) | buffer[3]);
        }
    }

    [Fact]
    public void 缓冲区不足时明确报错而不是越界()
    {
        var buffer = new byte[10];
        var items = new[] { S7ReadItem.Bytes(new S7Address(S7Area.Memory, 0, 0, 0), 2) };

        var ex = Assert.Throws<ArgumentException>(() => S7RequestBuilder.WriteReadRequest(buffer, 1, items));
        Assert.Contains("缓冲区不足", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 空数据项列表被拒绝()
        => Assert.Throws<ArgumentException>(
            () => S7RequestBuilder.WriteReadRequest(new byte[64], 1, ReadOnlySpan<S7ReadItem>.Empty));

    [Fact]
    public void 定时器和计数器使用专用的传输尺寸()
    {
        var buffer = new byte[S7RequestBuilder.GetReadRequestLength(1)];
        var items = new[] { S7ReadItem.Bytes(S7AddressParser.Parse("T5"), 2) };

        S7RequestBuilder.WriteReadRequest(buffer, 1, items);

        // 数据项规格从偏移 19 开始，第 4 个字节是传输尺寸
        Assert.Equal(0x1D, buffer[19 + 3]);
        Assert.Equal((byte)S7Area.Timer, buffer[19 + 8]);
    }
}
