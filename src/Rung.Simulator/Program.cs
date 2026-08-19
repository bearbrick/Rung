using System.Globalization;
using System.Text.Json;

namespace Rung.Simulator;

/// <summary>
/// 模拟器命令行入口。
/// <para>
/// 没有真机的时候，它就是真机。信号会随时间变化，故障可以按需注入，
/// 因此死区过滤、断线重连、超时处理这些真正容易出问题的地方，
/// 全都能在办公室里验证。
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

    /// <summary>可测试的入口。</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            output.WriteLine("""
                rung-sim — 西门子 S7 设备模拟器

                用法：
                  rung-sim <配置文件.json>

                信号类型：
                  constant    恒定值。客户端写入后会保留，适合模拟设定值
                  counter     单调递增，模拟产量、工件计数
                  ramp        锯齿波，模拟批次进度
                  sine        正弦波，模拟温度、压力
                  toggle      方波，模拟运行/停止状态
                  randomwalk  有界随机游走，固定种子因而可复现

                故障注入（faults 段）：
                  rejectConnections    拒绝连接，模拟机架槽号配错
                  responseDelayMs      应答延迟，触发客户端超时
                  dropAfterExchanges   收发 N 次后断开，验证重连
                  dropEverySeconds     每隔若干秒断一次
                  failingDbNumbers     指定 DB 一律返回"对象不存在"
                  rejectWrites         拒绝所有写命令

                配置示例见 samples/simulator.json
                """);

            return args.Length == 0 ? 1 : 0;
        }

        if (!File.Exists(args[0]))
        {
            output.WriteLine($"找不到配置文件：{args[0]}");
            return 1;
        }

        SimulatorConfig config;
        try
        {
            config = SimulatorConfig.Load(args[0]);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            output.WriteLine($"错误：{ex.Message}");
            return 1;
        }

        var servers = new List<S7SimulatorServer>();

        try
        {
            foreach (var device in config.Devices)
            {
                var server = new S7SimulatorServer(device);
                servers.Add(server);

                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{server.Name}] 监听 127.0.0.1:{server.Port}，"
                    + $"PDU {server.NegotiatedPduLength}，{device.Signals.Count} 个信号"));

                foreach (var signal in device.Signals)
                {
                    output.WriteLine($"    {signal}  {signal.Description}");
                }
            }

            output.WriteLine();
            output.WriteLine("模拟器运行中，Ctrl+C 停止。");

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("已停止。");
            return 0;
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
