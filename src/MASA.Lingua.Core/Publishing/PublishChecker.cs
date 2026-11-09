using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;

namespace MASA.Lingua.Core.Publishing;

public sealed record PublishBlocker(string Code, string Message);

public sealed record PublishCheckReport(
    string ProjectCode,
    PublishPolicy Policy,
    int EntryCount,
    IReadOnlyList<CultureCoverage> Coverage,
    IReadOnlyList<ValidationIssue> BlockingIssues,
    IReadOnlyList<PublishBlocker> Blockers,
    int ExcludedTranslations)
{
    public bool CanPublish => Blockers.Count == 0;

    /// <summary>一句话摘要，写入发布快照用于审计。</summary>
    public string Summary => CanPublish
        ? $"通过检查（{EntryCount} 条词条，{ExcludedTranslations} 条译文因状态未达标不进发布文件）"
        : "未通过：" + string.Join("；", Blockers.Select(b => b.Message));
}

/// <summary>未通过发布门禁，且没有选择（或没有给出原因地）强制发布。</summary>
public sealed class PublishBlockedException(PublishCheckReport report, string? message = null)
    : Exception(message ?? "发布被门禁拦截：" + string.Join("；", report.Blockers.Select(b => b.Message)))
{
    public PublishCheckReport Report { get; } = report;
}

/// <summary>
/// 发布门禁：只检查「即将上线」的内容——按项目发布策略会进入发布文件的译文。
/// 被状态过滤掉的（草稿 / 待复核）不会上线，其问题不阻断发布，但会拉低上线完成度。
/// </summary>
public static class PublishChecker
{
    private static readonly DeterministicTranslationValidator Validator = new();

    public const string CoverageCode = "COVERAGE_BELOW_MIN";
    public const string ErrorsCode = "BLOCKING_ERRORS";
    public const string GlossaryCode = "GLOSSARY_VIOLATIONS";

    /// <param name="namespaces">项目自有命名空间 + 项目继承的共享命名空间（词条全集）。</param>
    /// <param name="glossary">生效术语（全局 + 项目覆盖）。</param>
    public static PublishCheckReport Check(
        Project project,
        IEnumerable<TranslationNamespace> namespaces,
        IEnumerable<GlossaryTerm> glossary)
    {
        var policy = project.PublishPolicy ?? new PublishPolicy();
        var entries = namespaces.SelectMany(n => n.Entries).ToList();
        var glossaryList = glossary.ToList();
        var coverage = CoverageCalculator.Compute(entries, project.SupportedCultures, project.DefaultCulture);

        var blockers = new List<PublishBlocker>();
        var issues = new List<ValidationIssue>();
        var excluded = 0;

        foreach (var culture in project.SupportedCultures)
        {
            if (string.Equals(culture, project.DefaultCulture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var tr = entry.Translations.FirstOrDefault(
                    t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));
                if (tr is null || string.IsNullOrWhiteSpace(tr.Text))
                {
                    continue;
                }

                if (!policy.Includes(tr.Status))
                {
                    excluded++;
                    continue;
                }

                foreach (var issue in Validator.ValidateEntry(entry, culture, tr.Text, glossaryList))
                {
                    var isGlossary = issue.RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal);
                    if (isGlossary ? policy.BlockOnGlossaryViolations : (policy.BlockOnErrors && issue.Severity == IssueSeverity.Error))
                    {
                        issues.Add(issue);
                    }
                }
            }
        }

        var errorCount = issues.Count(i => !i.RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal));
        if (errorCount > 0)
        {
            blockers.Add(new PublishBlocker(ErrorsCode,
                $"{errorCount} 处即将上线的译文含 Error 级问题（占位符 / 长度 / HTML）"));
        }

        var glossaryCount = issues.Count - errorCount;
        if (glossaryCount > 0)
        {
            blockers.Add(new PublishBlocker(GlossaryCode,
                $"{glossaryCount} 处即将上线的译文违反术语库"));
        }

        if (policy.MinCoveragePercent > 0)
        {
            foreach (var c in coverage.Where(c => !string.Equals(c.Culture, project.DefaultCulture, StringComparison.OrdinalIgnoreCase)))
            {
                var percent = c.LivePercent(policy.Content);
                if (percent < policy.MinCoveragePercent)
                {
                    blockers.Add(new PublishBlocker(CoverageCode,
                        $"{c.Culture} 上线完成度 {percent}%，低于要求的 {policy.MinCoveragePercent}%"));
                }
            }
        }

        return new PublishCheckReport(
            project.Code,
            policy,
            entries.Count,
            coverage,
            issues,
            blockers,
            excluded);
    }
}
