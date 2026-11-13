using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// translation_entries：命名空间内 key 唯一（<b>忽略大小写</b>：<c>Confirm</c> 与 <c>confirm</c> 是同一个词条）。
/// <para>
/// 物理索引是 <c>(namespace_id, lower(key))</c> 表达式唯一索引，由迁移 <c>CaseInsensitiveKeyUniqueness</c> 用原生 SQL 建立：
/// EF Core 的 Fluent API 无法声明表达式索引，所以下面的 <c>HasIndex</c> 与它同名、同列、同样唯一，只用来让模型与迁移快照保持一致；
/// 应用侧的查找统一用 <c>LinguaKeys.Fold</c> + <c>ToLower()</c> 落到这条索引上。
/// </para>
/// 词条不再冗余存命名空间编码（唯一事实源是 namespace_id → translation_namespaces.code）。
/// </summary>
internal sealed class TranslationEntryConfiguration : IEntityTypeConfiguration<TranslationEntry>
{
    public void Configure(EntityTypeBuilder<TranslationEntry> builder)
    {
        builder.ToTable("translation_entries");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.NamespaceId).HasColumnName("namespace_id");
        builder.Property(x => x.Key).HasColumnName("key").HasMaxLength(256).IsRequired();
        builder.Property(x => x.SourceText).HasColumnName("source_text").IsRequired();
        builder.Property(x => x.Context).HasColumnName("context").HasMaxLength(512);
        builder.Property(x => x.MaxLength).HasColumnName("max_length");
        builder.Property(x => x.IsAutoReported).HasColumnName("is_auto_reported");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(x => new { x.NamespaceId, x.Key }).IsUnique();

        builder.HasMany(x => x.Translations)
            .WithOne()
            .HasForeignKey(x => x.EntryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.UseRowVersionConcurrencyToken();
    }
}
