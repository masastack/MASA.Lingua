using System.ComponentModel.DataAnnotations.Schema;

namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 单个多语言词条定义（归属于特定命名空间，包含上下文约束与长度限制）。
/// </summary>
public sealed class TranslationEntry : IHasUpdatedAt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid NamespaceId { get; set; }

    /// <summary>所属命名空间。取 NamespaceCode 之外的语义信息（含校验消息里的完整 key）都走这里。</summary>
    public TranslationNamespace? Namespace { get; set; }

    public required string Key { get; set; }

    /// <summary>源语言默认文案（通常为 zh-CN）。</summary>
    public required string SourceText { get; set; }

    /// <summary>词条使用场景说明（供 AI 翻译与检查理解语境，例如："移动端底部提交按钮"）。</summary>
    public string? Context { get; set; }

    /// <summary>UI 最大字符长度限制（可选，例如按钮文案不超过 12 个字符）。</summary>
    public int? MaxLength { get; set; }

    /// <summary>是否由开发环境缺失词条自动上报创建。</summary>
    public bool IsAutoReported { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>各语种译文集合（Key: Culture 如 en-US, Value: 译文详情）。</summary>
    public List<TranslationValue> Translations { get; set; } = [];

    /// <summary>
    /// 所属命名空间编码（不落库，由导航属性派生）。
    /// 只有命名空间已加载或经 EF 关系修复后才有值，仅用于展示与校验消息，不参与查询条件。
    /// </summary>
    [NotMapped]
    public string NamespaceCode => Namespace?.Code ?? string.Empty;
}
