using Xunit;

namespace Rung.Protocols.S7.Tests;

/// <summary>
/// 针对夹具本身的元测试。夹具是所有协议断言的地基，
/// 它自己出错会让一整批测试给出错误的安全感。
/// </summary>
public class FixtureIntegrityTests
{
    private static readonly string[] AllowedSources = ["spec", "capture"];

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in HexFixture.EnumerateNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void 夹具的TPKT声明长度与实际字节数一致(string name)
    {
        var frame = HexFixture.Load(name);

        Assert.Equal(frame.Length, S7ResponseReader.ReadFrameLength(frame));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void 夹具都标明了来源(string name)
    {
        var source = HexFixture.ReadMetadata(name, "source");

        // spec = 按规范推导，只能防回归；capture = 真机抓包，才是协议正确性的依据。
        // 换成真机夹具时把这里的断言改成只允许 capture，就能强制完成迁移
        Assert.Contains(source, AllowedSources, StringComparer.Ordinal);
    }

    [Fact]
    public void 夹具目录不为空()
    {
        // 防止 csproj 的 Content 拷贝规则被改坏后，测试静默地全部跳过
        Assert.NotEmpty(HexFixture.EnumerateNames());
    }
}
