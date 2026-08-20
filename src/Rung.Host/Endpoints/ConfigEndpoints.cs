using Microsoft.AspNetCore.Http.HttpResults;
using Rung.Abstractions;
using Rung.Configuration;
using Rung.Configuration.Storage;
using Rung.Core;

namespace Rung.Host.Endpoints;

/// <summary>
/// 配置管理接口。
/// <para>
/// 刻意<b>不</b>提供网页版的点位表格编辑器：几百行点位在网页表格里改，
/// 体验一定不如 Excel，而电气工程师手上本来就是 Excel。
/// 这里支持的是真正有价值的那条工作流——
/// 下载 Excel → 在 Excel 里改 → 上传 → 校验 → 一键生效。
/// </para>
/// </summary>
public static class ConfigEndpoints
{
    /// <summary>上传文件的大小上限。点位表再大也就几百 KB，留 8 MB 绰绰有余。</summary>
    private const long MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>把配置管理接口挂到路由上。</summary>
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api/config").WithTags("Rung 配置");

        api.MapGet("/", GetSummary)
            .WithSummary("当前配置摘要")
            .RequireRead();

        api.MapGet("/export", ExportExcelAsync)
            .WithSummary("导出点位表为 Excel")
            .WithDescription("导出的文件改完可以直接上传回来。")
            .RequireRead();

        api.MapPost("/validate", ValidateAsync)
            .WithSummary("只校验上传的配置，不写入")
            .WithDescription("上传 .xlsx 或 .json，逐行报出问题。不改变任何现有配置。")
            .DisableAntiforgery()
            .RequireRead();

        api.MapPost("/import", ImportAsync)
            .WithSummary("导入配置并立即生效")
            .WithDescription(
                "上传 .xlsx 或 .json。先校验，有问题就整份拒绝；"
                + "通过之后写入并在线重载，只有配置真的变了的设备会被重启。")
            .DisableAntiforgery()
            .RequireWrite();

        return app;
    }

    private static Ok<ConfigSummaryView> GetSummary(GatewayHost gateway, IConfigStore store)
        => TypedResults.Ok(new ConfigSummaryView(
            store.Description,
            store is SqliteConfigStore,
            gateway.DeviceStatuses.Count,
            gateway.AllTags.Count));

    private static async Task<IResult> ExportExcelAsync(
        IConfigStore store, CancellationToken cancellationToken)
    {
        var config = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(Path.GetTempPath(), $"rung-export-{Guid.NewGuid():N}.xlsx");

        try
        {
            await TagExcel.ExportAsync(temp, config, cancellationToken).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false);

            return TypedResults.File(
                bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"rung-tags-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<Results<Ok<ConfigCheckView>, BadRequest<string>>> ValidateAsync(
        IFormFile file,
        IEnumerable<IDeviceDriverFactory> factories,
        CancellationToken cancellationToken)
    {
        var (view, error) = await ReadAndCheckAsync(file, factories, cancellationToken)
            .ConfigureAwait(false);

        return view is null
            ? TypedResults.BadRequest(error ?? "无法解析上传的文件。")
            : TypedResults.Ok(view);
    }

    private static async Task<Results<Ok<ImportView>, BadRequest<string>>> ImportAsync(
        IFormFile file,
        IConfigStore store,
        GatewayHost gateway,
        IEnumerable<IDeviceDriverFactory> factories,
        CancellationToken cancellationToken)
    {
        if (store is not SqliteConfigStore sqlite)
        {
            return TypedResults.BadRequest(
                $"当前配置来源是{store.Description}，只读。要在线改配置请用 --Db 指向 SQLite。");
        }

        var (view, error) = await ReadAndCheckAsync(file, factories, cancellationToken)
            .ConfigureAwait(false);

        if (view is null)
        {
            return TypedResults.BadRequest(error!);
        }

        // 有问题就整份拒绝。导入是个会立刻影响产线采集的动作，
        // 这里不像 CLI 那样"坏行跳过"——CLI 是人在盯着看，这里可能是脚本在调
        if (view.ProblemCount > 0)
        {
            return TypedResults.BadRequest(
                $"配置有 {view.ProblemCount} 个问题，已拒绝导入。请先调用 /api/config/validate 查看详情。");
        }

        // Excel 只承载设备和点位，不含全局设置。照单全收会把采集组周期、
        // 重连参数、Redis / MQTT 配置静默清空——表现为"改了个点位名，
        // 结果 Redis 输出没了"，是最难联想到原因的一类事故
        var fromExcel = Path.GetExtension(file.FileName)
            .Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

        await sqlite.ImportAsync(view.Config!, replace: true, cancellationToken,
            includeGlobalSettings: !fromExcel).ConfigureAwait(false);

        // 重新从库里读，这样全局设置用的是保留下来的那份而不是上传文件里的空值
        var effective = await sqlite.LoadAsync(cancellationToken).ConfigureAwait(false);

        var reload = await gateway
            .ReloadAsync(GatewayEndpoints.ToRegistrations(effective), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new ImportView(
            view.Devices, reload.Added, reload.Restarted, reload.Removed, reload.Unchanged));
    }

    private static async Task<(ConfigCheckView? View, string? Error)> ReadAndCheckAsync(
        IFormFile file,
        IEnumerable<IDeviceDriverFactory> factories,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return (null, "没有收到文件。");
        }

        if (file.Length > MaxUploadBytes)
        {
            return (null, $"文件 {file.Length / 1024} KB 超过上限 {MaxUploadBytes / 1024 / 1024} MB。");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".xlsx" or ".json"))
        {
            return (null, $"只支持 .xlsx 和 .json，收到的是 \"{file.FileName}\"。");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"rung-upload-{Guid.NewGuid():N}{extension}");

        try
        {
            await using (var stream = File.Create(temp))
            {
                await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            RungConfig config;
            IReadOnlyList<ExcelIssue> excelIssues = [];

            if (extension == ".xlsx")
            {
                config = TagExcel.Import(temp, out excelIssues);
            }
            else
            {
                config = RungConfig.Load(temp);
            }

            var registrations = GatewayEndpoints.ToRegistrations(config);
            var check = ConfigChecker.Check(factories, registrations);

            return (new ConfigCheckView(
                [.. check.Devices.Select(static device => new DeviceCheckView(
                    device.DeviceId, device.Protocol, device.TagCount, device.RequestCount,
                    [.. device.Issues.Select(static issue =>
                        new TagIssueView(issue.TagName, issue.Reason))]))],
                check.DuplicateTagNames,
                [.. excelIssues.Select(static issue => issue.ToString())],
                check.TagCount,
                check.RequestCount,
                check.ProblemCount + excelIssues.Count)
            {
                Config = config,
            }, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or System.Text.Json.JsonException or ArgumentException)
        {
            return (null, $"无法解析上传的文件：{ex.Message}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
