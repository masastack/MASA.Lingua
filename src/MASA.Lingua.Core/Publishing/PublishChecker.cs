using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Serialization;
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
    public const string PathConflictCode = "KEY_PATH_CONFLICT";

    /// <summary>键路径冲突在门禁摘要里最多逐条列出多少处；超出部分汇总成一条，避免摘要被极端数据撑爆。</summary>
    public const int PathConflictReportLimit = 5;

    /// <param name="namespaces">项目自有命名空间 + 项目继承的共享命名空间（词条全集，共享在前项目在后，与发布图层顺序一致）。</param>
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

        // 键路径冲突是结构性的（与语种无关）：同一路径既是词条值又是命名空间对象时，嵌套发布 JSON
        // 只能保留一侧（扁平文件两侧都保留）。默认阻断；消费端用扁平格式的项目可按策略关闭本检查。
        if (policy.BlockOnKeyPathConflicts)
        {
            var pathConflicts = DetectPathConflicts(namespaces);
            foreach (var conflict in pathConflicts.Take(PathConflictReportLimit))
            {
                blockers.Add(new PublishBlocker(PathConflictCode, Describe(conflict)));
            }

            if (pathConflicts.Count > PathConflictReportLimit)
            {
                blockers.Add(new PublishBlocker(PathConflictCode,
                    $"另有 {pathConflicts.Count - PathConflictReportLimit} 处键路径冲突，发布文件同样会丢失其中一侧的内容"));
            }
        }

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

    /// <summary>
    /// 按发布口径合并词条全集后做键路径检测：同 (命名空间, Key) 的词条（共享层被项目层覆盖）只算一条，
    /// 与发布图层合并语义一致，覆盖不会被误报为冲突。
    /// </summary>
    private static IReadOnlyList<JsonBundleConverter.KeyPathConflict> DetectPathConflicts(
        IEnumerable<TranslationNamespace> namespaces)
    {
        var merged = new HashSet<(string Namespace, string Key)>();
        foreach (var ns in namespaces)
        {
            foreach (var entry in ns.Entries)
            {
                merged.Add((ns.Code, entry.Key));
            }
        }

        return JsonBundleConverter.DetectPathConflicts(merged);
    }

    private static string Describe(JsonBundleConverter.KeyPathConflict conflict)
    {
        var values = DescribeClaims(conflict.ValueClaims);

        // 同一路径被两个不同词条都当作「值」：完全重复，发布文件只保留其中一个
        if (conflict.BranchClaims.Count == 0)
        {
            return $"键路径冲突：\"{conflict.JsonPath}\" 被词条 {values} 重复占用，发布文件只能保留其一，其余词条的文案会丢失";
        }

        var branches = DescribeClaims(conflict.BranchClaims);
        return $"键路径冲突：\"{conflict.JsonPath}\" 既是词条 {values} 的文案位置，又是词条 {branches} 的父级对象；"
            + "发布 JSON 只能保留对象，词条的文案不会进入发布文件";
    }

    private static string DescribeClaims(IReadOnlyList<(string Namespace, string Key)> claims)
    {
        static string Display((string Namespace, string Key) claim) =>
            string.IsNullOrWhiteSpace(claim.Namespace) ? $"「项目根/{claim.Key}」" : $"「{claim.Namespace}/{claim.Key}」";

        var text = string.Join("", claims.Take(3).Select(Display));
        return claims.Count > 3 ? text + " 等" : text;
    }
}
