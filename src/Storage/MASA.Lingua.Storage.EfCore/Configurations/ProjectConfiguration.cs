using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// projects：项目只有「多语言配置」——语种、共享命名空间继承、发布策略与最近一次发布结果。
/// <para>
/// 发布策略是值对象，存单列 jsonb（owned JSON）：策略字段变化不需要改表结构，
/// 也不会把策略字段混进项目表的列里造成语义噪音。
/// </para>
/// </summary>
internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
        builder.Property(x => x.WriteKey).HasColumnName("write_key").HasMaxLength(64);
        builder.Property(x => x.DefaultCulture).HasColumnName("default_culture").HasMaxLength(32).IsRequired();

        // EF Core 原生 primitive collection：Npgsql 默认映射成 jsonb，不再手写转换器与比较器
        builder.Property(x => x.SupportedCultures).HasColumnName("supported_cultures").IsRequired();
        builder.Property(x => x.SharedNamespaceCodes).HasColumnName("shared_namespace_codes").IsRequired();

        builder.Property(x => x.LastPublishedAt).HasColumnName("last_published_at");
        builder.Property(x => x.LastPublishedVersionHash).HasColumnName("last_published_version_hash").HasMaxLength(64);

        builder.HasIndex(x => x.Code).IsUnique();

        builder.OwnsOne(x => x.PublishPolicy, policy => policy.ToJson("publish_policy"));

        builder.HasMany(x => x.Namespaces)
            .WithOne()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.GlossaryTerms)
            .WithOne()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.UseRowVersionConcurrencyToken();
    }
}
