using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MASA.Lingua.Storage.Migrations.PostgreSql;

/// <summary>
/// dotnet ef 设计时工厂：以 PostgreSQL 提供程序构建模型（仅生成迁移，不会连接数据库）。
/// </summary>
public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<LinguaDbContext>
{
    public LinguaDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<LinguaDbContext>();
        LinguaStorageExtensions.ConfigureDbContextOptions(
            builder,
            "Host=127.0.0.1;Port=5432;Database=lingua_design;Username=postgres;Password=design");
        return new LinguaDbContext(builder.Options);
    }
}
