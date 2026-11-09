using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MASA.Lingua.Storage.EfCore;

/// <summary>
/// PostgreSQL 专用的 EF Core DbContext：
/// 1. 模型配置按实体拆到 <c>Configurations</c> 下的 <see cref="IEntityTypeConfiguration{TEntity}"/>，本类只保留 DbSet 与全局行为；
/// 2. DateTimeOffset 直接映射为原生 timestamptz（无需字符串转换器）；
/// 3. 审计时间戳由 <see cref="SaveChanges(bool)"/> / <see cref="SaveChangesAsync(bool, CancellationToken)"/> 统一填充。
/// </summary>
public sealed class LinguaDbContext(DbContextOptions<LinguaDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<TranslationNamespace> TranslationNamespaces => Set<TranslationNamespace>();
    public DbSet<TranslationEntry> TranslationEntries => Set<TranslationEntry>();
    public DbSet<TranslationValue> TranslationValues => Set<TranslationValue>();
    public DbSet<GlossaryTerm> GlossaryTerms => Set<GlossaryTerm>();
    public DbSet<PublishSnapshot> PublishSnapshots => Set<PublishSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LinguaDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampUpdatedAt();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampUpdatedAt();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>新增或修改的实体统一戳「最后更新时间」，业务代码不再手工赋值。</summary>
    private void StampUpdatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<IHasUpdatedAt>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
