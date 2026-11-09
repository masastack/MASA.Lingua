namespace MASA.Lingua.Core.Domain;

public enum SnapshotKind
{
    Publish = 0,
    Rollback = 1
}

/// <summary>
/// 发布快照记录（用于版本审计与一键回滚）。
/// </summary>
public sealed class PublishSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProjectId { get; set; }

    /// <summary>所属项目（外键，删除项目受 Restrict 保护，审计链不被隐式破坏）。</summary>
    public Project? Project { get; set; }

    public required string VersionHash { get; set; }

    public string? ReleaseNote { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    public int EntryCount { get; set; }

    public List<string> PublishedCultures { get; set; } = [];

    public SnapshotKind Kind { get; set; } = SnapshotKind.Publish;

    /// <summary>回滚快照：回滚到的目标快照 Id。</summary>
    public Guid? RollbackOfSnapshotId { get; set; }

    /// <summary>该次发布未通过门禁，但由管理员填写原因后强制发布。</summary>
    public bool Forced { get; set; }

    public string? ForceReason { get; set; }

    /// <summary>发布时门禁检查的摘要（审计用）。</summary>
    public string? GateSummary { get; set; }

    /// <summary>
    /// 各语种发布产物（gzip 压缩的 JSON：语种 → 嵌套 JSON 文本）。回滚就是把它重新发布为线上版本。
    /// 只保留最近若干次快照的内容，更早的为 null（仍保留审计记录）。列表查询不加载此字段。
    /// </summary>
    public byte[]? Content { get; set; }

    /// <summary>快照内容的压缩后字节数；0 表示没有保存内容（无法回滚）。</summary>
    public int ContentSize { get; set; }

    /// <summary>该快照是否保存了产物内容（可回滚）。</summary>
    public bool CanRollback => ContentSize > 0;
}
