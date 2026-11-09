using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// glossary_terms：作用域（全局 / 项目）内源术语唯一，三组「语种 → 译法」放 jsonb 列。
/// <para>
/// 与命名空间同理，用「项目作用域」+「全局作用域」两条部分唯一索引覆盖 NULL 的情况（目标库 PG 14 无 NULLS NOT DISTINCT）；
/// 索引本身区分大小写，忽略大小写的业务去重仍由仓储在写入前完成。
/// </para>
/// </summary>
internal sealed class GlossaryTermConfiguration : IEntityTypeConfiguration<GlossaryTerm>
{
    public void Configure(EntityTypeBuilder<GlossaryTerm> builder)
    {
        builder.ToTable("glossary_terms");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.SourceTerm).HasColumnName("source_term").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
        builder.Property(x => x.DoNotTranslate).HasColumnName("do_not_translate");
        builder.Property(x => x.CaseSensitive).HasColumnName("case_sensitive");

        builder.Property(x => x.TargetTranslations)
            .HasColumnName("target_translations")
            .HasColumnType("jsonb")
            .HasConversion(JsonColumnConverters.TargetTranslationsConverter)
            .Metadata.SetValueComparer(JsonColumnConverters.TargetTranslationsComparer);

        builder.Property(x => x.AcceptedVariants)
            .HasColumnName("accepted_variants")
            .HasColumnType("jsonb")
            .HasConversion(JsonColumnConverters.WordListsConverter)
            .Metadata.SetValueComparer(JsonColumnConverters.WordListsComparer);

        builder.Property(x => x.ForbiddenTranslations)
            .HasColumnName("forbidden_translations")
            .HasColumnType("jsonb")
            .HasConversion(JsonColumnConverters.WordListsConverter)
            .Metadata.SetValueComparer(JsonColumnConverters.WordListsComparer);

        builder.HasIndex(x => new { x.ProjectId, x.SourceTerm })
            .IsUnique()
            .HasFilter("project_id IS NOT NULL");

        builder.HasIndex(x => x.SourceTerm)
            .IsUnique()
            .HasFilter("project_id IS NULL");

        builder.UseRowVersionConcurrencyToken();
    }
}
