using System.Globalization;
using Rung.Configuration;
using Rung.Abstractions;
using Rung.Configuration.Storage;
using Rung.Core;
using Rung.Drivers.Modbus;
using Rung.Drivers.S7;

namespace Rung.Cli;

/// <summary>
/// <c>rung config …</c> 子命令：把配置在 JSON、SQLite、Excel 之间搬来搬去。
/// <para>
/// 现场交接的实际流程是这样的：电气工程师给一份 Excel 点位表 →
/// <c>config import</c> 进 SQLite → 网关直接用。改了之后
/// <c>config export</c> 出来发回去核对。省掉的手工誊抄正是地址配错的主要来源。
/// </para>
/// </summary>
internal static class ConfigCommands
{
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        var action = args.Length > 1 ? args[1] : string.Empty;
        var database = ReadOption(args, "--db");

        if (string.IsNullOrEmpty(database))
        {
            output.WriteLine("需要用 --db <文件> 指定配置数据库。");
            return 1;
        }

        var store = new SqliteConfigStore(database);

        return action switch
        {
            "import" => await ImportAsync(args, store, output, cancellationToken).ConfigureAwait(false),
            "export" => await ExportAsync(args, store, output, cancellationToken).ConfigureAwait(false),
            "list" => await ListAsync(store, output, cancellationToken).ConfigureAwait(false),
            "check" => await CheckAsync(args, store, output, cancellationToken).ConfigureAwait(false),
            _ => Usage(output),
        };
    }

    private static async Task<int> ImportAsync(
        string[] args, SqliteConfigStore store, TextWriter output, CancellationToken cancellationToken)
    {
        var source = args.Length > 2 ? args[2] : string.Empty;

        if (!File.Exists(source))
        {
            output.WriteLine($"找不到来源文件：{source}");
            return 1;
        }

        RungConfig config;
        var fromExcel = source.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

        if (fromExcel)
        {
            config = TagExcel.Import(source, out var issues);

            foreach (var issue in issues)
            {
                output.WriteLine($"  ! {issue}");
            }

            if (issues.Count > 0)
            {
                // 有问题的行被跳过而不是整份拒绝：一张几百行的表里错两行，
                // 让人改完重来一遍不如先把对的导进去，再单独修那两行
                output.WriteLine($"  共 {issues.Count} 行有问题，已跳过；其余照常导入。");
            }
        }
        else
        {
            config = RungConfig.Load(source);
        }

        // 默认整表替换：点位表是一份份交付的，合并会让"这次交付改了什么"说不清
        var merge = args.Contains("--merge", StringComparer.Ordinal);
        // Excel 不含全局设置，照单全收会把采集组周期、重连参数、
        // Redis / MQTT 配置静默清空
        var result = await store.ImportAsync(
            config, replace: !merge, cancellationToken, includeGlobalSettings: !fromExcel)
            .ConfigureAwait(false);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"已导入 {store.DatabasePath}：新增 {result.DevicesAdded} 台设备，"
            + $"替换 {result.DevicesUpdated} 台，当前共 {result.TotalTags} 个点位。"));

        return 0;
    }

    private static async Task<int> ExportAsync(
        string[] args, SqliteConfigStore store, TextWriter output, CancellationToken cancellationToken)
    {
        var target = args.Length > 2 ? args[2] : "rung-tags.xlsx";
        var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        await TagExcel.ExportAsync(target, config, cancellationToken).ConfigureAwait(false);

        var tagCount = config.ResolveDevices().Sum(static device => device.Tags?.Count ?? 0);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"已导出 {target}：{config.ResolveDevices().Count} 台设备，{tagCount} 个点位。"));

        return 0;
    }

    private static async Task<int> ListAsync(
        SqliteConfigStore store, TextWriter output, CancellationToken cancellationToken)
    {
        var devices = await store.ListDevicesAsync(cancellationToken).ConfigureAwait(false);

        if (devices.Count == 0)
        {
            output.WriteLine($"{store.DatabasePath} 里还没有设备。用 config import 导入。");
            return 0;
        }

        output.WriteLine($"{store.DatabasePath}：");
        foreach (var (deviceId, protocol, tagCount, enabled) in devices)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {deviceId,-20} {protocol,-12} {tagCount,4} 个点位  {(enabled ? "" : "（已停用）")}"));
        }

        return 0;
    }

    /// <summary>
    /// 离线校验配置。不连接任何设备，因此出差前、交付前都可以随手跑一遍。
    /// </summary>
    private static async Task<int> CheckAsync(
        string[] args, SqliteConfigStore store, TextWriter output, CancellationToken cancellationToken)
    {
        // 也允许直接校验一份 JSON 或 Excel，不必先导进数据库
        var source = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
            ? args[2]
            : null;

        RungConfig config;

        if (source is null)
        {
            config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            output.WriteLine($"校验 {store.DatabasePath}");
        }
        else if (source.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            config = TagExcel.Import(source, out var excelIssues);
            output.WriteLine($"校验 {source}");

            foreach (var issue in excelIssues)
            {
                output.WriteLine($"  ! {issue}");
            }
        }
        else
        {
            config = RungConfig.Load(source);
            output.WriteLine($"校验 {source}");
        }

        IDeviceDriverFactory[] factories = [new S7DriverFactory(), new ModbusDriverFactory()];
        var registrations = config.ResolveDevices()
            .Select(device => new DeviceRegistration(
                device.ToDeviceOptions(), device.ToTagDefs(), config.ToWorkerOptions(device)))
            .ToList();

        var check = ConfigChecker.Check(factories, registrations);

        output.WriteLine();
        foreach (var result in check.Devices)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {result.DeviceId,-18} {result.Protocol,-12} "
                + $"{result.TagCount,4} 个点位 → 每轮 {result.RequestCount} 次请求"));

            foreach (var issue in result.Issues)
            {
                output.WriteLine($"      ! {issue}");
            }
        }

        foreach (var duplicate in check.DuplicateTagNames)
        {
            // 跨设备重名会让写命令落到错误的设备上，是代价最大的一类配置错误
            output.WriteLine($"  ! 点位名重复：{duplicate}");
        }

        output.WriteLine();
        output.WriteLine(check.ProblemCount == 0
            ? $"未发现问题。共 {check.Devices.Count} 台设备，{check.TagCount} 个点位，"
                + $"每轮 {check.RequestCount} 次请求。"
            : $"发现 {check.ProblemCount} 个问题。请求次数按最保守的协商假设估算，真机只会更少。");

        return check.ProblemCount == 0 ? 0 : 1;
    }

    private static int Usage(TextWriter output)
    {
        output.WriteLine("""
            用法：
              rung config import <文件.json|文件.xlsx> --db <数据库>   导入配置
                  --merge   按设备标识合并，而不是整表替换
              rung config export <文件.xlsx> --db <数据库>            导出成 Excel
              rung config list --db <数据库>                          列出设备
              rung config check --db <数据库>                         离线校验配置
              rung config check <文件.json|.xlsx> --db <数据库>       校验文件而不必先导入
            """);

        return 1;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
