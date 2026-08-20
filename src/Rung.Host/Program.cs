using Rung.Abstractions;
using Rung.Configuration;
using Rung.Configuration.Storage;
using Rung.Core;
using Rung.Drivers.Modbus;
using Rung.Drivers.S7;
using Rung.Host;
using Rung.Host.Endpoints;
using Rung.Sinks.Mqtt;
using Rung.Sinks.Redis;

// 容器健康检查探针。aspnet 基础镜像里没有 curl/wget，
// 与其为了探活多装一个包，不如让程序自己探自己
if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    return await HealthProbe.RunAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

// 端口挑一个冷门的四位数：5000 是 Kestrel 默认、8080 到处都是、9090 归 Prometheus。
// 部署时端口撞车是最烦人的小事，写死在这里省掉一轮沟通
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5580");

builder.Services.AddOpenApi(options => options.AddSchemaTransformer<NumericSchemaTransformer>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(GatewayStartupTime.Now());

// 配置可以来自 SQLite（--Db）或 JSON 文件（--ConfigPath）。
// SQLite 优先：点位多、要在界面上改的场景走它
var databasePath = builder.Configuration["Db"];
if (!string.IsNullOrWhiteSpace(databasePath))
{
    var sqliteStore = new SqliteConfigStore(databasePath);
    var sqliteConfig = await sqliteStore.LoadAsync(CancellationToken.None);

    return await RunGatewayAsync(builder, sqliteConfig, sqliteStore);
}

// 相对路径按内容根解析，而不是按当前工作目录——dotnet run 时这两者经常不是一回事。
// 报错时把解析后的绝对路径打出来，省掉一轮"我明明放那儿了"的来回
var configPath = builder.Configuration["ConfigPath"] ?? "rung.json";
var resolvedConfigPath = Path.IsPathRooted(configPath)
    ? configPath
    : Path.GetFullPath(configPath, builder.Environment.ContentRootPath);

if (!File.Exists(resolvedConfigPath))
{
    Console.Error.WriteLine($"找不到采集配置：{resolvedConfigPath}");
    Console.Error.WriteLine($"（配置项 ConfigPath = \"{configPath}\"，内容根 = {builder.Environment.ContentRootPath}）");
    Console.Error.WriteLine("用 --ConfigPath <绝对路径或相对内容根的路径> 指定。");
    return 1;
}

return await RunGatewayAsync(
    builder, RungConfig.Load(resolvedConfigPath), new JsonConfigStore(resolvedConfigPath));

async Task<int> RunGatewayAsync(WebApplicationBuilder builder, RungConfig rungConfig, IConfigStore store)
{
builder.Services.AddSingleton(rungConfig);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<TagCache>();
builder.Services.AddSingleton<TagChangeBroadcaster>();
builder.Services.AddSingleton<IDeviceDriverFactory, S7DriverFactory>();
builder.Services.AddSingleton<IDeviceDriverFactory, ModbusDriverFactory>();

// Redis 输出是可选的：没配就整个不注册，下游用 GetService 取到 null 即可。
// 连不上也不影响采集——abortConnect=false 让它自己在后台重连
if (rungConfig.Redis is { Enabled: true } redisConfig)
{
    builder.Services.AddSingleton(provider => RedisTagSink.ConnectAsync(
        new RedisSinkOptions
        {
            ConnectionString = redisConfig.ConnectionString,
            KeyPrefix = redisConfig.KeyPrefix,
            Database = redisConfig.Database,
            PublishChanges = redisConfig.PublishChanges,
            ChannelName = redisConfig.ChannelName,
        },
        provider.GetRequiredService<ILogger<RedisTagSink>>()).GetAwaiter().GetResult());
}

builder.Services.AddSingleton(provider =>
{
    var cache = provider.GetRequiredService<TagCache>();

    var sinks = new List<ITagSink> { provider.GetRequiredService<TagChangeBroadcaster>() };
    if (provider.GetService<RedisTagSink>() is { } redis)
    {
        sinks.Add(redis);
    }

    if (provider.GetService<MqttTagSink>() is { } mqtt)
    {
        sinks.Add(mqtt);
    }

    var gateway = new GatewayHost(
        provider.GetServices<IDeviceDriverFactory>(),
        cache,
        sinks,
        provider.GetRequiredService<ILoggerFactory>());

    foreach (var registration in GatewayEndpoints.ToRegistrations(rungConfig))
    {
        gateway.AddDevice(registration.Options, registration.Tags, registration.WorkerOptions);
    }

    return gateway;
});

// MQTT 与 Redis 互不影响，可以同时开
if (rungConfig.Mqtt is { Enabled: true } mqttConfig)
{
    builder.Services.AddSingleton(provider => MqttTagSink.ConnectAsync(
        new MqttSinkOptions
        {
            Host = mqttConfig.Host,
            Port = mqttConfig.Port,
            ClientId = mqttConfig.ClientId,
            Username = mqttConfig.Username,
            Password = mqttConfig.Password,
            TopicPrefix = mqttConfig.TopicPrefix,
            TagQos = mqttConfig.TagQos,
            RetainTags = mqttConfig.RetainTags,
        },
        provider.GetRequiredService<ILogger<MqttTagSink>>()).GetAwaiter().GetResult());
}

builder.Services.AddHostedService<GatewayService>();

var app = builder.Build();

app.MapOpenApi();
app.MapGatewayEndpoints();

// Web UI 的落脚点。现在是空的，前端构建产物会输出到这里
app.UseDefaultFiles();
app.UseStaticFiles();

HostLog.ConfigSource(app.Logger, store.Description);

await app.RunAsync();
return 0;
}

/// <summary>供集成测试引用宿主。</summary>
public partial class Program;
