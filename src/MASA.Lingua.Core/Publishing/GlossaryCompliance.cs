using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;

namespace MASA.Lingua.Core.Publishing;

/// <summary>
/// 某个语种的术语遵循情况。统计单位是「词条 × 命中的术语」：
/// 源文案命中了某个对该语种有约束的术语，就记一次检查；译文遵循了（保留 / 标准译法 / 可接受变体，且没有出现禁用译法）记一次遵循。
/// </summary>
public sealed record GlossaryCompliance(string Culture, int Checked, int Followed)
{
    /// <summary>遵循率（0-100，向下取整）；没有任何受约束的术语命中时为 null。</summary>
    public int? Percent => Checked == 0 ? null : (int)Math.Floor(Followed * 100.0 / Checked);
}

/// <summary>术语遵循率计算：命中与判定都复用规则校验使用的 <see cref="GlossaryMatcher"/> 与 <see cref="GlossaryRules"/>。</summary>
public static class GlossaryComplianceCalculator
{
    public static IReadOnlyList<GlossaryCompliance> Compute(
        IEnumerable<TranslationEntry> entries,
        IEnumerable<string> cultures,
        string defaultCulture,
        IReadOnlyList<GlossaryTerm> glossary)
    {
        var list = entries as IReadOnlyCollection<TranslationEntry> ?? entries.ToList();

        // 源文案命中的术语与文化无关，每条词条只匹配一次
        var matched = new Dictionary<Guid, IReadOnlyList<GlossaryTerm>>();
        if (glossary.Count > 0)
        {
            foreach (var entry in list)
            {
                matched[entry.Id] = GlossaryMatcher.FindMatchedTerms(entry.SourceText, glossary);
            }
        }

        return Compute(list, cultures, defaultCulture, matched);
    }

    /// <summary>
    /// 同 <see cref="Compute(IEnumerable{TranslationEntry}, IEnumerable{string}, string, IReadOnlyList{GlossaryTerm})"/>，
    /// 但词条源文案命中的术语由调用方按词条 Id 给出（缺项视为无命中）。
    /// 调用方长期持有命中结果（例如管理页只在源文案或术语库变化时重新匹配），重算遵循率时就只剩廉价的判定，不再跑术语匹配。
    /// </summary>
    public static IReadOnlyList<GlossaryCompliance> Compute(
        IEnumerable<TranslationEntry> entries,
        IEnumerable<string> cultures,
        string defaultCulture,
        IReadOnlyDictionary<Guid, IReadOnlyList<GlossaryTerm>> matchedTermsByEntryId)
    {
        var list = entries as IReadOnlyCollection<TranslationEntry> ?? entries.ToList();
        var result = new List<GlossaryCompliance>();

        foreach (var culture in cultures)
        {
            if (string.Equals(culture, defaultCulture, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 源语种就是术语的来源，不统计
            }

            int total = 0, followed = 0;
            foreach (var entry in list)
            {
                if (!matchedTermsByEntryId.TryGetValue(entry.Id, out var terms) || terms.Count == 0)
                {
                    continue;
                }

                var tr = entry.Translations.FirstOrDefault(t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));
                if (tr is null || string.IsNullOrWhiteSpace(tr.Text))
                {
                    continue; // 还没有译文：属于完成度问题，不算术语违规
                }

                foreach (var term in terms)
                {
                    var r = GlossaryRules.Evaluate(term, culture, tr.Text);
                    if (!r.Applies)
                    {
                        continue;
                    }

                    total++;
                    if (r.Followed)
                    {
                        followed++;
                    }
                }
            }

            result.Add(new GlossaryCompliance(culture, total, followed));
        }

        return result;
    }
}

/// <summary>共享词条的影响面：被多少项目继承、其中多少项目已用自己的同名词条覆盖。</summary>
public sealed record SharedEntryImpact(int InheritingProjects, int OverridingProjects, IReadOnlyList<string> OverridingProjectCodes);

public static class SharedImpact
{
    /// <summary>
    /// 计算共享命名空间 <paramref name="namespaceCode"/> 下词条 <paramref name="entryKey"/> 的影响面。
    /// 继承 = 项目的 SharedNamespaceCodes 含该命名空间；覆盖 = 继承的项目在自己的同名命名空间里有同名 Key（项目层优先于共享层）。
    /// 传入 <see cref="ProjectImpact"/> 轻量投影（命名空间编码 + 词条 Key）即可，不需要加载词条文案与译文。
    /// </summary>
    public static SharedEntryImpact Compute(IEnumerable<ProjectImpact> projects, string namespaceCode, string entryKey)
    {
        var inheriting = 0;
        var overriding = new List<string>();

        foreach (var project in projects)
        {
            if (!project.SharedNamespaceCodes.Contains(namespaceCode, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            inheriting++;
            var overridden = project.Namespaces.Any(n =>
                !n.IsShared &&
                string.Equals(n.Code, namespaceCode, StringComparison.OrdinalIgnoreCase) &&
                n.EntryKeys.Contains(entryKey)); // 哈希集合（Ordinal）O(1)；带 comparer 的扩展方法会退化成线性扫描
            if (overridden)
            {
                overriding.Add(project.Code);
            }
        }

        return new SharedEntryImpact(inheriting, overriding.Count, overriding);
    }
}
