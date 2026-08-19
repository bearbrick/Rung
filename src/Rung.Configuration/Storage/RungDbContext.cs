using Microsoft.EntityFrameworkCore;

namespace Rung.Configuration.Storage;

/// <summary>
/// 配置数据库。
/// <para>
/// 选 SQLite 是因为它单文件、零依赖、跟着数据目录走——网关往往部署在
/// 产线侧的一台小机器上，再拉一个数据库服务进来不现实。
/// </para>
/// <para>
/// 用 EF Core 主要图的是 Migrations：配置结构将来一定会变，
/// 有迁移机制就不必手写"检测旧结构再 ALTER TABLE"那套东西。
/// </para>
/// </summary>
public sealed class RungDbContext(DbContextOptions<RungDbContext> options) : DbContext(options)
{
    /// <summary>设备表。</summary>
    public DbSet<DeviceRecord> Devices => Set<DeviceRecord>();

    /// <summary>点位表。</summary>
    public DbSet<TagRecord> Tags => Set<TagRecord>();

    /// <summary>全局设置。</summary>
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    /// <summary>用文件路径创建一个上下文。</summary>
    public static RungDbContext Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new DbContextOptionsBuilder<RungDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new RungDbContext(options);
    }

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<DeviceRecord>(entity =>
        {
            entity.HasIndex(static device => device.DeviceId).IsUnique();

            entity.HasMany(static device => device.Tags)
                .WithOne(static tag => tag.Device)
                .HasForeignKey(static tag => tag.DeviceRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 业务点位名全局唯一：重名会让写命令路由到错误的设备上。
        // 在数据库层面加约束，比只靠 GatewayHost 启动时检查更早、更硬
        modelBuilder.Entity<TagRecord>()
            .HasIndex(static tag => tag.Name)
            .IsUnique();

        modelBuilder.Entity<SettingRecord>().HasKey(static setting => setting.Key);
    }
}
