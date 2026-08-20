using Rung.Abstractions;
using Xunit;

namespace Rung.Abstractions.Tests;

/// <summary>
/// API 密钥。这是唯一挡在"任何人都能往 PLC 写值"前面的东西，
/// 所以它的每一条性质都值得单独测。
/// </summary>
public class ApiKeysTests
{
    [Fact]
    public void 生成的密钥有足够的熵()
    {
        // 256 位随机，暴力穷举不可行
        var keys = Enumerable.Range(0, 200).Select(static _ => ApiKeys.Generate()).ToHashSet();

        Assert.Equal(200, keys.Count);
        Assert.All(keys, static key => Assert.StartsWith(ApiKeys.Prefix, key, StringComparison.Ordinal));
        Assert.All(keys, static key => Assert.True(key.Length > 40, $"密钥过短：{key}"));
    }

    [Fact]
    public void 密钥可以安全地放进URL和请求头()
    {
        // Base64 的 + / = 在 URL 和某些头解析里会出问题
        var key = ApiKeys.Generate();

        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('=', key);
    }

    [Fact]
    public void 哈希是确定的且不可逆推出明文()
    {
        var plaintext = ApiKeys.Generate();
        var hash = ApiKeys.ComputeHash(plaintext);

        Assert.Equal(hash, ApiKeys.ComputeHash(plaintext));
        Assert.DoesNotContain(plaintext, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void 创建时返回明文而记录里只有哈希()
    {
        // 明文只在这一刻存在。配置库会被备份、会被拷去排障，
        // 明文躺在里面就等于没有密钥
        var (key, plaintext) = ApiKeys.Create("mes", canWrite: true);

        Assert.Equal("mes", key.Name);
        Assert.True(key.CanWrite);
        Assert.Equal(ApiKeys.ComputeHash(plaintext), key.Hash);
        Assert.NotEqual(plaintext, key.Hash);
    }

    [Fact]
    public void 正确的密钥能被找到()
    {
        var (readOnly, readOnlyText) = ApiKeys.Create("dashboard", canWrite: false);
        var (writer, writerText) = ApiKeys.Create("mes", canWrite: true);
        ApiKey[] keys = [readOnly, writer];

        Assert.Equal("dashboard", ApiKeys.Find(keys, readOnlyText)!.Name);
        Assert.Equal("mes", ApiKeys.Find(keys, writerText)!.Name);
        Assert.True(ApiKeys.Find(keys, writerText)!.CanWrite);
        Assert.False(ApiKeys.Find(keys, readOnlyText)!.CanWrite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rung_wrong")]
    [InlineData(null)]
    public void 错误的密钥找不到(string? presented)
    {
        var (key, _) = ApiKeys.Create("mes", canWrite: true);

        Assert.Null(ApiKeys.Find([key], presented));
    }

    [Fact]
    public void 空密钥列表下任何输入都找不到()
    {
        // 一个密钥都没配时写接口应当整个关闭，而不是放开
        Assert.Null(ApiKeys.Find([], ApiKeys.Generate()));
    }

    [Fact]
    public void 明文差一个字符就匹配不上()
    {
        var (key, plaintext) = ApiKeys.Create("mes", canWrite: true);
        var tampered = plaintext[..^1] + (plaintext[^1] == 'A' ? 'B' : 'A');

        Assert.Null(ApiKeys.Find([key], tampered));
    }
}
