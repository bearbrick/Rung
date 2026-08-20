using Rung.Abstractions;
using Rung.Cli;
using Rung.Configuration;
using Xunit;

namespace Rung.Drivers.S7.Tests;

/// <summary>
/// 离线配置校验。这些检查全是纯逻辑，没有理由等到现场连上 PLC 才发现，
/// 因此它们必须自己足够可靠。
/// </summary>
public class ConfigCheckerTests
{
    private static TagConfig Tag(string name, string address, TagDataType type = TagDataType.Int16)
        => new() { Name = name, Address = address, DataType = type };

    private static RungConfig Config(params DeviceConfig[] devices) => new() { Devices = devices };

    private static DeviceConfig Device(string id, string protocol, params TagConfig[] tags) => new()
    {
        DeviceId = id,
        Protocol = protocol,
        Host = "127.0.0.1",
        Tags = tags,
    };

    [Fact]
    public void 正常配置没有问题并给出请求次数()
    {
        var config = Config(Device("oven", "s7",
            Tag("A", "DB1.DBW0"), Tag("B", "DB1.DBW2"), Tag("C", "DB1.DBW4")));

        var result = Assert.Single(ConfigChecker.Check(config));

        Assert.Empty(result.Issues);
        Assert.Equal(3, result.TagCount);
        Assert.Equal(1, result.RequestCount);   // 连续地址合并成一次
        Assert.Equal(6, result.FetchedBytes);
    }

    [Fact]
    public void 地址写错被离线发现()
    {
        var config = Config(Device("oven", "s7", Tag("bad", "DB0.DBW0")));

        var issue = Assert.Single(Assert.Single(ConfigChecker.Check(config)).Issues);

        Assert.Equal("bad", issue.TagName);
        Assert.Contains("不能为 0", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 类型与地址宽度不符被离线发现()
    {
        // 这个错在现场表现为"读回一个乱码"，且因为长度对得上很难想到是配置问题
        var config = Config(Device("oven", "s7", Tag("bad", "DB1.DBW4", TagDataType.Float32)));

        var issue = Assert.Single(Assert.Single(ConfigChecker.Check(config)).Issues);

        Assert.Contains("2 字节", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("4 字节", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Modbus地址同样被校验()
    {
        var config = Config(Device("meter", "modbus-tcp",
            Tag("ok", "HR0"), Tag("bad", "CO0", TagDataType.Int16)));

        var result = Assert.Single(ConfigChecker.Check(config));

        Assert.Contains("只能配 Bool", Assert.Single(result.Issues).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 未知协议被拦下并列出可用值()
    {
        var config = Config(Device("x", "profinet", Tag("A", "DB1.DBW0")));

        var issue = Assert.Single(Assert.Single(ConfigChecker.Check(config)).Issues);

        Assert.Contains("profinet", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("modbus-tcp", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 跨设备的点位重名被发现()
    {
        // 单设备内的重名各自的编译器会管，跨设备的只有摊在一起才看得出来。
        // 重名会让写命令落到错误的设备上，是代价最大的一类配置错误
        var config = Config(
            Device("a", "s7", Tag("Shared", "DB1.DBW0")),
            Device("b", "s7", Tag("Shared", "DB1.DBW2")));

        var duplicate = Assert.Single(ConfigChecker.FindDuplicateTagNames(config));

        Assert.Contains("Shared", duplicate, StringComparison.Ordinal);
        Assert.Contains("a", duplicate, StringComparison.Ordinal);
        Assert.Contains("b", duplicate, StringComparison.Ordinal);
    }

    [Fact]
    public void 停用的点位不参与重名判定()
    {
        var config = Config(
            Device("a", "s7", Tag("Shared", "DB1.DBW0")),
            Device("b", "s7", Tag("Shared", "DB1.DBW2") with { Enabled = false }));

        Assert.Empty(ConfigChecker.FindDuplicateTagNames(config));
    }

    [Fact]
    public void 请求次数按最保守的PDU估算()
    {
        // 真机协商出来只会更大，因此这个数字是上界，不会给人过于乐观的印象。
        // 200 个 Int16 跨度 400 字节，PDU 240 下单次最多读 222 字节
        var tags = Enumerable.Range(0, 200)
            .Select(i => Tag($"t{i}", $"DB1.DBW{i * 2}"))
            .ToArray();

        var result = Assert.Single(ConfigChecker.Check(Config(Device("oven", "s7", tags))));

        Assert.Equal(2, result.RequestCount);
        Assert.Empty(result.Issues);
    }
}
