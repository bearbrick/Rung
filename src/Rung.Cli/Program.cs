using System.Globalization;
using Microsoft.Extensions.Logging;
using Rung.Abstractions;
using Rung.Core;
using Rung.Drivers.S7;

namespace Rung.Cli;

/// <summary>
/// Rung 的命令行入口。
/// <para>
/// 它是一个真正的常驻服务：断线会按退避策略自己重连，恢复后继续采集，
/// 不需要人工重启。<c>--once</c> 则采一轮就退出，适合脚本和现场点位验证。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>进程入口。</summary>
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        return await RunAsync(args, Console.Out, cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>可测试的入口：输出目标和取消信号都从外面传进来。</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage(output);
            return args.Length == 0 ? 1 : 0;
        }

        var configPath = args[0];
        var once = args.Contains("--once", StringComparer.Ordinal);
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        var startupTimeout = ParseTimeout(args);

        if (!File.Exists(configPath))
        {
            output.WriteLine($"找不到配置文件：{configPath}");
            return 1;
        }

        try
        {
            return await ServeAsync(
                RungConfig.Load(configPath), once, quiet, startupTimeout, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("已停止。");
            return 0;
        }
        catch (Exception ex) when (ex is RungException or IOException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            output.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ServeAsync(
        RungConfig config,
        bool once,
        bool quiet,
        TimeSpan startupTimeout,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var devices = config.ResolveDevices();

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(quiet ? LogLevel.Warning : LogLevel.Information)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            }));

        var cache = new TagCache();

        // --once 只要一轮结果，不必让输出被逐条变化刷屏
        IReadOnlyList<ITagSink> sinks = once ? [] : [new ConsoleTagSink(output)];

        await using var host = new GatewayHost([new S7DriverFactory()], cache, sinks, loggerFactory);

        foreach (var device in devices)
        {
            host.AddDevice(device.ToDeviceOptions(), device.ToTagDefs(), config.ToWorkerOptions(device));
            output.WriteLine($"注册设备 {device.DeviceId} → {device.Host}:{device.Port}（{device.Protocol}）");
        }

        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = host.RunAsync(scope.Token);

        try
        {
            await WaitForFirstPollAsync(host, startupTimeout, scope.Token).ConfigureAwait(false);

            output.WriteLine();
            foreach (var status in host.DeviceStatuses)
            {
                PrintDeviceSummary(status, output);
            }

            PrintValues(cache, output);

            if (once)
            {
                return 0;
            }

            output.WriteLine();
            output.WriteLine("持续采集中，Ctrl+C 停止。只打印发生变化的点位。");

            await run.ConfigureAwait(false);
            return 0;
        }
        finally
        {
            await scope.CancelAsync().ConfigureAwait(false);
            await Observe(run).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 等第一轮采集出结果。
    /// <para>
    /// 这里必须有超时。作为常驻服务，连不上就无限重连是对的；
    /// 但首次启动时若配置写错（IP 打错、机架槽号不对），
    /// 无限重连的表现就是进程静静地挂在那里什么也不说——
    /// 最糟糕的失败方式。所以给启动阶段设一个上限，到点就把原因说出来。
    /// </para>
    /// </summary>
    private static async Task WaitForFirstPollAsync(
        GatewayHost host,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (host.DeviceStatuses.Any(static status => status.LastSuccessUtc is null))
        {
            if (DateTime.UtcNow >= deadline)
            {
                var pending = host.DeviceStatuses
                    .Where(static status => status.LastSuccessUtc is null)
                    .Select(static status => $"{status.DeviceId}（{status.LastError ?? "未知原因"}）");

                throw new RungException(
                    $"{timeout.TotalSeconds:F0} 秒内以下设备未能完成首次采集：{string.Join("、", pending)}");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>解析 <c>--timeout &lt;秒&gt;</c>，缺省 30 秒。</summary>
    private static TimeSpan ParseTimeout(string[] args)
    {
        var index = Array.IndexOf(args, "--timeout");

        return index >= 0 && index + 1 < args.Length
            && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromSeconds(30);
    }

    private static void PrintDeviceSummary(DeviceStatus status, TextWriter output)
    {
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{status.DeviceId}] PDU {status.NegotiatedPduLength} 字节 · "
            + $"{status.ActiveTagCount} 个点位 → 每轮 {status.RequestCount} 次请求 · "
            + $"上轮耗时 {status.LastPollDuration.TotalMilliseconds:F1} ms"));

        foreach (var issue in status.Issues)
        {
            output.WriteLine($"  ! 配置问题 {issue}");
        }
    }

    private static void PrintValues(TagCache cache, TextWriter output)
    {
        var snapshots = cache.Snapshot();
        if (snapshots.Count == 0)
        {
            return;
        }

        var nameWidth = Math.Max(4, snapshots.Max(static s => s.Tag.Name.Length));

        foreach (var snapshot in snapshots)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {snapshot.Tag.Name.PadRight(nameWidth)}  {ConsoleTagSink.Format(snapshot.Value),20}"
                + $"   {snapshot.DeviceId}/{snapshot.Tag.Address}"));
        }
    }

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机路径上的预期取消
        }
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            Rung — 轻量级 PLC 数据采集网关

            用法：
              rung <配置文件.json>          持续采集所有设备，断线自动重连，Ctrl+C 停止
              rung <配置文件.json> --once   采集一轮后退出
              rung <配置文件.json> --quiet  只打印警告以上级别的日志
              rung <配置文件.json> --timeout <秒>  首次连接的等待上限，缺省 30

            配置文件示例见 samples/s7-demo.json
            """);
    }
}
