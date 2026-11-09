using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// translation_values：词条下每个语种一条译文。
/// 状态以字符串落库，重排 <see cref="TranslationStatus"/> 不会静默改变历史数据的语义。
/// </summary>
internal sealed class TranslationValueConfiguration : IEntityTypeConfiguration<TranslationValue>
{
    public void Configure(EntityTypeBuilder<TranslationValue> builder)
    {
        builder.ToTable("translation_values");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.EntryId).HasColumnName("entry_id");
        builder.Property(x => x.Culture).HasColumnName("culture").HasMaxLength(32).IsRequired();
        builder.Property(x => x.Text).HasColumnName("text").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.ReviewComment).HasColumnName("review_comment").HasMaxLength(512);
        builder.Property(x => x.SourceTextHash).HasColumnName("source_text_hash").HasMaxLength(64);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(x => new { x.EntryId, x.Culture }).IsUnique();

        builder.UseRowVersionConcurrencyToken();
    }
}
