using Rung.Abstractions;
using Xunit;

namespace Rung.Protocols.S7.Tests;

public class S7ValueCodecTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 2, 30, 0, DateTimeKind.Utc);

    private static TagDef Tag(
        TagDataType type,
        ByteOrder order = ByteOrder.ABCD,
        double scale = 1.0,
        double offset = 0.0,
        int length = 0)
        => new()
        {
            Name = "t",
            Address = "DB1.DBW0",
            DataType = type,
            ByteOrder = order,
            Scale = scale,
            Offset = offset,
            Length = length,
        };

    // ---- 字节序：四种排列每种都要有向量。错了不会崩，只会读出一个像样的错数 ----

    [Theory]
    [InlineData(ByteOrder.ABCD, "12345678")] // 原样
    [InlineData(ByteOrder.CDAB, "56781234")] // 换字顺序
    [InlineData(ByteOrder.BADC, "34127856")] // 字内换字节
    [InlineData(ByteOrder.DCBA, "78563412")] // 两者都换，等于整体倒序
    public void 四字节的四种排列都还原成同一个数(ByteOrder order, string hex)
    {
        var raw = Convert.FromHexString(hex);

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.Int32, order), 0, Now);

        Assert.Equal(0x12345678, value.AsInt64());
    }

    [Theory]
    [InlineData(ByteOrder.ABCD, "1234")]
    [InlineData(ByteOrder.CDAB, "1234")] // 只有一个字，换字顺序是空操作
    [InlineData(ByteOrder.BADC, "3412")]
    [InlineData(ByteOrder.DCBA, "3412")]
    public void 两字节类型只区分大端和小端(ByteOrder order, string hex)
    {
        var raw = Convert.FromHexString(hex);

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.Int16, order), 0, Now);

        Assert.Equal(0x1234, value.AsInt64());
    }

    /// <remarks>
    /// ABCD 这套命名本身只定义了 4 字节的行为，扩展到 8 字节有歧义。
    /// 这里采用一致的推广方式：CDAB 是整体按字倒序（而不是交换两个 32 位半）。
    /// 好处是 DCBA 自然等于整体字节倒序，也就是标准的 64 位小端。
    /// 真机上遇到不符的型号，加一个枚举值，不要改这里的语义。
    /// </remarks>
    [Theory]
    [InlineData(ByteOrder.ABCD, "0123456789abcdef")]
    [InlineData(ByteOrder.CDAB, "cdef89ab45670123")]
    [InlineData(ByteOrder.BADC, "23016745ab89efcd")]
    [InlineData(ByteOrder.DCBA, "efcdab8967452301")]
    public void 八字节类型沿用同一套换算规则(ByteOrder order, string hex)
    {
        var raw = Convert.FromHexString(hex);

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.Int64, order), 0, Now);

        Assert.Equal(0x0123456789ABCDEF, value.AsInt64());
    }

    [Theory]
    [InlineData(ByteOrder.ABCD, "422a0000")]
    [InlineData(ByteOrder.CDAB, "0000422a")]
    [InlineData(ByteOrder.BADC, "2a420000")]
    [InlineData(ByteOrder.DCBA, "00002a42")]
    public void 浮点数走同一条字节序路径(ByteOrder order, string hex)
    {
        var raw = Convert.FromHexString(hex);

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.Float32, order), 0, Now);

        Assert.Equal(42.5, value.AsDouble());
        Assert.Equal(TagDataType.Float32, value.DataType);
    }

    // ---- 有符号与边界 ----

    [Fact]
    public void 负数按补码还原()
    {
        var value = S7ValueCodec.Decode(Convert.FromHexString("FFFF"), Tag(TagDataType.Int16), 0, Now);

        Assert.Equal(-1, value.AsInt64());
    }

    [Fact]
    public void 无符号类型不被误读成负数()
    {
        var value = S7ValueCodec.Decode(Convert.FromHexString("FFFF"), Tag(TagDataType.UInt16), 0, Now);

        Assert.Equal(65535, value.AsInt64());
    }

    [Fact]
    public void 六十四位无符号数的高位不丢失()
    {
        // long 位槽存 ulong 时会溢出成负数，取值路径必须走无符号解释
        var value = S7ValueCodec.Decode(
            Convert.FromHexString("FFFFFFFFFFFFFFFF"), Tag(TagDataType.UInt64), 0, Now);

        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(value.ToObject()));
    }

    // ---- 位 ----

    [Theory]
    [InlineData(0b0000_0001, 0, true)]
    [InlineData(0b0010_0000, 5, true)]
    [InlineData(0b1101_1111, 5, false)]
    [InlineData(0b1000_0000, 7, true)]
    public void 按位偏移取出布尔值(byte raw, byte bitOffset, bool expected)
    {
        var value = S7ValueCodec.Decode([raw], Tag(TagDataType.Bool), bitOffset, Now);

        Assert.Equal(expected, value.AsBool());
    }

    // ---- 线性换算 ----

    [Fact]
    public void 换算后的值类型提升为双精度()
    {
        // PLC 里存 2350，倍率 0.1 → 235.0 度。
        // DataType 描述 PLC 侧存储形式，应用侧拿到的是工程值
        var tag = Tag(TagDataType.Int16, scale: 0.1);

        var value = S7ValueCodec.Decode(Convert.FromHexString("092E"), tag, 0, Now);

        Assert.Equal(TagDataType.Float64, value.DataType);
        Assert.Equal(235.0, value.AsDouble(), precision: 10);
    }

    [Fact]
    public void 倍率与偏移同时生效()
    {
        var tag = Tag(TagDataType.Int16, scale: 0.1, offset: -40.0);

        var value = S7ValueCodec.Decode(Convert.FromHexString("092E"), tag, 0, Now);

        Assert.Equal(195.0, value.AsDouble(), precision: 10);
    }

    [Fact]
    public void 未配置换算时保留原始类型()
    {
        var value = S7ValueCodec.Decode(Convert.FromHexString("092E"), Tag(TagDataType.Int16), 0, Now);

        Assert.Equal(TagDataType.Int16, value.DataType);
        Assert.Equal(2350, value.AsInt64());
    }

    // ---- 西门子 STRING ----

    [Fact]
    public void 西门子字符串跳过两字节头部()
    {
        // [最大容量][实际长度][字符…]
        byte[] raw = [10, 5, (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0, 0, 0];

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.String, length: 10), 0, Now);

        Assert.Equal("Hello", value.AsString());
    }

    [Fact]
    public void 字符串读取长度含两字节头部()
    {
        // 这 2 个字节是 S7 特有的，漏算会导致最后两个字符读不到
        Assert.Equal(12, S7ValueCodec.GetReadByteLength(Tag(TagDataType.String, length: 10)));
        Assert.Equal(10, S7ValueCodec.GetReadByteLength(Tag(TagDataType.Bytes, length: 10)));
        Assert.Equal(4, S7ValueCodec.GetReadByteLength(Tag(TagDataType.Float32)));
    }

    [Fact]
    public void 实际长度字段异常时不越界()
    {
        // DB 没初始化过时这个字节可能是任意值，直接拿它切片会崩
        byte[] raw = [10, 250, (byte)'A', (byte)'B', 0, 0, 0, 0, 0, 0, 0, 0];

        var value = S7ValueCodec.Decode(raw, Tag(TagDataType.String, length: 10), 0, Now);

        Assert.Equal(10, value.AsString().Length);
    }

    // ---- 编码：写命令走的路径 ----

    [Theory]
    [InlineData(ByteOrder.ABCD, "12345678")]
    [InlineData(ByteOrder.CDAB, "56781234")]
    [InlineData(ByteOrder.BADC, "34127856")]
    [InlineData(ByteOrder.DCBA, "78563412")]
    public void 编码是解码的逆运算(ByteOrder order, string expectedHex)
    {
        var tag = Tag(TagDataType.Int32, order);
        var buffer = new byte[4];

        var written = S7ValueCodec.Encode(buffer, tag, TagValue.FromInteger(TagDataType.Int32, 0x12345678, Now));

        Assert.Equal(4, written);
        Assert.Equal(expectedHex, HexFixture.ToHex(buffer));

        // 再解回来必须原样
        Assert.Equal(0x12345678, S7ValueCodec.Decode(buffer, tag, 0, Now).AsInt64());
    }

    [Fact]
    public void 写入时把工程值反算回原始值()
    {
        // 应用侧下发 235.0 度，PLC 里要存的是 2350
        var tag = Tag(TagDataType.Int16, scale: 0.1);
        var buffer = new byte[2];

        S7ValueCodec.Encode(buffer, tag, TagValue.FromDouble(235.0, Now));

        Assert.Equal("092e", HexFixture.ToHex(buffer));
    }

    [Fact]
    public void 布尔写入编码成单字节()
    {
        var buffer = new byte[1];

        Assert.Equal(1, S7ValueCodec.Encode(buffer, Tag(TagDataType.Bool), TagValue.FromBool(true, Now)));
        Assert.Equal(1, buffer[0]);

        S7ValueCodec.Encode(buffer, Tag(TagDataType.Bool), TagValue.FromBool(false, Now));
        Assert.Equal(0, buffer[0]);
    }

    [Theory]
    [InlineData(TagDataType.Int16, ByteOrder.CDAB)]
    [InlineData(TagDataType.Int32, ByteOrder.BADC)]
    [InlineData(TagDataType.Int64, ByteOrder.DCBA)]
    [InlineData(TagDataType.Float32, ByteOrder.CDAB)]
    [InlineData(TagDataType.Float64, ByteOrder.BADC)]
    public void 编解码在所有类型与字节序组合下往返一致(TagDataType type, ByteOrder order)
    {
        var tag = Tag(type, order);
        var buffer = new byte[8];

        var original = type is TagDataType.Float32 or TagDataType.Float64
            ? TagValue.FromDouble(-1234.5, Now)
            : TagValue.FromInteger(type, -12345, Now);

        if (type == TagDataType.Float32)
        {
            original = TagValue.FromSingle(-1234.5f, Now);
        }

        S7ValueCodec.Encode(buffer, tag, original);
        var roundTripped = S7ValueCodec.Decode(buffer, tag, 0, Now);

        Assert.Equal(original.AsDouble(), roundTripped.AsDouble(), precision: 6);
    }

    [Fact]
    public void 字节数不足时明确报错而不是越界()
    {
        var ex = Assert.Throws<ProtocolException>(
            () => S7ValueCodec.Decode(new byte[2], Tag(TagDataType.Float32), 0, Now));

        Assert.Contains("需要 4 字节", ex.Message, StringComparison.Ordinal);
    }
}
