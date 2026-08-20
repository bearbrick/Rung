using System.Security.Cryptography;
using System.Text;

namespace Rung.Abstractions;

/// <summary>
/// 一个 API 密钥。
/// <para>
/// <b>只存哈希，不存明文。</b>配置库会被备份、会被拷到别人机器上排障，
/// 明文密钥躺在里面就等于没有密钥。生成时把明文给用户一次，之后再也拿不回来。
/// </para>
/// </summary>
/// <param name="Name">调用方名称。写命令的审计日志记的就是它——这是审计能落地的前提。</param>
/// <param name="Hash">密钥的 SHA-256，Base64 编码。</param>
/// <param name="CanWrite">是否允许写点位。只读调用方（看板、报表）不该有写权限。</param>
public sealed record ApiKey(string Name, string Hash, bool CanWrite = false);

/// <summary>API 密钥的生成与校验。</summary>
public static class ApiKeys
{
    /// <summary>密钥的随机字节数。32 字节 = 256 位，暴力穷举不可行。</summary>
    public const int KeyBytes = 32;

    /// <summary>明文密钥的前缀，便于在日志和配置里一眼认出来。</summary>
    public const string Prefix = "rung_";

    /// <summary>
    /// 生成一个新密钥，返回明文。<b>明文只在这一刻存在</b>，调用方必须立刻交给用户。
    /// </summary>
    public static string Generate()
        => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    /// <summary>
    /// 计算密钥哈希。
    /// <para>
    /// 用 SHA-256 而不是 PBKDF2 之类的慢哈希：慢哈希是为<b>低熵的人类密码</b>
    /// 准备的，用来抬高字典攻击的成本。这里的密钥是 256 位随机数，
    /// 本来就没有字典可查，加慢哈希只是给每次请求平白增加延迟。
    /// </para>
    /// </summary>
    public static string ComputeHash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>创建一个新密钥项，同时返回要交给用户的明文。</summary>
    public static (ApiKey Key, string Plaintext) Create(string name, bool canWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var plaintext = Generate();
        return (new ApiKey(name, ComputeHash(plaintext), canWrite), plaintext);
    }

    /// <summary>
    /// 按明文查找匹配的密钥。
    /// <para>
    /// 用固定时间比较：普通的字符串相等会在第一个不同的字节处提前返回，
    /// 攻击者能通过测量响应时间逐字节猜出哈希。这条链路上的每一次比较
    /// 都必须走 <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>。
    /// </para>
    /// </summary>
    public static ApiKey? Find(IReadOnlyList<ApiKey> keys, string? presented)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (string.IsNullOrWhiteSpace(presented))
        {
            return null;
        }

        var candidate = Encoding.UTF8.GetBytes(ComputeHash(presented));
        ApiKey? matched = null;

        // 不提前退出：命中之后继续把剩下的比完，让耗时与"是第几个密钥命中"无关
        foreach (var key in keys)
        {
            var stored = Encoding.UTF8.GetBytes(key.Hash);

            if (stored.Length == candidate.Length
                && CryptographicOperations.FixedTimeEquals(stored, candidate))
            {
                matched = key;
            }
        }

        return matched;
    }
}
