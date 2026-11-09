namespace MASA.Lingua.Server.Services;

/// <summary>
/// 管理后台里的展示文案：语种的本地语言名称、校验规则码对应的中文说明。
/// </summary>
public static class UiText
{
    private static readonly Dictionary<string, string> RuleNames = new(StringComparer.Ordinal)
    {
        ["EMPTY_TRANSLATION"] = "译文缺失",
        ["PLACEHOLDER_MISSING"] = "占位符缺失",
        ["PLACEHOLDER_EXTRA"] = "多余占位符",
        ["MAX_LENGTH_EXCEEDED"] = "超出长度限制",
        ["HTML_TAG_MISMATCH"] = "HTML 标签不一致",
        ["GLOSSARY_DNT_VIOLATION"] = "品牌词被翻译",
        ["GLOSSARY_TERM_MISMATCH"] = "术语译法不一致",
    };

    /// <summary>语种的本地语言名称（未收录的语种原样返回编码）。</summary>
    public static string Locale(string culture) =>
        MASA.Lingua.Core.Localization.LinguaCultures.GetName(culture);

    /// <summary>校验规则码对应的中文说明（未收录的规则原样返回编码）。</summary>
    public static string Rule(string ruleCode) =>
        RuleNames.TryGetValue(ruleCode, out var name) ? name : ruleCode;
}
