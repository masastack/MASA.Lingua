using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Validation;

/// <summary>
/// 术语库源文案命中逻辑（供规则校验与 AI Prompt 注入共用，保证两处判定一致）：
/// 1. 拉丁字母/数字构成的术语边界必须是词边界（"Lingua" 不会命中 "Linguaphone"）；
/// 2. 中日韩术语不依赖空格分词，按子串匹配；
/// 3. 重叠即去重（最长匹配优先）：若另一个更长的术语已经命中同一处文本（如术语表同时有「结算」与「结算中心」，或等长重叠的情况），较短术语在该处不再重复命中，但它在文本其他位置的命中不受影响；
/// 4. 大小写策略由 <see cref="GlossaryTerm.CaseSensitive"/> 决定。
/// </summary>
public static class GlossaryMatcher
{
    /// <summary>
    /// 返回在源文案中至少有一处「有效命中」的术语（保持输入顺序）。
    /// </summary>
    public static IReadOnlyList<GlossaryTerm> FindMatchedTerms(string? sourceText, IEnumerable<GlossaryTerm>? terms)
    {
        if (string.IsNullOrEmpty(sourceText) || terms is null)
        {
            return [];
        }

        var candidates = new List<(GlossaryTerm Term, int Order, List<(int Start, int End)> Spans)>();
        var order = 0;
        foreach (var term in terms)
        {
            var text = term.SourceTerm?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var spans = FindSpans(sourceText, text, term.CaseSensitive);
                if (spans.Count > 0)
                {
                    candidates.Add((term, order, spans));
                }
            }

            order++;
        }

        if (candidates.Count <= 1)
        {
            return candidates.Select(c => c.Term).ToList();
        }

        var claimed = new List<(int Start, int End)>();
        var matched = new List<(GlossaryTerm Term, int Order)>();

        foreach (var candidate in candidates.OrderByDescending(c => c.Term.SourceTerm.Trim().Length))
        {
            var free = candidate.Spans
                .Where(s => !claimed.Any(k => s.Start < k.End && k.Start < s.End))
                .ToList();

            if (free.Count == 0)
            {
                continue;
            }

            claimed.AddRange(free);
            matched.Add((candidate.Term, candidate.Order));
        }

        return matched.OrderBy(m => m.Order).Select(m => m.Term).ToList();
    }

    /// <summary>
    /// 译文里是否出现了某个词（用于禁用译法判断）：拉丁字母词按词边界匹配（禁用 "Work" 不会误伤 "Workspace"），中日韩按子串匹配。
    /// </summary>
    public static bool ContainsWord(string? text, string? word, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        return FindSpans(text, word.Trim(), caseSensitive).Count > 0;
    }

    private static List<(int Start, int End)> FindSpans(string text, string term, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var spans = new List<(int Start, int End)>();

        var index = 0;
        while (index <= text.Length - term.Length &&
               (index = text.IndexOf(term, index, comparison)) >= 0)
        {
            var end = index + term.Length;
            if (IsBoundaryValid(text, index, end, term))
            {
                spans.Add((index, end));
            }

            index++;
        }

        return spans;
    }

    private static bool IsBoundaryValid(string text, int start, int end, string term)
    {
        if (IsLatinWordChar(term[0]) && start > 0 && IsLatinWordChar(text[start - 1]))
        {
            return false;
        }

        if (IsLatinWordChar(term[^1]) && end < text.Length && IsLatinWordChar(text[end]))
        {
            return false;
        }

        return true;
    }

    private static bool IsLatinWordChar(char c) =>
        c < '\u0250' && (char.IsLetterOrDigit(c) || c == '_');
}
