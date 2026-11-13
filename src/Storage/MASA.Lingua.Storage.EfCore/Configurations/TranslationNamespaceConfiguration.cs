using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// translation_namespaces：项目内命名空间编码唯一，全局共享命名空间（project_id 为 null）同样唯一，均<b>忽略大小写</b>（<c>Order</c> 与 <c>order</c> 是同一个命名空间）。
/// <para>
/// 物理索引是 <c>lower(code)</c> 表达式唯一索引，由迁移 <c>CaseInsensitiveKeyUniqueness</c> 用原生 SQL 建立；
/// 下面的 <c>HasIndex</c> 与之同名、同列，只用来让模型与迁移快照保持一致（EF Core 无法声明表达式索引）。
/// </para>
/// <para>
/// 用两个部分唯一索引而不是 <c>UNIQUE NULLS NOT DISTINCT</c>：后者要 PostgreSQL 15+，
/// 本站点的目标库是 14，常规唯一索引又会把 NULL 视作互不相同（共享命名空间可被重复插入），
/// 因此把「项目作用域」与「全局作用域」拆成两条带 WHERE 的唯一索引。
/// </para>
/// </summary>
internal sealed class TranslationNamespaceConfiguration : IEntityTypeConfiguration<TranslationNamespace>
{
    public void Configure(EntityTypeBuilder<TranslationNamespace> builder)
    {
        builder.ToTable("translation_namespaces");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsShared).HasColumnName("is_shared");

        builder.HasIndex(x => new { x.ProjectId, x.Code })
            .IsUnique()
            .HasFilter("project_id IS NOT NULL");

        builder.HasIndex(x => x.Code)
            .IsUnique()
            .HasFilter("project_id IS NULL");

        builder.HasMany(x => x.Entries)
            .WithOne(x => x.Namespace)
            .HasForeignKey(x => x.NamespaceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.UseRowVersionConcurrencyToken();
    }
}
