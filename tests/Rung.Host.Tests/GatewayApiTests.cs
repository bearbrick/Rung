using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Rung.Host.Tests;

/// <summary>REST 与 SSE 接口的端到端测试，走真实 HTTP 管道。</summary>
public class GatewayApiTests : IClassFixture<GatewayFixture>
{
    private readonly GatewayFixture _fixture;

    public GatewayApiTests(GatewayFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task 健康检查反映设备连接情况()
    {
        var health = await _fixture.Client.GetFromJsonAsync<HealthView>(
            "/api/health", TestContext.Current.CancellationToken);

        Assert.NotNull(health);
        Assert.Equal("healthy", health.Status);
        Assert.Equal(1, health.DeviceCount);
        Assert.Equal(1, health.ConnectedCount);
        Assert.Equal(4, health.TagCount);
        Assert.Equal(0, health.IssueCount);

        // 启动时刻若用静态只读字段，beforefieldinit 会把它推迟到首次读取，
        // 于是第一次调用时 uptime 恒为 0
        Assert.True(health.UptimeSeconds > 0, $"uptime 应当大于 0，实际 {health.UptimeSeconds}");
    }

    [Fact]
    public async Task 设备列表带出诊断信息()
    {
        var devices = await _fixture.Client.GetFromJsonAsync<List<DeviceView>>(
            "/api/devices", TestContext.Current.CancellationToken);

        var device = Assert.Single(devices!);

        Assert.Equal("oven", device.DeviceId);
        Assert.Equal("Connected", device.State);
        Assert.Equal(240, device.MaxFrameBytes);
        Assert.Equal(4, device.ActiveTagCount);
        Assert.NotNull(device.LastSuccessUtc);
        Assert.Empty(device.Issues);
    }

    [Fact]
    public async Task 未知设备返回404()
    {
        var response = await _fixture.Client.GetAsync(
            "/api/devices/nonexistent", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 点位列表带出值与地址()
    {
        var tags = await _fixture.Client.GetFromJsonAsync<List<TagView>>(
            "/api/tags", TestContext.Current.CancellationToken);

        Assert.Equal(4, tags!.Count);

        var setpoint = Assert.Single(tags, t => t.Name == "Line1.Oven.Setpoint");
        Assert.Equal("Good", setpoint.Quality);
        Assert.Equal("oven", setpoint.DeviceId);
        Assert.Equal("DB1.DBW10", setpoint.Address);

        // 倍率断言用一个没人写的只读点位：拿可写点位断言固定值
        // 会让这个用例依赖执行顺序，Release 下顺序一变就红
        var scaled = Assert.Single(tags, t => t.Name == "Line1.Oven.Scaled");
        Assert.Equal(123.4d, ((JsonElement)scaled.Value!).GetDouble(), precision: 6);
    }

    [Fact]
    public async Task 按前缀过滤点位()
    {
        var all = await _fixture.Client.GetFromJsonAsync<List<TagView>>(
            "/api/tags?prefix=Line1.Oven.Set", TestContext.Current.CancellationToken);

        Assert.Equal("Line1.Oven.Setpoint", Assert.Single(all!).Name);
    }

    [Fact]
    public async Task 按设备过滤点位()
    {
        var mine = await _fixture.Client.GetFromJsonAsync<List<TagView>>(
            "/api/tags?device=oven", TestContext.Current.CancellationToken);
        var others = await _fixture.Client.GetFromJsonAsync<List<TagView>>(
            "/api/tags?device=nobody", TestContext.Current.CancellationToken);

        Assert.Equal(4, mine!.Count);
        Assert.Empty(others!);
    }

    [Fact]
    public async Task 单个点位可以按业务名取()
    {
        var tag = await _fixture.Client.GetFromJsonAsync<TagView>(
            "/api/tags/Line1.Oven.Running", TestContext.Current.CancellationToken);

        Assert.Equal("Bool", tag!.DataType);
        Assert.Equal("Good", tag.Quality);
    }

    [Fact]
    public async Task 未知点位返回404()
    {
        var response = await _fixture.Client.GetAsync(
            "/api/tags/Nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 写入点位落到设备上并回读确认()
    {
        // 工程值 250.0，倍率 0.1，设备上应当是 2500 = 0x09C4
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/tags/Line1.Oven.Setpoint/write", new { value = 250.0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0x09, 0xC4], _fixture.Plc.Peek("DB1.DBW10", 2));

        // 返回的必须是回读到的设备实际值，而不是刚发出去的值。
        // PLC 可能对写入做钳位、取整，或被联锁逻辑改回去
        var view = await response.Content.ReadFromJsonAsync<TagView>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(view);
        Assert.Equal(250d, ((JsonElement)view.Value!).GetDouble());
        Assert.Equal("Good", view.Quality);
    }

    [Fact]
    public async Task 写入后立刻查询就能看到新值()
    {
        // 回读结果同样进缓存，不必等下一个采集周期
        await _fixture.Client.PostAsJsonAsync(
            "/api/tags/Line1.Oven.Setpoint/write", new { value = 271.0 },
            TestContext.Current.CancellationToken);

        var tag = await _fixture.Client.GetFromJsonAsync<TagView>(
            "/api/tags/Line1.Oven.Setpoint", TestContext.Current.CancellationToken);

        Assert.Equal(271d, ((JsonElement)tag!.Value!).GetDouble());
    }

    [Fact]
    public async Task 写只读点位被拒绝()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/tags/Line1.Oven.Count/write", new { value = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("只读", await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 写入类型不匹配时明确报错()
    {
        // 往产线设备上写值，宁可报错也不要猜
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/tags/Line1.Oven.Setpoint/write", new { value = "不是数字" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 写未知点位返回404()
    {
        var response = await _fixture.Client.PostAsJsonAsync(
            "/api/tags/Nope/write", new { value = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SSE推送点位变化()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream/tags");
        using var response = await _fixture.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 模拟器上的计数器每 200 ms 变一次，几秒内一定能收到
        var payload = await ReadFirstDataLineAsync(reader, cancellation.Token);

        Assert.Contains("\"name\"", payload, StringComparison.Ordinal);
        Assert.Contains("Line1.Oven.", payload, StringComparison.Ordinal);
    }

    private static async Task<string> ReadFirstDataLineAsync(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line["data:".Length..].Trim();
            }
        }

        throw new TimeoutException("没有收到任何 SSE 数据行");
    }

    [Fact]
    public async Task 提供Prometheus指标()
    {
        var response = await _fixture.Client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("rung_device_up{device=\"oven\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("rung_tags_total 4", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 提供OpenAPI文档()
    {
        var document = await _fixture.Client.GetStringAsync(
            "/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Contains("/api/tags", document, StringComparison.Ordinal);
        Assert.Contains("/api/stream/tags", document, StringComparison.Ordinal);
    }
}
