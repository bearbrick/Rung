using System.Globalization;

namespace Rung.Protocols.S7.Tests;

/// <summary>
/// 报文夹具加载器。夹具是纯文本的十六进制快照，因此
/// 将来用 Wireshark 抓到真机报文后，直接替换文件内容即可，测试代码一行不动。
/// </summary>
internal static class HexFixture
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>加载一份夹具的报文字节。</summary>
    public static byte[] Load(string name)
    {
        var path = Path.Combine(FixtureDirectory, name + ".hex");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到报文夹具 {name}.hex（查找目录 {FixtureDirectory}）", path);
        }

        var hex = string.Concat(File.ReadAllLines(path)
            .Select(StripComment)
            .SelectMany(static line => line.Where(static c => !char.IsWhiteSpace(c))));

        if (hex.Length % 2 != 0)
        {
            throw new InvalidDataException($"夹具 {name} 的十六进制字符数为奇数（{hex.Length}）");
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>读取夹具头部的一项元数据，如 <c>source</c>、<c>direction</c>。</summary>
    public static string? ReadMetadata(string name, string key)
    {
        var path = Path.Combine(FixtureDirectory, name + ".hex");
        var prefix = "# " + key + ":";

        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>枚举全部夹具名。</summary>
    public static IEnumerable<string> EnumerateNames()
        => Directory.EnumerateFiles(FixtureDirectory, "*.hex")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .OrderBy(static name => name, StringComparer.Ordinal);

    /// <summary>把字节序列格式化成可读的十六进制，断言失败时看得懂差在哪一位。</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? line : line[..hash];
    }
}
