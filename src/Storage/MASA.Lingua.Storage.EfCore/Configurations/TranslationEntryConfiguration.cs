using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// translation_entries：命名空间内 key 唯一。
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
