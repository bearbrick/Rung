using System.Buffers.Binary;
using Rung.Abstractions;
using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7ResponseReaderTests
{
    [Fact]
    public void 从TPKT头读出整帧长度()
    {
        // 传输层靠它决定还要再收多少字节，这是防半包/粘包的第一道关
        var frame = HexFixture.Load("read-response-single-real");

        Assert.Equal(frame.Length, S7ResponseReader.ReadFrameLength(frame.AsSpan(0, 4)));
    }

    [Fact]
    public void 通讯建立响应给出协商后的PDU长度()
    {
        var frame = HexFixture.Load("setup-communication-response-240");

        Assert.Equal(240, S7ResponseReader.ReadNegotiatedPduLength(frame));
    }

    [Fact]
    public void 单项读响应取回原始字节()
    {
        var frame = HexFixture.Load("read-response-single-real");
        var cursor = S7ResponseReader.ReadResults(frame);

        Assert.Equal(1, cursor.ItemCount);
        Assert.True(cursor.TryReadNext(out var code, out var data));
        Assert.Equal(S7ReturnCode.Success, code);
        Assert.Equal("422a0000", HexFixture.ToHex(data));

        // 大端解释后应当正好是 42.5
        Assert.Equal(42.5f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data)));
        Assert.False(cursor.TryReadNext(out _, out _));
    }

    [Fact]
    public void 奇数长度的数据项之间有填充字节()
    {
        // 漏掉填充，从第二项起全部错位；而且错得很隐蔽——长度对得上，值是错的
        var frame = HexFixture.Load("read-response-odd-lengths-padding");
        var cursor = S7ResponseReader.ReadResults(frame);

        Assert.Equal(3, cursor.ItemCount);

        Assert.True(cursor.TryReadNext(out var c1, out var d1));
        Assert.Equal(S7ReturnCode.Success, c1);
        Assert.Equal("11", HexFixture.ToHex(d1));

        Assert.True(cursor.TryReadNext(out var c2, out var d2));
        Assert.Equal(S7ReturnCode.Success, c2);
        Assert.Equal("22", HexFixture.ToHex(d2));

        Assert.True(cursor.TryReadNext(out var c3, out var d3));
        Assert.Equal(S7ReturnCode.Success, c3);
        Assert.Equal("3344", HexFixture.ToHex(d3));

        Assert.Equal(3, cursor.Consumed);
    }

    [Fact]
    public void 单项失败不影响同一批次的其余点位()
    {
        // 产线上 DB 号配错一个是常事，绝不能因此让整台设备的采集全灭
        var frame = HexFixture.Load("read-response-partial-failure");
        var cursor = S7ResponseReader.ReadResults(frame);

        Assert.True(cursor.TryReadNext(out var failed, out var emptyData));
        Assert.Equal(S7ReturnCode.ObjectDoesNotExist, failed);
        Assert.True(emptyData.IsEmpty);

        Assert.True(cursor.TryReadNext(out var ok, out var data));
        Assert.Equal(S7ReturnCode.Success, ok);
        Assert.Equal("1234", HexFixture.ToHex(data));
    }

    [Fact]
    public void 八位组串的长度以字节计而不是位()
    {
        // 传输尺寸 0x09 的长度字段单位与 0x04 不同。
        // 照搬除 8 的逻辑会得到 0 字节，现场表现为"偶尔读到空值"
        var frame = HexFixture.Load("read-response-octet-string");
        var cursor = S7ResponseReader.ReadResults(frame);

        Assert.True(cursor.TryReadNext(out var code, out var data));
        Assert.Equal(S7ReturnCode.Success, code);
        Assert.Equal(4, data.Length);
        Assert.Equal("deadbeef", HexFixture.ToHex(data));
    }

    [Fact]
    public void 单个位占用一个字节()
    {
        var frame = HexFixture.Load("read-response-single-bit");
        var cursor = S7ResponseReader.ReadResults(frame);

        Assert.True(cursor.TryReadNext(out var code, out var data));
        Assert.Equal(S7ReturnCode.Success, code);
        Assert.Equal(1, data.Length);
        Assert.Equal(0x01, data[0]);
    }

    [Fact]
    public void 写响应返回成功码()
    {
        var frame = HexFixture.Load("write-response-ok");

        Assert.Equal(S7ReturnCode.Success, S7ResponseReader.ReadWriteResult(frame));
    }

    [Fact]
    public void 声明长度与实际字节数不符时报错()
    {
        // 半包被当成整包处理，是解析器最危险的失败方式：它会读出一堆看似合法的垃圾
        var frame = HexFixture.Load("read-response-single-real");
        var truncated = frame.AsSpan(0, frame.Length - 2).ToArray();

        var ex = Assert.Throws<ProtocolException>(() => S7ResponseReader.ReadResults(truncated));
        Assert.Contains("TPKT 声明帧长", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 数据段被截断时报错而不是越界读取()
    {
        var frame = HexFixture.Load("read-response-single-real");

        // 砍掉最后 2 字节数据，同时把 TPKT 声明长度改成一致，制造"头部合法但数据不足"
        var truncated = frame.AsSpan(0, frame.Length - 2).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(2), (ushort)truncated.Length);

        var ex = Assert.Throws<ProtocolException>(() =>
        {
            var cursor = S7ResponseReader.ReadResults(truncated);
            cursor.TryReadNext(out _, out _);
        });

        Assert.Contains("截断", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 整帧级错误码被识别为协议异常()
    {
        var frame = HexFixture.Load("read-response-single-real");
        frame[17] = 0x81; // 错误类别：应用关系错误
        frame[18] = 0x04;

        var ex = Assert.Throws<ProtocolException>(() => S7ResponseReader.ReadResults(frame));
        Assert.Contains("0x81", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 拒绝连接的COTP响应给出可操作的提示()
    {
        // 机架槽号配错是新设备接入时最高频的问题，错误信息必须直接指向它
        var disconnectRequest = new byte[] { 0x03, 0x00, 0x00, 0x07, 0x02, 0x80, 0x00 };

        var ex = Assert.Throws<ProtocolException>(
            () => S7ResponseReader.ValidateConnectionConfirm(disconnectRequest));

        Assert.Contains("机架号/槽号", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 正常的连接确认通过校验()
    {
        var confirm = new byte[] { 0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, 0x00, 0x01 };

        S7ResponseReader.ValidateConnectionConfirm(confirm);
    }
}
