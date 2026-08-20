using Xunit;

namespace Rung.Core.Tests;

/// <summary>
/// 写审计。产线上出了事，这个文件是唯一能还原"谁、什么时候、往哪个点位写了什么"的东西，
/// 因此它的每一条性质都值得单独测。
/// </summary>
public class WriteAuditTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"rung-audit-{Guid.NewGuid():N}");

    private static WriteAuditRecord Record(
        string tag = "Line1.Oven.Setpoint",
        string caller = "mes-system",
        bool success = true,
        DateTime? at = null)
        => new(
            at ?? new DateTime(2026, 8, 20, 3, 30, 0, DateTimeKind.Utc),
            caller, "oven", tag, "DB1.DBW20", "Float64", "250", success ? "250" : null,
            success, success ? null : "对端关闭了连接");

    [Fact]
    public async Task 记录能写进去也能读回来()
    {
        using var audit = new JsonLinesWriteAuditLog(_directory);

        await audit.RecordAsync(Record(), TestContext.Current.CancellationToken);

        var back = Assert.Single(await audit.ReadRecentAsync(10, TestContext.Current.CancellationToken));

        Assert.Equal("mes-system", back.Caller);
        Assert.Equal("Line1.Oven.Setpoint", back.TagName);
        Assert.Equal("DB1.DBW20", back.Address);
        Assert.Equal("250", back.Requested);
        Assert.Equal("250", back.Actual);
        Assert.True(back.Success);
    }

    [Fact]
    public async Task 失败的尝试同样留痕()
    {
        // 只记成功的审计，等于把"谁试图动了什么但没成"这一半丢掉了
        using var audit = new JsonLinesWriteAuditLog(_directory);

        await audit.RecordAsync(Record(success: false), TestContext.Current.CancellationToken);

        var back = Assert.Single(await audit.ReadRecentAsync(10, TestContext.Current.CancellationToken));

        Assert.False(back.Success);
        Assert.Equal("对端关闭了连接", back.Error);
        Assert.Null(back.Actual);
    }

    [Fact]
    public async Task 最新的记录排在最前()
    {
        // 查审计几乎总是看最近的
        using var audit = new JsonLinesWriteAuditLog(_directory);

        for (var i = 0; i < 5; i++)
        {
            await audit.RecordAsync(Record(tag: $"Tag{i}"), TestContext.Current.CancellationToken);
        }

        var back = await audit.ReadRecentAsync(3, TestContext.Current.CancellationToken);

        Assert.Equal(["Tag4", "Tag3", "Tag2"], back.Select(static r => r.TagName));
    }

    [Fact]
    public async Task 按天分文件()
    {
        // 查审计的场景永远是"某天某个时间段出了事"，按天切正好对上
        using var audit = new JsonLinesWriteAuditLog(_directory);

        var today = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        await audit.RecordAsync(Record(at: today), TestContext.Current.CancellationToken);
        await audit.RecordAsync(
            Record(at: today.AddDays(-1)), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(audit.FilePathFor(new DateOnly(2026, 8, 20))));
        Assert.True(File.Exists(audit.FilePathFor(new DateOnly(2026, 8, 19))));
    }

    [Fact]
    public async Task 一行是一条记录可以直接grep()
    {
        // 换成 JSON 数组或 XML，"直接 tail 看最近发生了什么"就不可能了
        using var audit = new JsonLinesWriteAuditLog(_directory);

        await audit.RecordAsync(Record(tag: "A"), TestContext.Current.CancellationToken);
        await audit.RecordAsync(Record(tag: "B"), TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(
            audit.FilePathFor(new DateOnly(2026, 8, 20)), TestContext.Current.CancellationToken);

        Assert.Equal(2, lines.Length);
        Assert.All(lines, static line => Assert.StartsWith("{", line, StringComparison.Ordinal));
        Assert.Contains(lines, static line => line.Contains("\"tagName\":\"A\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 半行损坏时跳过它继续读()
    {
        // 进程在写到一半时被杀会留下半行。因为一行坏了就放弃整个文件，
        // 是审计日志最不该有的行为
        using var audit = new JsonLinesWriteAuditLog(_directory);
        await audit.RecordAsync(Record(tag: "Good1"), TestContext.Current.CancellationToken);

        var path = audit.FilePathFor(new DateOnly(2026, 8, 20));
        await File.AppendAllTextAsync(
            path, "{\"timestampUtc\":\"2026-08-20T03:3", TestContext.Current.CancellationToken);

        await audit.RecordAsync(Record(tag: "Good2"), TestContext.Current.CancellationToken);

        var back = await audit.ReadRecentAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(2, back.Count);
        Assert.Contains(back, static r => r.TagName == "Good1");
        Assert.Contains(back, static r => r.TagName == "Good2");
    }

    [Fact]
    public async Task 超过保留期的文件被清理()
    {
        using var audit = new JsonLinesWriteAuditLog(_directory, retentionDays: 7);

        var old = audit.FilePathFor(new DateOnly(2026, 1, 1));
        await File.WriteAllTextAsync(old, "{}\n", TestContext.Current.CancellationToken);

        var recent = new DateTime(2026, 8, 20, 3, 0, 0, DateTimeKind.Utc);
        await audit.RecordAsync(Record(at: recent), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(audit.FilePathFor(new DateOnly(2026, 8, 20))));
    }

    [Fact]
    public async Task 保留天数为零时永久保留()
    {
        using var audit = new JsonLinesWriteAuditLog(_directory, retentionDays: 0);

        var old = audit.FilePathFor(new DateOnly(2020, 1, 1));
        await File.WriteAllTextAsync(old, "{}\n", TestContext.Current.CancellationToken);

        await audit.RecordAsync(Record(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public async Task 并发写入不会互相破坏()
    {
        // 多台设备共享同一个审计日志，各自在自己的工作者里写
        using var audit = new JsonLinesWriteAuditLog(_directory);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            audit.RecordAsync(Record(tag: $"Tag{i}"), TestContext.Current.CancellationToken).AsTask()));

        var back = await audit.ReadRecentAsync(100, TestContext.Current.CancellationToken);

        Assert.Equal(50, back.Count);
        Assert.Equal(50, back.Select(static r => r.TagName).Distinct().Count());
    }

    [Fact]
    public async Task 未配置时什么也不记()
    {
        await NullWriteAuditLog.Instance.RecordAsync(
            Record(), TestContext.Current.CancellationToken);

        Assert.Empty(await NullWriteAuditLog.Instance.ReadRecentAsync(
            10, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
