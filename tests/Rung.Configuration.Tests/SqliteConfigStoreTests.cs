using Rung.Abstractions;
using Rung.Configuration.Storage;
using Xunit;

namespace Rung.Configuration.Tests;

public class SqliteConfigStoreTests
{
    private static RungConfig Sample(string deviceId = "oven", params string[] tagNames) => new()
    {
        PollIntervalMs = 750,
        Redis = new RedisConfig { KeyPrefix = "factory-a" },
        Devices =
        [
            new DeviceConfig
            {
                DeviceId = deviceId,
                Protocol = "s7",
                Host = "192.168.0.10",
                Port = 102,
                Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rack"] = "0",
                    ["slot"] = "1",
                },
                Tags = [.. (tagNames.Length > 0 ? tagNames : ["Line1.Temp"]).Select(name => new TagConfig
                {
                    Name = name,
                    Address = "DB1.DBW0",
                    DataType = TagDataType.Int16,
                    Scale = 0.1,
                    Deadband = 0.5,
                    PollGroup = "slow",
                    Access = TagAccess.ReadWrite,
                    Description = "炉温",
                })],
            },
        ],
    };

    [Fact]
    public async Task 首次打开自动建表()
    {
        // 让用户手工跑迁移，等于在制造"升级后起不来"的现场事故
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        var config = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(config.Devices!);
        Assert.True(File.Exists(file.Path));
    }

    [Fact]
    public async Task 导入后读回字段完全保真()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample(), replace: true, TestContext.Current.CancellationToken);
        var back = await store.LoadAsync(TestContext.Current.CancellationToken);

        var device = Assert.Single(back.ResolveDevices());
        Assert.Equal("oven", device.DeviceId);
        Assert.Equal("192.168.0.10", device.Host);
        Assert.Equal("0", device.Extra!["rack"]);

        var tag = Assert.Single(device.Tags!);
        Assert.Equal("Line1.Temp", tag.Name);
        Assert.Equal(TagDataType.Int16, tag.DataType);
        Assert.Equal(0.1, tag.Scale);
        Assert.Equal(0.5, tag.Deadband);
        Assert.Equal("slow", tag.PollGroup);
        Assert.Equal(TagAccess.ReadWrite, tag.Access);
        Assert.Equal("炉温", tag.Description);
    }

    [Fact]
    public async Task 全局设置一并保存()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample(), replace: true, TestContext.Current.CancellationToken);
        var back = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(750, back.PollIntervalMs);
        Assert.Equal("factory-a", back.Redis!.KeyPrefix);
    }

    [Fact]
    public async Task 默认整表替换()
    {
        // 点位表是一份份交付的，默认合并会让"这次交付改了什么"说不清
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample("a"), replace: true, TestContext.Current.CancellationToken);
        await store.ImportAsync(Sample("b"), replace: true, TestContext.Current.CancellationToken);

        var device = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken))
            .ResolveDevices());
        Assert.Equal("b", device.DeviceId);
    }

    [Fact]
    public async Task 合并模式保留其他设备()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample("a", "A.Tag"), replace: true, TestContext.Current.CancellationToken);
        var result = await store.ImportAsync(
            Sample("b", "B.Tag"), replace: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DevicesAdded);
        Assert.Equal(2, (await store.LoadAsync(TestContext.Current.CancellationToken))
            .ResolveDevices().Count);
    }

    [Fact]
    public async Task 同名设备按整体替换而不是叠加点位()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample("a", "Old.Tag"), replace: true, TestContext.Current.CancellationToken);
        var result = await store.ImportAsync(
            Sample("a", "New.Tag"), replace: false, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DevicesUpdated);
        Assert.Equal(1, result.TotalTags);

        var device = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken))
            .ResolveDevices());
        Assert.Equal("New.Tag", Assert.Single(device.Tags!).Name);
    }

    [Fact]
    public async Task 点位名全局唯一由数据库约束保证()
    {
        // 重名会让写命令路由到错误的设备上。在数据库层面拦，比只靠启动检查更早更硬
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample("a", "Shared"), replace: true, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(async () => await store.ImportAsync(
            Sample("b", "Shared"), replace: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 停用的设备不参与采集()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);
        await store.ImportAsync(Sample("a"), replace: true, TestContext.Current.CancellationToken);

        await using (var context = RungDbContext.Open(file.Path))
        {
            var device = context.Devices.First();
            device.Enabled = false;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty((await store.LoadAsync(TestContext.Current.CancellationToken)).ResolveDevices());
    }

    [Fact]
    public async Task 额外参数的JSON被改坏时不至于起不来()
    {
        // 后果是设备用默认机架槽号连不上，日志里看得见；
        // 比整个网关拒绝启动强得多
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);
        await store.ImportAsync(Sample(), replace: true, TestContext.Current.CancellationToken);

        await using (var context = RungDbContext.Open(file.Path))
        {
            context.Devices.First().ExtraJson = "{ 这不是 JSON";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var device = Assert.Single((await store.LoadAsync(TestContext.Current.CancellationToken))
            .ResolveDevices());
        Assert.Empty(device.Extra!);
    }

    [Fact]
    public async Task 从Excel导入时不覆盖全局设置()
    {
        // Excel 只承载设备和点位。照单全收会把采集组周期、重连参数、
        // Redis / MQTT 配置静默清空——表现为"改了个点位名，结果 Redis 输出没了"，
        // 是最难联想到原因的一类事故
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample(), replace: true, TestContext.Current.CancellationToken);

        // 模拟从 Excel 读出来的配置：只有设备，没有任何全局设置
        var fromExcel = new RungConfig { Devices = Sample().Devices };
        await store.ImportAsync(
            fromExcel, replace: true, TestContext.Current.CancellationToken,
            includeGlobalSettings: false);

        var back = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(750, back.PollIntervalMs);
        Assert.Equal("factory-a", back.Redis!.KeyPrefix);
    }

    [Fact]
    public async Task 从JSON导入时全局设置照常覆盖()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);

        await store.ImportAsync(Sample(), replace: true, TestContext.Current.CancellationToken);
        await store.ImportAsync(
            Sample() with { PollIntervalMs = 250, Redis = null },
            replace: true, TestContext.Current.CancellationToken);

        var back = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(250, back.PollIntervalMs);
        Assert.Null(back.Redis);
    }

    [Fact]
    public void 空设备列表是合法状态而不是错误()
    {
        // 刚建好还没导入任何设备的数据库就是这样，抛异常会让用户
        // 撞上一句完全误导的"既没有 devices 也没有 device"
        Assert.Empty(new RungConfig { Devices = [] }.ResolveDevices());
    }

    [Fact]
    public void 两种写法都缺席时才算配置有问题()
    {
        var ex = Assert.Throws<InvalidDataException>(() => new RungConfig().ResolveDevices());

        Assert.Contains("rung config import", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 列出设备带上点位数()
    {
        using var file = new TempFile(".db");
        var store = new SqliteConfigStore(file.Path);
        await store.ImportAsync(
            Sample("oven", "T1", "T2", "T3"), replace: true, TestContext.Current.CancellationToken);

        var row = Assert.Single(await store.ListDevicesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("oven", row.DeviceId);
        Assert.Equal("s7", row.Protocol);
        Assert.Equal(3, row.TagCount);
        Assert.True(row.Enabled);
    }
}
