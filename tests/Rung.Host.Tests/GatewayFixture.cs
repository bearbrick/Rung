using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Rung.Abstractions;
using Rung.Simulator;
using Xunit;

namespace Rung.Host.Tests;

/// <summary>
/// 一台模拟 PLC + 一个真实运行的 Rung.Host。
/// 整个 Web 层跑在真实的 Kestrel 管道上，测的不是"方法能不能调通"，
/// 而是"HTTP 客户端能不能拿到预期的东西"。
/// </summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    private string _configPath = string.Empty;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>模拟设备，可用来注入故障或核对写入结果。</summary>
    public S7SimulatorServer Plc { get; private set; } = null!;

    /// <summary>
    /// 指向宿主的 HTTP 客户端，已带上可写密钥。
    /// <para>
    /// 夹具里配一把真密钥而不是关掉认证：这样写路径的每个用例
    /// 都顺带验证了认证链路是通的，而不是绕过它。
    /// </para>
    /// </summary>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>不带任何密钥的客户端，用来验证拒绝路径。</summary>
    public HttpClient Anonymous { get; private set; } = null!;

    /// <summary>夹具使用的可写密钥明文。</summary>
    public string WriteKey { get; } = ApiKeys.Generate();

    public async ValueTask InitializeAsync()
    {
        Plc = new S7SimulatorServer(new SimulatedDeviceConfig
        {
            Name = "oven",
            Port = 0,
            NegotiatedPduLength = 240,
            Signals =
            [
                new SignalConfig
                {
                    Address = "DB1.DBW0", Type = "Int16",
                    Generator = "counter", Value = 100, Step = 1, PeriodSeconds = 0.2,
                },
                new SignalConfig { Address = "DB1.DBX4.0", Type = "Bool", Generator = "toggle", PeriodSeconds = 1 },
                new SignalConfig { Address = "DB1.DBW10", Type = "Int16", Generator = "constant", Value = 2400 },
            ],
        });

        _configPath = Path.Combine(Path.GetTempPath(), $"rung-host-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_configPath, string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "version": 1,
              "pollIntervalMs": 100,
              "auth": {
                "keys": [
                  { "name": "test-writer", "hash": "{{ApiKeys.ComputeHash(WriteKey)}}", "canWrite": true }
                ]
              },
              "devices": [
                {
                  "deviceId": "oven",
                  "protocol": "s7",
                  "host": "127.0.0.1",
                  "port": {{Plc.Port}},
                  "extra": { "rack": "0", "slot": "1" },
                  "tags": [
                    { "name": "Line1.Oven.Count", "address": "DB1.DBW0", "dataType": "Int16" },
                    { "name": "Line1.Oven.Running", "address": "DB1.DBX4.0", "dataType": "Bool" },
                    { "name": "Line1.Oven.Setpoint", "address": "DB1.DBW10", "dataType": "Int16",
                      "scale": 0.1, "access": "ReadWrite" }
                  ]
                }
              ]
            }
            """));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("ConfigPath", _configPath));

        Anonymous = _factory.CreateClient();

        Client = _factory.CreateClient();
        Client.DefaultRequestHeaders.Add("X-Rung-Key", WriteKey);

        // 等第一轮采集落进缓存，之后所有断言才有意义
        await WaitForFirstPollAsync();
    }

    private async Task WaitForFirstPollAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var health = await Client.GetFromJsonAsync<HealthView>("/api/health");
            if (health is { TagCount: > 0, ConnectedCount: > 0 })
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("宿主在 5 秒内没有完成首轮采集");
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        Anonymous?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Plc.DisposeAsync();

        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }
    }
}
