using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Rung.Simulator;

/// <summary>模拟器内部使用的地址表示。刻意与 Rung 的实现无关。</summary>
/// <param name="Area">区域码：0x84=DB，0x83=M，0x81=I，0x82=Q。</param>
/// <param name="DbNumber">数据块号。</param>
/// <param name="ByteOffset">字节偏移。</param>
/// <param name="BitOffset">位偏移。</param>
public readonly record struct SimAddress(byte Area, ushort DbNumber, int ByteOffset, byte BitOffset);

/// <summary>
/// 模拟设备的存储区，以及一个独立实现的地址解析器。
/// <para>
/// 这里<b>刻意重新实现了一遍地址解析</b>，没有复用 Rung.Protocols.S7。
/// 两边同源的话，一个写错的偏移量会同时体现在模拟器和被测代码上，
/// 测试全绿但真机上一读就错。独立实现才能互为对照。
/// </para>
/// </summary>
public sealed partial class SimulatedMemory
{
    private readonly Dictionary<(byte Area, ushort Db), byte[]> _areas = [];

    /// <summary>每个存储区的大小。</summary>
    public int AreaSize { get; init; } = 8192;

    /// <summary>取得（必要时创建）一个存储区。</summary>
    public byte[] GetArea(byte area, ushort dbNumber)
    {
        if (!_areas.TryGetValue((area, dbNumber), out var buffer))
        {
            buffer = new byte[AreaSize];
            _areas[(area, dbNumber)] = buffer;
        }

        return buffer;
    }

    /// <summary>按类型把一个数值写进存储区。S7 一律大端。</summary>
    public void Write(SimAddress address, string type, double value)
    {
        var span = GetArea(address.Area, address.DbNumber).AsSpan(address.ByteOffset);

        switch (type.ToLowerInvariant())
        {
            case "bool":
                var mask = (byte)(1 << address.BitOffset);
                span[0] = value != 0 ? (byte)(span[0] | mask) : (byte)(span[0] & ~mask);
                break;
            case "int16":
                BinaryPrimitives.WriteInt16BigEndian(span, (short)Math.Round(value));
                break;
            case "uint16":
                BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)Math.Round(value));
                break;
            case "int32":
                BinaryPrimitives.WriteInt32BigEndian(span, (int)Math.Round(value));
                break;
            case "uint32":
                BinaryPrimitives.WriteUInt32BigEndian(span, (uint)Math.Round(value));
                break;
            case "float32":
                BinaryPrimitives.WriteSingleBigEndian(span, (float)value);
                break;
            case "float64":
                BinaryPrimitives.WriteDoubleBigEndian(span, value);
                break;
            default:
                throw new ArgumentException($"未知的数据类型 \"{type}\"", nameof(type));
        }
    }

    /// <summary>读出一段原始字节，供断言使用。</summary>
    public byte[] Read(byte area, ushort dbNumber, int byteOffset, int length)
        => GetArea(area, dbNumber).AsSpan(byteOffset, length).ToArray();

    /// <summary>
    /// 解析地址字符串。只支持模拟器配置里会用到的几种写法，
    /// 够用即可——它不是给现场用的，是给测试和演示用的。
    /// </summary>
    public static SimAddress ParseAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var text = address.Trim().ToUpperInvariant();

        var db = DataBlockPattern().Match(text);
        if (db.Success)
        {
            return new SimAddress(
                0x84,
                ushort.Parse(db.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(db.Groups[3].Value, CultureInfo.InvariantCulture),
                db.Groups[4].Success ? byte.Parse(db.Groups[4].Value, CultureInfo.InvariantCulture) : (byte)0);
        }

        var simple = SimpleAreaPattern().Match(text);
        if (simple.Success)
        {
            var area = simple.Groups[1].Value switch
            {
                "M" => (byte)0x83,
                "I" or "E" => (byte)0x81,
                "Q" or "A" => (byte)0x82,
                _ => throw new ArgumentException($"未知的存储区 \"{simple.Groups[1].Value}\"", nameof(address)),
            };

            return new SimAddress(
                area,
                0,
                int.Parse(simple.Groups[3].Value, CultureInfo.InvariantCulture),
                simple.Groups[4].Success
                    ? byte.Parse(simple.Groups[4].Value, CultureInfo.InvariantCulture)
                    : (byte)0);
        }

        throw new ArgumentException($"无法解析地址 \"{address}\"", nameof(address));
    }

    [GeneratedRegex(@"^DB(\d+)\.(?:DB([XBWD]))?(\d+)(?:\.(\d))?$")]
    private static partial Regex DataBlockPattern();

    [GeneratedRegex(@"^([MIQEA])([XBWD])?(\d+)(?:\.(\d))?$")]
    private static partial Regex SimpleAreaPattern();
}
