namespace MASA.Lingua.Core.Domain;

public enum TranslationStatus
{
    Draft = 0,
    AiTranslated = 1,
    NeedsReview = 2,
    Approved = 3
}

/// <summary>
/// 特定语种的译文内容与状态。
/// </summary>
public sealed class TranslationValue : IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EntryId { get; set; }

    public required string Culture { get; set; }

    public required string Text { get; set; }

    public TranslationStatus Status { get; set; } = TranslationStatus.Draft;

    public string? ReviewComment { get; set; }

    /// <summary>
    /// 翻译/确认这条译文时，对应源文案的哈希（<see cref="SourceTextHasher"/>）。为 null 表示历史数据，未知。
    /// </summary>
    public string? SourceTextHash { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 译文是否已过时：记录了源文案哈希，且与词条当前源文案不一致（源文案在译文之后被改过）。
    /// </summary>
    public bool IsStaleFor(TranslationEntry entry) =>
        SourceTextHash is not null && SourceTextHash != SourceTextHasher.Compute(entry.SourceText);
}
