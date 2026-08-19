using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Rung.Configuration.Storage;

/// <summary>
/// 从 SQLite 加载配置，并提供导入导出。
/// <para>
/// 启动时自动应用迁移：配置结构随版本演进是必然的，让用户手工跑迁移
/// 是在给自己制造"升级后起不来"的现场事故。
/// </para>
/// </summary>
public sealed class SqliteConfigStore(string databasePath) : IConfigStore
{
    /// <summary>全局设置在 <see cref="SettingRecord"/> 里使用的键。</summary>
    public const string GlobalSettingsKey = "global";

    private static readonly JsonSerializerOptions SettingsJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>数据库文件路径。</summary>
    public string DatabasePath => databasePath;

    /// <inheritdoc/>
    public string Description => $"SQLite 数据库 {databasePath}";

    /// <inheritdoc/>
    public async Task<RungConfig> LoadAsync(CancellationToken cancellationToken)
    {
        await using var context = RungDbContext.Open(databasePath);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var devices = await context.Devices
            .Include(static device => device.Tags)
            .Where(static device => device.Enabled)
            .OrderBy(static device => device.DeviceId)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var global = await LoadGlobalAsync(context, cancellationToken).ConfigureAwait(false);

        return global with
        {
            Devices = [.. devices.Select(ConfigMapping.ToConfig)],
            Device = null,
            Tags = null,
        };
    }

    /// <summary>
    /// 把一份配置写进数据库。
    /// </summary>
    /// <param name="config">来源配置，可以来自 JSON 文件或 Excel。</param>
    /// <param name="replace">true 表示先清空再写入；false 表示按设备标识合并。</param>
    /// <param name="cancellationToken">取消信号。</param>
    public async Task<ImportResult> ImportAsync(
        RungConfig config,
        bool replace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        await using var context = RungDbContext.Open(databasePath);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        if (replace)
        {
            // 级联删除会带走点位，不必单独清
            context.Devices.RemoveRange(await context.Devices.ToListAsync(cancellationToken)
                .ConfigureAwait(false));

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var added = 0;
        var updated = 0;

        foreach (var device in config.ResolveDevices())
        {
            var existing = await context.Devices
                .Include(static d => d.Tags)
                .FirstOrDefaultAsync(d => d.DeviceId == device.DeviceId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                context.Devices.Add(ConfigMapping.ToRecord(device));
                added++;
                continue;
            }

            // 同名设备按整体替换处理：点位表是一份份交付的，
            // 逐点位差异合并只会让"这次交付到底改了什么"变得说不清
            context.Tags.RemoveRange(existing.Tags);

            var replacement = ConfigMapping.ToRecord(device);
            existing.Protocol = replacement.Protocol;
            existing.Host = replacement.Host;
            existing.Port = replacement.Port;
            existing.TimeoutMs = replacement.TimeoutMs;
            existing.RetryCount = replacement.RetryCount;
            existing.ExtraJson = replacement.ExtraJson;
            existing.PollIntervalMs = replacement.PollIntervalMs;
            existing.Tags = replacement.Tags;

            updated++;
        }

        await SaveGlobalAsync(context, config, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var tagCount = await context.Tags.CountAsync(cancellationToken).ConfigureAwait(false);

        return new ImportResult(added, updated, tagCount);
    }

    /// <summary>列出全部设备及其点位数，用于命令行展示。</summary>
    public async Task<IReadOnlyList<(string DeviceId, string Protocol, int TagCount, bool Enabled)>>
        ListDevicesAsync(CancellationToken cancellationToken)
    {
        await using var context = RungDbContext.Open(databasePath);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var rows = await context.Devices
            .OrderBy(static device => device.DeviceId)
            .Select(static device => new
            {
                device.DeviceId,
                device.Protocol,
                TagCount = device.Tags.Count,
                device.Enabled,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(static r => (r.DeviceId, r.Protocol, r.TagCount, r.Enabled))];
    }

    private static async Task<RungConfig> LoadGlobalAsync(
        RungDbContext context, CancellationToken cancellationToken)
    {
        var record = await context.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == GlobalSettingsKey, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return new RungConfig { Devices = [] };
        }

        try
        {
            return JsonSerializer.Deserialize<RungConfig>(record.Value, SettingsJsonOptions)
                ?? new RungConfig { Devices = [] };
        }
        catch (JsonException)
        {
            // 全局设置坏了就退回默认值，总比整个网关起不来强
            return new RungConfig { Devices = [] };
        }
    }

    private static async Task SaveGlobalAsync(
        RungDbContext context, RungConfig config, CancellationToken cancellationToken)
    {
        // 只存非设备部分：设备已经在自己的表里了，重复一份迟早会不一致
        var global = config with { Devices = null, Device = null, Tags = null };
        var json = JsonSerializer.Serialize(global, SettingsJsonOptions);

        var record = await context.Settings
            .FirstOrDefaultAsync(s => s.Key == GlobalSettingsKey, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            context.Settings.Add(new SettingRecord { Key = GlobalSettingsKey, Value = json });
            return;
        }

        record.Value = json;
    }
}

/// <summary>导入结果。</summary>
/// <param name="DevicesAdded">新增的设备数。</param>
/// <param name="DevicesUpdated">被整体替换的设备数。</param>
/// <param name="TotalTags">导入后数据库里的点位总数。</param>
public readonly record struct ImportResult(int DevicesAdded, int DevicesUpdated, int TotalTags);
