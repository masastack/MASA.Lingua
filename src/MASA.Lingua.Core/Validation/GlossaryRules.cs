using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Validation;

/// <summary>术语对一条译文的单项违规。</summary>
public sealed record GlossaryViolation(string RuleCode, IssueSeverity Severity, string Message, string? SuggestedFix);

/// <summary>某个术语对某条译文的判定结果。</summary>
/// <param name="Applies">这个术语在该语种下是否有约束（禁止翻译 / 有标准译法 / 有禁用译法）；没有约束的不计入遵循率。</param>
/// <param name="Violations">违规列表；为空表示遵循。</param>
public sealed record GlossaryTermResult(GlossaryTerm Term, bool Applies, IReadOnlyList<GlossaryViolation> Violations)
{
    public bool Followed => Applies && Violations.Count == 0;
}

/// <summary>
/// 术语对译文的判定规则（规则校验、遵循率统计共用，保证两处结论一致）：
/// 1. 禁止翻译：译文必须原样保留源术语；
/// 2. 标准译法：译文包含标准译法或任一「可接受变体」即遵循（目标侧不加词边界，容忍英文复数 / 词形变化）；
/// 3. 禁用译法：译文出现任何禁用词即违规，即使同时包含标准译法（拉丁词按词边界，避免禁用 "Work" 误伤 "Workspace"）。
/// </summary>
public static class GlossaryRules
{
    public const string DntViolation = "GLOSSARY_DNT_VIOLATION";
    public const string TermMismatch = "GLOSSARY_TERM_MISMATCH";
    public const string ForbiddenTerm = "GLOSSARY_FORBIDDEN_TERM";

    public static GlossaryTermResult Evaluate(GlossaryTerm term, string culture, string translatedText)
    {
        var sourceTerm = term.SourceTerm.Trim();
        var comparison = term.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var violations = new List<GlossaryViolation>();
        var applies = false;

        if (term.DoNotTranslate)
        {
            applies = true;
            // 与禁用译法同口径：拉丁词按词边界（"LinguaPhone" 不算保留了 "Lingua"），中日韩按子串
            if (!GlossaryMatcher.ContainsWord(translatedText, sourceTerm, term.CaseSensitive))
            {
                violations.Add(new GlossaryViolation(
                    DntViolation,
                    IssueSeverity.Error,
                    $"专有术语 \"{sourceTerm}\" 标记为禁止翻译，但译文中未原样保留。",
                    sourceTerm));
            }

            return new GlossaryTermResult(term, applies, violations);
        }

        var expected = Lookup(term.TargetTranslations, culture);
        var variants = LookupList(term.AcceptedVariants, culture);
        var forbidden = LookupList(term.ForbiddenTranslations, culture);

        if (!string.IsNullOrWhiteSpace(expected))
        {
            applies = true;
            var followed = translatedText.Contains(expected, comparison) ||
                           variants.Any(v => !string.IsNullOrWhiteSpace(v) && translatedText.Contains(v, comparison));
            if (!followed)
            {
                var alt = variants.Count > 0 ? $"（可接受变体：{string.Join("、", variants)}）" : "";
                violations.Add(new GlossaryViolation(
                    TermMismatch,
                    IssueSeverity.Warning,
                    $"源文案包含术语 \"{sourceTerm}\"，建议在 [{culture}] 中使用标准译法 \"{expected}\"{alt}。",
                    expected));
            }
        }

        if (forbidden.Count > 0)
        {
            applies = true;
            foreach (var word in forbidden.Where(w => !string.IsNullOrWhiteSpace(w)))
            {
                if (GlossaryMatcher.ContainsWord(translatedText, word, term.CaseSensitive))
                {
                    var hint = string.IsNullOrWhiteSpace(expected) ? "" : $"，请使用 \"{expected}\"";
                    violations.Add(new GlossaryViolation(
                        ForbiddenTerm,
                        IssueSeverity.Error,
                        $"术语 \"{sourceTerm}\" 在 [{culture}] 中禁止译为 \"{word.Trim()}\"{hint}。",
                        string.IsNullOrWhiteSpace(expected) ? null : expected));
                }
            }
        }

        return new GlossaryTermResult(term, applies, violations);
    }

    private static string? Lookup(Dictionary<string, string>? dict, string culture)
    {
        if (dict is null)
        {
            return null;
        }

        foreach (var (k, v) in dict)
        {
            if (string.Equals(k, culture, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    private static List<string> LookupList(Dictionary<string, List<string>>? dict, string culture)
    {
        if (dict is null)
        {
            return [];
        }

        foreach (var (k, v) in dict)
        {
            if (string.Equals(k, culture, StringComparison.OrdinalIgnoreCase))
            {
                return v ?? [];
            }
        }

        return [];
    }
}
