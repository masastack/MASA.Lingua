using System.Text.RegularExpressions;
using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Validation;

public enum IssueSeverity
{
    Warning = 0,
    Error = 1
}

public sealed record ValidationIssue(
    string EntryKey,
    string Culture,
    string RuleCode,
    IssueSeverity Severity,
    string Message,
    string? SuggestedFix = null)
{
    /// <summary>
    /// 阻断级问题：Error 级规则或任意术语库违规。含阻断问题的译文不应被视为「已通过」。
    /// </summary>
    public bool IsBlocking =>
        Severity == IssueSeverity.Error ||
        RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal);
}

/// <summary>
/// 第一阶段：零成本确定性规则检查引擎（保存词条或 AI 翻译完成后毫秒级运行）
/// 检查项：
/// 1. 命名/位置占位符一致性（如 {userName}, {count}, {0}）
/// 2. 最大长度限制（MaxLength）
/// 3. HTML 标签闭合与匹配
/// 4. 术语库（Glossary）强制译法与禁止翻译词检查
/// 5. 首尾空白字符或空译文检查
/// </summary>
public sealed partial class DeterministicTranslationValidator
{
    [GeneratedRegex(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"</?([a-zA-Z][a-zA-Z0-9]*)\b[^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    public IReadOnlyList<ValidationIssue> ValidateEntry(
        TranslationEntry entry,
        string targetCulture,
        string? translatedText,
        IEnumerable<GlossaryTerm>? glossaryTerms = null) =>
        ValidateCore(entry, targetCulture, translatedText, glossaryTerms, matchedTerms: null);

    /// <summary>
    /// 同 <see cref="ValidateEntry"/>，但源文案命中的术语由调用方给出（<see cref="GlossaryMatcher.FindMatchedTerms"/> 的结果）。
    /// 命中只取决于源文案与术语库、与目标语种无关：同一词条要校验多个语种时，调用方匹配一次、各语种复用，
    /// 不必每个语种重新跑一遍术语匹配（词条多、术语多时这是校验里最贵的一段）。
    /// 单独起名而不是加重载：<c>IEnumerable&lt;GlossaryTerm&gt;</c> 与 <c>IReadOnlyList&lt;GlossaryTerm&gt;</c>
    /// 重载会让传入 <c>List&lt;GlossaryTerm&gt;</c> 的旧调用悄悄改变语义（把整个术语库当成命中结果）。
    /// </summary>
    public IReadOnlyList<ValidationIssue> ValidateEntryWithMatches(
        TranslationEntry entry,
        string targetCulture,
        string? translatedText,
        IReadOnlyList<GlossaryTerm> matchedTerms) =>
        ValidateCore(entry, targetCulture, translatedText, glossaryTerms: null, matchedTerms);

    private static IReadOnlyList<ValidationIssue> ValidateCore(
        TranslationEntry entry,
        string targetCulture,
        string? translatedText,
        IEnumerable<GlossaryTerm>? glossaryTerms,
        IReadOnlyList<GlossaryTerm>? matchedTerms)
    {
        var issues = new List<ValidationIssue>();
        // 根命名空间（NamespaceCode 为空）下不能拼出 ".RoleNotExist" 这种带前导点的 key：
        // 与 Home.razor 的 FormatKey 保持一致，空命名空间直接用裸 key。
        var fullKey = string.IsNullOrWhiteSpace(entry.NamespaceCode)
            ? entry.Key
            : $"{entry.NamespaceCode}.{entry.Key}";

        if (string.IsNullOrWhiteSpace(translatedText))
        {
            issues.Add(new ValidationIssue(
                fullKey,
                targetCulture,
                "EMPTY_TRANSLATION",
                IssueSeverity.Warning,
                $"目标语言 [{targetCulture}] 尚未提供译文。"));
            return issues;
        }

        // 源文案未知（只导入过非默认语种、还没人补源文案）时，占位符与 HTML 无从比对：
        // 强行比对会把译文的每个占位符都报成「多出占位符」，直接跳过后续校验。
        if (string.IsNullOrWhiteSpace(entry.SourceText))
        {
            return issues;
        }

        var sourcePlaceholders = ExtractPlaceholders(entry.SourceText);
        var targetPlaceholders = ExtractPlaceholders(translatedText);

        var missingPlaceholders = sourcePlaceholders.Except(targetPlaceholders, StringComparer.Ordinal).ToList();
        var extraPlaceholders = targetPlaceholders.Except(sourcePlaceholders, StringComparer.Ordinal).ToList();

        if (missingPlaceholders.Count > 0)
        {
            issues.Add(new ValidationIssue(
                fullKey,
                targetCulture,
                "PLACEHOLDER_MISSING",
                IssueSeverity.Error,
                $"译文遗漏了占位符: {string.Join(", ", missingPlaceholders.Select(p => $"{{{p}}}"))}"));
        }

        if (extraPlaceholders.Count > 0)
        {
            issues.Add(new ValidationIssue(
                fullKey,
                targetCulture,
                "PLACEHOLDER_EXTRA",
                IssueSeverity.Error,
                $"译文包含了源文案不存在的未知占位符: {string.Join(", ", extraPlaceholders.Select(p => $"{{{p}}}"))}"));
        }

        if (entry.MaxLength is > 0 && translatedText.Length > entry.MaxLength.Value)
        {
            issues.Add(new ValidationIssue(
                fullKey,
                targetCulture,
                "MAX_LENGTH_EXCEEDED",
                IssueSeverity.Error,
                $"译文长度 ({translatedText.Length}) 超出 UI 最大长度限制 ({entry.MaxLength.Value})。"));
        }

        var sourceTags = ExtractHtmlTags(entry.SourceText);
        var targetTags = ExtractHtmlTags(translatedText);
        if (!sourceTags.SequenceEqual(targetTags, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(
                fullKey,
                targetCulture,
                "HTML_TAG_MISMATCH",
                IssueSeverity.Error,
                $"HTML 标签结构不一致。源标签: [{string.Join(", ", sourceTags)}]，译文标签: [{string.Join(", ", targetTags)}]。"));
        }

        if (matchedTerms is not null || glossaryTerms is not null)
        {
            // 源文案命中判定（词边界 + 最长匹配优先 + 大小写策略）与 AI Prompt 注入共用 GlossaryMatcher
            // 判定规则（禁止翻译 / 标准译法 + 可接受变体 / 禁用译法）与遵循率统计共用 GlossaryRules
            foreach (var term in matchedTerms ?? GlossaryMatcher.FindMatchedTerms(entry.SourceText, glossaryTerms))
            {
                foreach (var v in GlossaryRules.Evaluate(term, targetCulture, translatedText).Violations)
                {
                    issues.Add(new ValidationIssue(fullKey, targetCulture, v.RuleCode, v.Severity, v.Message, v.SuggestedFix));
                }
            }
        }

        return issues;
    }

    public static HashSet<string> ExtractPlaceholders(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return set;
        }

        foreach (Match match in PlaceholderRegex().Matches(text))
        {
            set.Add(match.Groups[1].Value);
        }
        return set;
    }

    private static List<string> ExtractHtmlTags(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        foreach (Match match in HtmlTagRegex().Matches(text))
        {
            list.Add(match.Value);
        }
        return list;
    }
}
