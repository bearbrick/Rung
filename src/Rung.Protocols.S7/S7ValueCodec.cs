using System.Text;
using Rung.Abstractions;

namespace Rung.Protocols.S7;

/// <summary>
/// S7 侧的值编解码。
/// <para>
/// 字节序、标量类型、线性换算这些<b>与协议无关</b>的部分交给
/// <see cref="TagValueCodec"/>，这里只处理西门子特有的东西：
/// STRING 的 2 字节头部，以及按位取值。
/// </para>
/// </summary>
public static class S7ValueCodec
{
    /// <summary>
    /// 西门子 STRING 的头部长度：第 1 字节是最大容量，第 2 字节是当前实际长度。
    /// 这 2 个字节是 S7 特有的，不体现在 <see cref="TagDef.ByteLength"/> 里。
    /// </summary>
    public const int S7StringHeaderLength = 2;

    /// <summary>该点位在 S7 上一次读取需要的字节数，含 STRING 的头部。</summary>
    public static int GetReadByteLength(TagDef tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return tag.DataType == TagDataType.String
            ? tag.Length + S7StringHeaderLength
            : tag.ByteLength;
    }

    /// <summary>把原始字节解成一个采集值。</summary>
    /// <param name="raw">该点位对应的原始字节，长度应为 <see cref="GetReadByteLength"/>。</param>
    /// <param name="tag">点位配置。</param>
    /// <param name="bitOffset">位偏移，仅 <see cref="TagDataType.Bool"/> 使用。</param>
    /// <param name="timestampUtc">采集时刻，UTC。</param>
    public static TagValue Decode(ReadOnlySpan<byte> raw, TagDef tag, byte bitOffset, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var required = GetReadByteLength(tag);
        if (raw.Length < required)
        {
            throw new ProtocolException(
                $"点位 {tag.Name} 需要 {required} 字节，实际只有 {raw.Length} 字节");
        }

        return tag.DataType switch
        {
            TagDataType.Bool => TagValue.FromBool((raw[0] & (1 << bitOffset)) != 0, timestampUtc),
            TagDataType.String => TagValue.FromString(DecodeS7String(raw, tag.Length), timestampUtc),
            TagDataType.Bytes => TagValue.FromBytes(raw[..tag.Length].ToArray(), timestampUtc),
            _ => TagValueCodec.DecodeScalar(raw, tag, timestampUtc),
        };
    }

    /// <summary>把一个待写入的值编码成原始字节。</summary>
    /// <returns>写入的字节数。</returns>
    public static int Encode(Span<byte> destination, TagDef tag, TagValue value)
    {
        ArgumentNullException.ThrowIfNull(tag);

        switch (tag.DataType)
        {
            case TagDataType.Bool:
                destination[0] = value.AsBool() ? (byte)1 : (byte)0;
                return 1;
            case TagDataType.String:
                return EncodeS7String(destination, value.AsString(), tag.Length);
            case TagDataType.Bytes:
                var bytes = value.AsBytes();
                bytes.CopyTo(destination);
                return bytes.Length;
            default:
                return TagValueCodec.EncodeScalar(destination, tag, value);
        }
    }

    /// <summary>解析西门子 STRING：[最大容量][实际长度][字符…]。</summary>
    private static string DecodeS7String(ReadOnlySpan<byte> raw, int capacity)
    {
        var actual = raw[1];

        // 实际长度字段来自设备，不可信：DB 没初始化过时这里可能是任意值
        var length = Math.Min(actual, Math.Min(capacity, raw.Length - S7StringHeaderLength));

        return length <= 0
            ? string.Empty
            : Encoding.ASCII.GetString(raw.Slice(S7StringHeaderLength, length));
    }

    private static int EncodeS7String(Span<byte> destination, string value, int capacity)
    {
        var length = Math.Min(value.Length, capacity);

        destination[0] = (byte)capacity;
        destination[1] = (byte)length;
        Encoding.ASCII.GetBytes(value.AsSpan(0, length), destination[S7StringHeaderLength..]);

        return capacity + S7StringHeaderLength;
    }
}
