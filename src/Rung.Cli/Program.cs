using System.Globalization;
using Rung.Abstractions;
using Rung.Drivers.S7;

namespace Rung.Cli;

/// <summary>
/// Rung 的命令行入口。
/// <para>
/// MVP 阶段它就是全部的"产品"：指向一台 PLC，把点位值打出来。
/// 采集调度、北向输出、Web UI 都还没有，但这条链路是完整的——
/// 握手、批量合并、拆包、字节序、质量标记，一个不少。
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

    /// <summary>
    /// 可测试的入口：输出目标和取消信号都从外面传进来，
    /// 这样端到端冒烟测试可以直接调它，不必启子进程。
    /// </summary>
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

        if (!File.Exists(configPath))
        {
            output.WriteLine($"找不到配置文件：{configPath}");
            return 1;
        }

        try
        {
            var config = RungConfig.Load(configPath);
            return await PollAsync(config, once, output, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("已停止。");
            return 0;
        }
        catch (Exception ex) when (ex is RungException or IOException or InvalidDataException)
        {
            output.WriteLine($"错误：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> PollAsync(
        RungConfig config,
        bool once,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var deviceOptions = config.ToDeviceOptions();
        var tags = config.ToTagDefs();

        if (!string.Equals(deviceOptions.Protocol, "s7", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"当前版本只支持 s7 协议，配置里是 {deviceOptions.Protocol}");
            return 1;
        }

        await using var driver = new S7Driver(deviceOptions);

        output.WriteLine($"正在连接 {deviceOptions.Host}:{deviceOptions.Port} …");
        await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
        output.WriteLine($"已连接，协商 PDU 长度 {driver.MaxPduLength} 字节");

        var plan = (Rung.Protocols.S7.S7ReadPlan)driver.CreateReadPlan(tags);
        PrintPlanSummary(plan, output);

        var values = new TagValue[tags.Count];

        if (once)
        {
            await driver.ExecuteAsync(plan, values, cancellationToken).ConfigureAwait(false);
            PrintValues(tags, values, output);
            return 0;
        }

        // 用 PeriodicTimer 而不是 Task.Delay 循环：后者会把每轮的执行耗时
        // 累加进周期，跑上几小时就明显漂移了
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.PollIntervalMs));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var started = DateTime.UtcNow;
            var good = await driver.ExecuteAsync(plan, values, cancellationToken).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - started;

            output.WriteLine();
            output.WriteLine(FormattableString.Invariant(
                $"── {DateTime.Now:HH:mm:ss}  {good}/{tags.Count} 良好  耗时 {elapsed.TotalMilliseconds:F1} ms"));
            PrintValues(tags, values, output);
        }

        return 0;
    }

    private static void PrintPlanSummary(Rung.Protocols.S7.S7ReadPlan plan, TextWriter output)
    {
        var needed = plan.Tags.Count == 0 ? 0 : plan.TotalFetchedBytes;

        output.WriteLine(FormattableString.Invariant(
            $"采集计划：{plan.ActiveTagCount} 个点位 → {plan.RequestCount} 次请求，每轮取回 {needed} 字节"));

        foreach (var issue in plan.Issues)
        {
            output.WriteLine($"  ! 配置问题 {issue}");
        }
    }

    private static void PrintValues(IReadOnlyList<TagDef> tags, TagValue[] values, TextWriter output)
    {
        var nameWidth = Math.Max(4, tags.Max(static t => t.Name.Length));

        for (var i = 0; i < tags.Count; i++)
        {
            var value = values[i];
            var display = value.Quality switch
            {
                TagQuality.Good => Format(value),
                TagQuality.Uninitialized => "—",
                _ => $"<{value.Quality}>",
            };

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {tags[i].Name.PadRight(nameWidth)}  {display,20}   {tags[i].Address}"));
        }
    }

    private static string Format(TagValue value) => value.DataType switch
    {
        TagDataType.Float32 or TagDataType.Float64 =>
            value.AsDouble().ToString("0.###", CultureInfo.InvariantCulture),
        TagDataType.Bool => value.AsBool() ? "true" : "false",
        TagDataType.String => value.AsString(),
        TagDataType.Bytes => Convert.ToHexString(value.AsBytes()),
        _ => value.AsInt64().ToString(CultureInfo.InvariantCulture),
    };

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("""
            Rung — 轻量级 PLC 数据采集网关

            用法：
              rung <配置文件.json>          按配置周期采集并打印
              rung <配置文件.json> --once   采集一轮后退出

            配置文件示例见 samples/s7-demo.json
            """);
    }
}
