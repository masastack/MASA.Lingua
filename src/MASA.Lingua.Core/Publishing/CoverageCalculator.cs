using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Publishing;

/// <summary>
/// 单个语种的完成度。Total 为词条总数；Approved / Ai / Review / Missing 互斥，合计等于 Total。
/// </summary>
public sealed record CultureCoverage(string Culture, int Total, int Approved, int Ai, int Review, int Missing)
{
    /// <summary>按发布策略，会进入发布文件的译文数。</summary>
    public int LiveCount(PublishContentMode mode) => mode switch
    {
        PublishContentMode.ApprovedOnly => Approved,
        PublishContentMode.ApprovedAndAi => Approved + Ai,
        _ => Approved + Ai + Review
    };

    /// <summary>上线完成度（0-100，向下取整）；没有词条时视为 100。</summary>
    public int LivePercent(PublishContentMode mode) =>
        Total == 0 ? 100 : (int)Math.Floor(LiveCount(mode) * 100.0 / Total);
}

/// <summary>
/// 语种完成度计算：管理后台矩阵、发布前检查、manifest 与 CLI 共用同一套口径。
/// 已审核 / AI 已译 / 待复核（含草稿）/ 缺失；默认语种没有译文行时按源文案计入已审核。
/// </summary>
public static class CoverageCalculator
{
    public static IReadOnlyList<CultureCoverage> Compute(
        IEnumerable<TranslationEntry> entries,
        IEnumerable<string> cultures,
        string defaultCulture)
    {
        var list = entries as IReadOnlyCollection<TranslationEntry> ?? entries.ToList();
        return cultures.Select(culture => ComputeCulture(list, culture, defaultCulture)).ToList();
    }

    public static CultureCoverage ComputeCulture(
        IReadOnlyCollection<TranslationEntry> entries,
        string culture,
        string defaultCulture)
    {
        var isDefault = string.Equals(culture, defaultCulture, StringComparison.OrdinalIgnoreCase);
        int approved = 0, ai = 0, review = 0, missing = 0;

        foreach (var entry in entries)
        {
            var tr = entry.Translations.FirstOrDefault(
                t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));

            var text = tr?.Text;
            if (tr is null && isDefault)
            {
                text = entry.SourceText;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                missing++;
                continue;
            }

            switch (tr?.Status)
            {
                case TranslationStatus.AiTranslated:
                    ai++;
                    break;
                case TranslationStatus.NeedsReview or TranslationStatus.Draft:
                    review++;
                    break;
                default:
                    approved++;
                    break;
            }
        }

        return new CultureCoverage(culture, entries.Count, approved, ai, review, missing);
    }
}
