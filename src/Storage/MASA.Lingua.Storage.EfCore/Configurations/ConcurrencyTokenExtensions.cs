using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// 行版本并发令牌：把 PostgreSQL 的系统列 <c>xmin</c> 映射成乐观并发令牌。
/// <para>
/// <c>xmin</c> 由 PostgreSQL 在每次 UPDATE 时自动改写，不需要额外列、不需要应用维护版本号；
/// EF 会把它放进 UPDATE 的 WHERE 条件，影响 0 行即抛 <see cref="DbUpdateConcurrencyException"/>，
/// 由仓储翻译成用户可读的冲突提示。
/// </para>
/// </summary>
internal static class ConcurrencyTokenExtensions
{
    public static void UseRowVersionConcurrencyToken<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
