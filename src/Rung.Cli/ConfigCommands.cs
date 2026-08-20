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
            "key" => await KeyAsync(args, store, output, cancellationToken).ConfigureAwait(false),
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

    /// <summary>API 密钥管理。</summary>
    private static async Task<int> KeyAsync(
        string[] args, SqliteConfigStore store, TextWriter output, CancellationToken cancellationToken)
    {
        var action = args.Length > 2 ? args[2] : string.Empty;
        var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var existing = config.Auth?.Keys.ToList() ?? [];

        switch (action)
        {
            case "list":
                if (existing.Count == 0)
                {
                    output.WriteLine("还没有任何 API 密钥。写接口目前是关闭的。");
                    output.WriteLine("用 rung config key add <名称> --write 生成一个。");
                    return 0;
                }

                foreach (var key in existing)
                {
                    output.WriteLine($"  {key.Name,-24} {(key.CanWrite ? "可读写" : "只读")}");
                }

                output.WriteLine();
                output.WriteLine(
                    config.Auth?.RequireForReads == true ? "读接口需要密钥。" : "读接口对内网放开。");

                return 0;

            case "add":
                var name = args.Length > 3 ? args[3] : string.Empty;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("--", StringComparison.Ordinal))
                {
                    output.WriteLine("需要给密钥起个名字：rung config key add <名称> [--write]");
                    return 1;
                }

                if (existing.Any(k => string.Equals(k.Name, name, StringComparison.Ordinal)))
                {
                    output.WriteLine($"密钥名 \"{name}\" 已存在。");
                    return 1;
                }

                var canWrite = args.Contains("--write", StringComparer.Ordinal);
                var (created, plaintext) = ApiKeys.Create(name, canWrite);

                existing.Add(new ApiKeyConfig
                {
                    Name = created.Name, Hash = created.Hash, CanWrite = created.CanWrite,
                });

                await SaveAuthAsync(store, config, existing, cancellationToken).ConfigureAwait(false);

                // 明文只在这一刻存在，库里只有哈希，之后再也拿不回来
                output.WriteLine($"已生成密钥 \"{name}\"（{(canWrite ? "可读写" : "只读")}）：");
                output.WriteLine();
                output.WriteLine($"    {plaintext}");
                output.WriteLine();
                output.WriteLine("请立刻保存——它只显示这一次，库里存的是哈希，找不回来。");
                output.WriteLine("调用时放进 X-Rung-Key 请求头。");

                return 0;

            case "remove":
                var target = args.Length > 3 ? args[3] : string.Empty;
                if (existing.RemoveAll(k => string.Equals(k.Name, target, StringComparison.Ordinal)) == 0)
                {
                    output.WriteLine($"没有名为 \"{target}\" 的密钥。");
                    return 1;
                }

                await SaveAuthAsync(store, config, existing, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"已删除密钥 \"{target}\"。");

                return 0;

            default:
                output.WriteLine("用法：");
                output.WriteLine("  rung config key list --db <数据库>                    列出密钥");
                output.WriteLine("  rung config key add <名称> [--write] --db <数据库>    生成密钥");
                output.WriteLine("  rung config key remove <名称> --db <数据库>           删除密钥");

                return 1;
        }
    }

    private static async Task SaveAuthAsync(
        SqliteConfigStore store,
        RungConfig config,
        List<ApiKeyConfig> keys,
        CancellationToken cancellationToken)
    {
        var updated = config with
        {
            Auth = (config.Auth ?? new AuthConfig()) with { Keys = keys },
        };

        // 只改全局设置，设备按标识合并、原样保留
        await store.ImportAsync(updated, replace: false, cancellationToken).ConfigureAwait(false);
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
              rung config key list|add|remove --db <数据库>           管理 API 密钥
            """);

        return 1;
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
