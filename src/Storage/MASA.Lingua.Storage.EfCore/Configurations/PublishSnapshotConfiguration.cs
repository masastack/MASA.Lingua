using MASA.Lingua.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// publish_snapshots：发布/回滚审计。
/// <para>
/// 项目外键用 <see cref="DeleteBehavior.Restrict"/>：快照是审计证据，项目被删除时不允许静默连坐清空；
/// 回滚自引用外键同样是 Restrict。列表查询按 (project_id, published_at desc) 走索引，且不加载 content 大字段。
/// </para>
/// </summary>
internal sealed class PublishSnapshotConfiguration : IEntityTypeConfiguration<PublishSnapshot>
{
    public void Configure(EntityTypeBuilder<PublishSnapshot> builder)
    {
        builder.ToTable("publish_snapshots");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.VersionHash).HasColumnName("version_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ReleaseNote).HasColumnName("release_note").HasMaxLength(512);
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").IsRequired();
        builder.Property(x => x.EntryCount).HasColumnName("entry_count");
        builder.Property(x => x.PublishedCultures).HasColumnName("published_cultures").IsRequired();
        builder.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(x => x.RollbackOfSnapshotId).HasColumnName("rollback_of_snapshot_id");
        builder.Property(x => x.Forced).HasColumnName("forced");
        builder.Property(x => x.ForceReason).HasColumnName("force_reason").HasMaxLength(512);
        builder.Property(x => x.GateSummary).HasColumnName("gate_summary").HasMaxLength(1024);
        builder.Property(x => x.Content).HasColumnName("content");
        builder.Property(x => x.ContentSize).HasColumnName("content_size");

        builder.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PublishSnapshot>()
            .WithMany()
            .HasForeignKey(x => x.RollbackOfSnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.ProjectId, x.PublishedAt }).IsDescending(false, true);

        builder.UseRowVersionConcurrencyToken();
    }
}
