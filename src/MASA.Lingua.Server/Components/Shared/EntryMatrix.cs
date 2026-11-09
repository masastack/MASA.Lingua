using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Server.Components.Shared;

/// <summary>
/// 词条矩阵里「一行词条」的纯函数口径：页面（保存 / AI 翻译）与 <see cref="EntryRow"/>（渲染）共用同一份实现，
/// 避免「渲染按一种口径、保存按另一种」。都只依赖词条本身和显式传入的语种，不读页面状态。
/// </summary>
internal static class EntryMatrix
{
    /// <summary>这条词条里「缺失 / 空白 / 草稿 / 待复核」的目标语种（「补全缺失」会处理的范围）。</summary>
    public static IReadOnlyList<string> PendingAiCultures(TranslationEntry entry, IReadOnlyList<string> targetCultures) =>
        targetCultures.Where(c => NeedsAi(entry, c)).ToList();

    /// <summary>
    /// 仅「缺失 / 空白 / 草稿 / 待复核」的译文需要 AI 处理；已审核（Approved）与 AI 已通过（AiTranslated）的译文不会被覆盖。
    /// </summary>
    public static bool NeedsAi(TranslationEntry entry, string culture) =>
        EntryTranslations.Find(entry, culture) is not { } tr
        || string.IsNullOrWhiteSpace(tr.Text)
        || tr.Status is TranslationStatus.Draft or TranslationStatus.NeedsReview;

    /// <summary>
    /// 「重新翻译全部」会覆盖的目标语种：已经人工审核（Approved）且有内容的译文。
    /// 这决定了点下去要不要弹确认框，行上也会据此显示提示。
    /// </summary>
    public static IReadOnlyList<string> ApprovedCultures(TranslationEntry entry, IReadOnlyList<string> targetCultures) =>
        targetCultures
            .Where(c => EntryTranslations.Find(entry, c) is { Status: TranslationStatus.Approved } tr && !string.IsNullOrWhiteSpace(tr.Text))
            .ToList();

    /// <summary>「重新翻译全部」的悬停说明：点名会被覆盖的语种，让用户点之前就知道这一步会弹确认。</summary>
    public static string OverwriteTitle(IReadOnlyList<string> approvedCultures) =>
        approvedCultures.Count > 0
            ? $"用 AI 重新翻译全部非源语种；会覆盖 {approvedCultures.Count} 项已人工审核的译文（{string.Join("、", approvedCultures)}），点击后先确认再执行"
            : "用 AI 重新翻译全部非源语种；当前没有已人工审核的译文，会直接覆盖 AI 已译内容，不再确认";

    public static string StatusKey(TranslationValue? tr, bool missing) =>
        missing
            ? "missing"
            : tr?.Status switch
            {
                TranslationStatus.AiTranslated => "ai",
                TranslationStatus.NeedsReview or TranslationStatus.Draft => "review",
                _ => "approved"
            };

    /// <summary>
    /// 词条任一语种译文处于「待复核」——现存的两个来源：AI 译文含阻断级问题（占位符 / 长度 / HTML / 术语），
    /// 以及源文案变更的 fuzzy 标记。「仅待复核」筛选与确认操作都以此为准。
    /// </summary>
    public static bool HasNeedsReview(TranslationEntry entry) =>
        entry.Translations.Any(t => t.Status == TranslationStatus.NeedsReview);

    /// <summary>词条展示名：项目根（空命名空间）的词条不显示前缀，其余为「命名空间.Key」。</summary>
    public static string DisplayKey(TranslationEntry entry) =>
        string.IsNullOrEmpty(entry.NamespaceCode) ? entry.Key : $"{entry.NamespaceCode}.{entry.Key}";
}
