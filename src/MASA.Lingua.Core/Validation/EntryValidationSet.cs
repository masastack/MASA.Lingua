using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Validation;

/// <summary>
/// 一轮校验的完整结果：纯数据，可以在后台线程算好后一次性换进调用方状态。
/// <para>
/// 三张表各司其职：<see cref="MatchedTerms"/> 供术语遵循率复用（命中只取决于源文案，与语种无关）；
/// <see cref="IssuesByEntry"/> 供检查报告按词条聚合；<see cref="Lookup"/> 供矩阵单元格查备注。
/// </para>
/// </summary>
public sealed record EntryValidationResult(
    Dictionary<Guid, IReadOnlyList<GlossaryTerm>> MatchedTerms,
    Dictionary<Guid, IReadOnlyList<ValidationIssue>> IssuesByEntry,
    Dictionary<(Guid EntryId, string Culture), List<ValidationIssue>> Lookup);

/// <summary>
/// 词条矩阵的规则校验口径：与发布门禁（<see cref="Publishing.PublishChecker"/>）使用同一个
/// <see cref="DeterministicTranslationValidator"/>，只是额外按词条 / 语种建索引以便页面查表。
/// </summary>
public static class EntryValidationSet
{
    /// <summary>词条数不超过这个值时调用方可在当前线程直接算（线程切换的开销比计算本身还大）。</summary>
    public const int InlineThreshold = 400;

    /// <summary>全量校验：逐词条、逐语种算一遍，并建好三张表。</summary>
    public static EntryValidationResult Compute(
        DeterministicTranslationValidator validator,
        IReadOnlyList<TranslationEntry> entries,
        IReadOnlyList<string> cultures,
        string defaultCulture,
        IReadOnlyList<GlossaryTerm> glossary)
    {
        var matched = new Dictionary<Guid, IReadOnlyList<GlossaryTerm>>(entries.Count);
        var issuesByEntry = new Dictionary<Guid, IReadOnlyList<ValidationIssue>>(entries.Count);
        var lookup = new Dictionary<(Guid, string), List<ValidationIssue>>();
        foreach (var entry in entries)
        {
            var (terms, issues) = CheckOne(validator, entry, cultures, defaultCulture, glossary, lookup);
            matched[entry.Id] = terms;
            issuesByEntry[entry.Id] = issues;
        }

        return new EntryValidationResult(matched, issuesByEntry, lookup);
    }

    /// <summary>
    /// 单条词条的全部语种校验。源文案命中的术语只匹配一次、各语种复用（词条多、术语多时这是校验里最贵的一段）；
    /// 单元格备注（EMPTY_TRANSLATION 之外的问题）写入 <paramref name="lookup"/>，没有备注的格子不留旧值。
    /// </summary>
    public static (IReadOnlyList<GlossaryTerm> Terms, IReadOnlyList<ValidationIssue> Issues) CheckOne(
        DeterministicTranslationValidator validator,
        TranslationEntry entry,
        IReadOnlyList<string> cultures,
        string defaultCulture,
        IReadOnlyList<GlossaryTerm> glossary,
        Dictionary<(Guid, string), List<ValidationIssue>> lookup)
    {
        IReadOnlyList<GlossaryTerm> terms = glossary.Count == 0
            ? []
            : GlossaryMatcher.FindMatchedTerms(entry.SourceText, glossary);

        var issues = new List<ValidationIssue>();
        foreach (var culture in cultures)
        {
            var text = EntryTranslations.Text(entry, culture, defaultCulture);
            var cellIssues = validator.ValidateEntryWithMatches(entry, culture, text, terms);
            issues.AddRange(cellIssues);

            // 单元格备注不重复报告「还没有译文」：那是完成度问题，矩阵里已经用「未翻译」占位符表示
            var notes = cellIssues.Where(i => i.RuleCode != "EMPTY_TRANSLATION").ToList();
            if (notes.Count > 0)
            {
                lookup[(entry.Id, culture)] = notes;
            }
            else
            {
                lookup.Remove((entry.Id, culture));
            }
        }

        return (terms, issues);
    }
}
