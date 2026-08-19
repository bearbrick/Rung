using MiniExcelLibs;
using Rung.Abstractions;
using Rung.Configuration.Storage;
using Xunit;

namespace Rung.Configuration.Tests;

/// <summary>
/// Excel 导入导出的测试。
/// <para>
/// 这些表是电气工程师手工维护的，一定会有脏数据。所以重点不在"格式正确时能不能读"，
/// 而在"格式不对时报得清不清楚"——报一句"格式错误"而不指出哪一行，等于没报。
/// </para>
/// </summary>
public class TagExcelTests
{
    private static RungConfig Sample() => new()
    {
        Devices =
        [
            new DeviceConfig
            {
                DeviceId = "oven",
                Protocol = "s7",
                Host = "192.168.0.10",
                Port = 102,
                TimeoutMs = 2500,
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rack"] = "0",
                    ["slot"] = "1",
                },
                Tags =
                [
                    new TagConfig
                    {
                        Name = "Line1.Oven.Temp",
                        Address = "DB1.DBW0",
                        DataType = TagDataType.Int16,
                        Scale = 0.1,
                        Offset = -40,
                        Deadband = 0.5,
                        ByteOrder = ByteOrder.CDAB,
                        Access = TagAccess.ReadWrite,
                        PollGroup = "slow",
                        Description = "炉温 ℃",
                    },
                    new TagConfig
                    {
                        Name = "Line1.Oven.Off",
                        Address = "DB1.DBX0.1",
                        DataType = TagDataType.Bool,
                        Enabled = false,
                    },
                ],
            },
        ],
    };

    /// <summary>直接拼一张 Excel，用来构造现场那种手工维护出来的脏数据。</summary>
    private static void WriteSheets(
        string path,
        IEnumerable<Dictionary<string, object?>> devices,
        IEnumerable<Dictionary<string, object?>> tags)
        => MiniExcel.SaveAs(path, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [TagExcel.DeviceSheet] = devices.ToList(),
            [TagExcel.TagSheet] = tags.ToList(),
        }, overwriteFile: true);

    private static Dictionary<string, object?> Device(string id) => new()
    {
        ["设备ID"] = id, ["协议"] = "s7", ["地址"] = "127.0.0.1", ["端口"] = 102,
        ["额外参数"] = "rack=0;slot=1",
    };

    private static Dictionary<string, object?> Tag(
        string name, string address = "DB1.DBW0", string type = "Int16", string device = "oven") => new()
    {
        ["设备ID"] = device, ["点位名"] = name, ["地址"] = address, ["数据类型"] = type,
    };

    [Fact]
    public async Task 导出再导入字段完全保真()
    {
        using var file = new TempFile(".xlsx");

        await TagExcel.ExportAsync(file.Path, Sample(), TestContext.Current.CancellationToken);
        var back = TagExcel.Import(file.Path, out var issues);

        Assert.Empty(issues);

        var device = Assert.Single(back.ResolveDevices());
        Assert.Equal("192.168.0.10", device.Host);
        Assert.Equal(2500, device.TimeoutMs);
        Assert.Equal("1", device.Extra!["slot"]);

        var temp = device.Tags!.First(static t => t.Name == "Line1.Oven.Temp");
        Assert.Equal(0.1, temp.Scale);
        Assert.Equal(-40, temp.Offset);
        Assert.Equal(0.5, temp.Deadband);
        Assert.Equal(ByteOrder.CDAB, temp.ByteOrder);
        Assert.Equal(TagAccess.ReadWrite, temp.Access);
        Assert.Equal("slow", temp.PollGroup);
        Assert.Equal("炉温 ℃", temp.Description);

        // 停用状态也要带回来，否则导出再导入会把停用的点位悄悄启用
        Assert.False(device.Tags!.First(static t => t.Name == "Line1.Oven.Off").Enabled);
    }

    [Fact]
    public void 数据类型写错时指出行号和可用取值()
    {
        using var file = new TempFile(".xlsx");
        WriteSheets(file.Path, [Device("oven")],
            [Tag("A"), Tag("B", type: "Fl0at32"), Tag("C")]);

        var config = TagExcel.Import(file.Path, out var issues);

        var issue = Assert.Single(issues);
        Assert.Equal(TagExcel.TagSheet, issue.Sheet);
        Assert.Equal(3, issue.Row); // 表头占第 1 行，B 在第 3 行
        Assert.Contains("Fl0at32", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("Float32", issue.Reason, StringComparison.Ordinal);

        // 坏行被跳过，其余照常导入——一张几百行的表里错两行，
        // 让人改完重来一遍不如先把对的导进去
        Assert.Equal(2, Assert.Single(config.ResolveDevices()).Tags!.Count);
    }

    [Fact]
    public void 点位指向未定义的设备时给出可照做的提示()
    {
        using var file = new TempFile(".xlsx");
        WriteSheets(file.Path, [Device("oven")], [Tag("A", device: "typo")]);

        TagExcel.Import(file.Path, out var issues);

        var issue = Assert.Single(issues);
        Assert.Contains("typo", issue.Reason, StringComparison.Ordinal);
        Assert.Contains("设备", issue.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void 点位名重复被拦下()
    {
        // 重名会让写命令路由到错误的设备上
        using var file = new TempFile(".xlsx");
        WriteSheets(file.Path, [Device("oven")], [Tag("Same"), Tag("Same", address: "DB1.DBW2")]);

        var config = TagExcel.Import(file.Path, out var issues);

        Assert.Contains("全局唯一", Assert.Single(issues).Reason, StringComparison.Ordinal);
        Assert.Single(Assert.Single(config.ResolveDevices()).Tags!);
    }

    [Fact]
    public void 缺少地址被拦下()
    {
        using var file = new TempFile(".xlsx");
        WriteSheets(file.Path, [Device("oven")], [Tag("A", address: "")]);

        Assert.Contains("缺少地址", Assert.Single(TagExcelIssues(file.Path)).Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void 空行被静默跳过()
    {
        // Excel 里空行非常常见，为此报错只会淹掉真正的问题
        using var file = new TempFile(".xlsx");
        // 真实的 Excel 空行是"有列无值"，不是没有列
        var blankDevice = Device("oven").ToDictionary(static kv => kv.Key, static _ => (object?)null);
        var blankTag = Tag("x").ToDictionary(static kv => kv.Key, static _ => (object?)null);

        WriteSheets(file.Path,
            [Device("oven"), blankDevice],
            [Tag("A"), blankTag, Tag("B", address: "DB1.DBW2")]);

        var config = TagExcel.Import(file.Path, out var issues);

        Assert.Empty(issues);
        Assert.Equal(2, Assert.Single(config.ResolveDevices()).Tags!.Count);
    }

    [Fact]
    public void 字节序与读写权限写错时退回默认值()
    {
        // 这两个填错的后果比数据类型轻得多，退回默认值比拒绝整行更实用
        using var file = new TempFile(".xlsx");
        var tag = Tag("A");
        tag["字节序"] = "ABDC";
        tag["读写"] = "读写";
        WriteSheets(file.Path, [Device("oven")], [tag]);

        var config = TagExcel.Import(file.Path, out _);
        var imported = Assert.Single(Assert.Single(config.ResolveDevices()).Tags!);

        Assert.Equal(ByteOrder.ABCD, imported.ByteOrder);
        Assert.Equal(TagAccess.Read, imported.Access);
    }

    [Fact]
    public void 启用列写否即为停用()
    {
        using var file = new TempFile(".xlsx");
        var tag = Tag("A");
        tag["启用"] = "否";
        WriteSheets(file.Path, [Device("oven")], [tag]);

        var config = TagExcel.Import(file.Path, out _);

        Assert.False(Assert.Single(Assert.Single(config.ResolveDevices()).Tags!).Enabled);
    }

    [Fact]
    public void 设备缺少地址被拦下()
    {
        using var file = new TempFile(".xlsx");
        var device = Device("oven");
        device["地址"] = "";
        WriteSheets(file.Path, [device], []);

        var issue = Assert.Single(TagExcelIssues(file.Path));

        Assert.Equal(TagExcel.DeviceSheet, issue.Sheet);
        Assert.Contains("缺少地址", issue.Reason, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ExcelIssue> TagExcelIssues(string path)
    {
        TagExcel.Import(path, out var issues);
        return issues;
    }
}
