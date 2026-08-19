using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rung.Configuration.Storage;

/// <summary>
/// 供 <c>dotnet ef migrations</c> 使用。运行期不会走这里——
/// 真正的连接由 <see cref="RungDbContext.Open"/> 按配置的数据目录建立。
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RungDbContext>
{
    public RungDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RungDbContext>()
            .UseSqlite("Data Source=rung-design.db")
            .Options;

        return new RungDbContext(options);
    }
}
