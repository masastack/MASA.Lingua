namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 术语库词条（用于强制规范专有名词译法或禁止翻译品牌词）。
/// <see cref="ProjectId"/> 为 null 表示对所有项目生效的全局术语。
/// </summary>
public sealed class GlossaryTerm
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属项目；null 表示全局术语。</summary>
    public Guid? ProjectId { get; set; }

    public required string SourceTerm { get; set; }

    public string? Description { get; set; }

    /// <summary>是否禁止翻译（例如品牌名 "Lingua", "Blazor" 保持原样）。</summary>
    public bool DoNotTranslate { get; set; }

    /// <summary>匹配时是否区分大小写（默认不区分；品牌词如 "Lingua" 可开启以避免误伤普通单词）。</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>各目标语种的强制标准译法（例如 {"en-US": "Workspace", "ja-JP": "ワークスペース"}）。</summary>
    public Dictionary<string, string> TargetTranslations { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 各目标语种可接受的变体译法（例如 {"en-US": ["Work space"]}）：
    /// 译文里出现标准译法或任一变体都算遵循，用来降低「必须包含标准译法」带来的误报。
    /// </summary>
    public Dictionary<string, List<string>> AcceptedVariants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 各目标语种的禁用译法（例如 {"en-US": ["Dashboard"]}）：译文里出现就算违规，
    /// 即使同时包含标准译法也一样。用来堵住「术语被翻成了团队不想要的词」。
    /// </summary>
    public Dictionary<string, List<string>> ForbiddenTranslations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
