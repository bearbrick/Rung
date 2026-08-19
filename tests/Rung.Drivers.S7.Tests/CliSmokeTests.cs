using System.Globalization;
using Rung.Cli;
using Xunit;

namespace Rung.Drivers.S7.Tests;

/// <summary>
/// 整个 MVP 的冒烟测试：配置文件 → 连接 → 编译计划 → 采集 → 打印。
/// 走的是和真机完全一样的代码路径，只是对端换成了进程内的假设备。
/// </summary>
public class CliSmokeTests : IDisposable
{
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"rung-{Guid.NewGuid():N}.json");

    private string WriteConfig(int port) => WriteConfigCore(port, """
            {
              "name": "Line1.Oven3.Temp",
              "address": "DB1.DBW0",
              "dataType": "Int16",
              "scale": 0.1
            },
            {
              "name": "Line1.Oven3.Running",
              "address": "DB1.DBX4.2",
              "dataType": "Bool"
            }
        """);

    private string WriteConfigCore(int port, string tagsJson)
    {
        File.WriteAllText(_configPath, string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "version": 1,
              "device": {
                "deviceId": "fake-plc",
                "protocol": "s7",
                "host": "127.0.0.1",
                "port": {{port}},
                "timeoutMs": 3000,
                "extra": { "rack": "0", "slot": "1" }
              },
              "pollIntervalMs": 200,
              "tags": [
            {{tagsJson}}
              ]
            }
            """));

        return _configPath;
    }

    [Fact]
    public async Task 从配置文件到打印出值的完整链路()
    {
        await using var server = new FakeS7Server();
        server.Poke(0x84, 1, 0, 0x09, 0x2E);       // 2350，倍率 0.1 后是 235
        server.Poke(0x84, 1, 4, 0b0000_0100);      // 第 2 位置起

        var output = new StringWriter();

        var exitCode = await Program.RunAsync(
            [WriteConfig(server.Port), "--once"], output, TestContext.Current.CancellationToken);

        var text = output.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("已连接，协商 PDU 长度 240 字节", text, StringComparison.Ordinal);
        Assert.Contains("2/2 个点位 → 每轮 1 次请求", text, StringComparison.Ordinal);
        Assert.Contains("235", text, StringComparison.Ordinal);
        Assert.Contains("true", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 配置里的坏点位被显式报出来而不是静默跳过()
    {
        await using var server = new FakeS7Server();
        var output = new StringWriter();

        var configPath = WriteConfigCore(server.Port, """
                {
                  "name": "good",
                  "address": "DB1.DBW0",
                  "dataType": "Int16"
                },
                {
                  "name": "typo",
                  "address": "DB1.DBW10",
                  "dataType": "Float32"
                }
            """);

        var exitCode = await Program.RunAsync(
            [configPath, "--once"], output, TestContext.Current.CancellationToken);

        var text = output.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("! 配置问题 typo", text, StringComparison.Ordinal);
        Assert.Contains("1/2 个点位", text, StringComparison.Ordinal);
        Assert.Contains("ConfigError", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 连不上时在有界时间内退出并说明原因()
    {
        // 作为常驻服务，连不上就无限重连是对的；但首次启动必须有超时，
        // 否则配置写错的表现是"进程静静挂着什么也不说"——最糟糕的失败方式
        var output = new StringWriter();

        var configPath = WriteConfigCore(1, """
                { "name": "t", "address": "DB1.DBW0", "dataType": "Int16" }
            """);

        var exitCode = await Program.RunAsync(
            [configPath, "--once", "--timeout", "2"], output, TestContext.Current.CancellationToken);

        var text = output.ToString();

        Assert.Equal(1, exitCode);
        Assert.Contains("未能完成首次采集", text, StringComparison.Ordinal);
        Assert.Contains("最后一次错误", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缺少配置文件时给出提示()
    {
        var output = new StringWriter();

        var exitCode = await Program.RunAsync(
            ["/nonexistent/rung.json"], output, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("找不到配置文件", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 无参数时打印用法()
    {
        var output = new StringWriter();

        var exitCode = await Program.RunAsync([], output, TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("用法：", output.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        GC.SuppressFinalize(this);
    }
}
