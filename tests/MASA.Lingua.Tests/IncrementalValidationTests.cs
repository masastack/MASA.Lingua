using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 词条矩阵的增量校验建立在三块「可复用中间结果」之上：源文案命中的术语（每词条只算一次）、
/// 带命中结果的单格校验、带命中结果的遵循率统计。它们必须与「每格重新匹配术语」的全量算法逐项一致，
/// 否则页面上增量刷新出来的结果会和发布门禁 / 全量重算对不上。
/// </summary>
public sealed class IncrementalValidationTests
{
    private static readonly DeterministicTranslationValidator Validator = new();

    private static readonly string[] Cultures = ["zh-CN", "en-US", "ja-JP"];

    private static List<GlossaryTerm> Glossary() =>
    [
        new() { SourceTerm = "Lingua", DoNotTranslate = true, CaseSensitive = true },
        new()
        {
            SourceTerm = "工作台",
            TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace", ["ja-JP"] = "ワークスペース" },
            ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Dashboard"] },
            AcceptedVariants = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Work space"] }
        },
        // 重叠术语：较长的「结算中心」命中处，较短的「结算」不再重复命中
        new() { SourceTerm = "结算", TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Checkout" } },
        new() { SourceTerm = "结算中心", TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Settlement Center" } },
    ];

    /// <summary>覆盖各类规则的词条：占位符缺失/多余、超长、HTML、术语遵循/违反、空译文、无源文案。</summary>
    private static List<TranslationEntry> Entries()
    {
        var ns = new TranslationNamespace { Code = "page", ProjectId = Guid.NewGuid() };

        TranslationEntry Make(string key, string source, int? maxLength, params (string Culture, string Text, TranslationStatus Status)[] translations)
        {
            var entry = new TranslationEntry { Key = key, SourceText = source, MaxLength = maxLength, Namespace = ns };
            entry.NamespaceId = ns.Id;
            foreach (var (culture, text, status) in translations)
            {
                entry.Translations.Add(new TranslationValue { EntryId = entry.Id, Culture = culture, Text = text, Status = status });
            }

            ns.Entries.Add(entry);
            return entry;
        }

        Make("plain", "保存", null,
            ("en-US", "Save", TranslationStatus.Approved), ("ja-JP", "保存", TranslationStatus.AiTranslated));
        Make("placeholder", "你好 {name}，共 {count} 条", null,
            ("en-US", "Hello {name}", TranslationStatus.Approved), ("ja-JP", "こんにちは {name} {extra}", TranslationStatus.Approved));
        Make("tooLong", "提交订单", 6,
            ("en-US", "Submit your order now", TranslationStatus.Approved));
        Make("html", "点击<b>这里</b>", null,
            ("en-US", "Click <b>here", TranslationStatus.AiTranslated), ("ja-JP", "<b>ここ</b>をクリック", TranslationStatus.Approved));
        Make("brand", "欢迎使用 Lingua 平台", null,
            ("en-US", "Welcome to Linguo", TranslationStatus.Approved), ("ja-JP", "Linguaへようこそ", TranslationStatus.Approved));
        Make("glossaryOk", "进入工作台", null,
            ("en-US", "Open the Workspace", TranslationStatus.Approved), ("ja-JP", "ワークスペースを開く", TranslationStatus.Approved));
        Make("glossaryBad", "进入工作台", null,
            ("en-US", "Open the Dashboard (Workspace)", TranslationStatus.Approved), ("ja-JP", "ダッシュボードを開く", TranslationStatus.Draft));
        Make("overlap", "前往结算中心完成结算", null,
            ("en-US", "Go to Settlement Center and Checkout", TranslationStatus.Approved));
        Make("emptyTranslation", "待翻译", null,
            ("en-US", "", TranslationStatus.Draft));
        Make("missingTranslation", "没有译文", null);
        Make("noSource", "", null,
            ("en-US", "Only translated", TranslationStatus.Approved));

        return ns.Entries;
    }

    [Fact]
    public void Validation_With_Precomputed_Matches_Is_Identical_To_Per_Cell_Matching()
    {
        var glossary = Glossary();
        var entries = Entries();
        var checkedCells = 0;
        var issueTotal = 0;

        foreach (var entry in entries)
        {
            // 匹配只取决于源文案：每词条算一次，各语种复用
            var matched = GlossaryMatcher.FindMatchedTerms(entry.SourceText, glossary);

            foreach (var culture in Cultures.Where(c => c != "zh-CN"))
            {
                var text = entry.Translations.FirstOrDefault(t => t.Culture == culture)?.Text;
                var full = Validator.ValidateEntry(entry, culture, text, glossary);
                var incremental = Validator.ValidateEntryWithMatches(entry, culture, text, matched);

                Assert.Equal(full, incremental);
                checkedCells++;
                issueTotal += full.Count;
            }
        }

        // 数据确实覆盖了规则：有足够多的格子，也确实产生了各类问题（防止测试空转）
        Assert.Equal(entries.Count * 2, checkedCells);
        Assert.True(issueTotal >= 8, $"expected a rich set of issues, got {issueTotal}");
        var codes = entries
            .SelectMany(e => Cultures.Where(c => c != "zh-CN").SelectMany(c =>
                Validator.ValidateEntry(e, c, e.Translations.FirstOrDefault(t => t.Culture == c)?.Text, glossary)))
            .Select(i => i.RuleCode)
            .ToHashSet();
        Assert.Contains("PLACEHOLDER_MISSING", codes);
        Assert.Contains("PLACEHOLDER_EXTRA", codes);
        Assert.Contains("MAX_LENGTH_EXCEEDED", codes);
        Assert.Contains("EMPTY_TRANSLATION", codes);
        Assert.Contains(codes, c => c.StartsWith("GLOSSARY_", StringComparison.Ordinal));
    }

    [Fact]
    public void Compliance_From_Cached_Matches_Equals_The_Full_Computation()
    {
        var glossary = Glossary();
        var entries = Entries();

        var full = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", glossary);

        // 页面长期持有的命中表：只在源文案或术语库变化时重建
        var matches = entries.ToDictionary(e => e.Id, e => GlossaryMatcher.FindMatchedTerms(e.SourceText, glossary));
        var cached = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", matches);

        Assert.Equal(full, cached);
        Assert.Contains(full, c => c.Checked > 0);
        Assert.Contains(full, c => c.Followed < c.Checked); // 有违规，遵循率才有意义

        // 缺项视为无命中；空术语库时所有语种都是 0 / 0（页面据此不显示遵循率）
        var none = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", new Dictionary<Guid, IReadOnlyList<GlossaryTerm>>());
        Assert.All(none, c => Assert.Equal((0, 0), (c.Checked, c.Followed)));
        Assert.All(none, c => Assert.Null(c.Percent));
    }

    [Fact]
    public void Editing_One_Entry_Only_Changes_That_Entrys_Contribution()
    {
        var glossary = Glossary();
        var entries = Entries();
        var matches = entries.ToDictionary(e => e.Id, e => GlossaryMatcher.FindMatchedTerms(e.SourceText, glossary));

        var before = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", matches)
            .Single(c => c.Culture == "en-US");

        // 把违反术语的 glossaryBad 改成遵循：只有这一格的贡献变化，命中表不用重建
        var target = entries.Single(e => e.Key == "glossaryBad");
        target.Translations.Single(t => t.Culture == "en-US").Text = "Open the Workspace";

        var after = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", matches)
            .Single(c => c.Culture == "en-US");
        Assert.Equal(before.Checked, after.Checked);
        Assert.Equal(before.Followed + 1, after.Followed);

        // 与「重建命中表」的全量结果一致
        var rebuilt = GlossaryComplianceCalculator.Compute(entries, Cultures, "zh-CN", glossary)
            .Single(c => c.Culture == "en-US");
        Assert.Equal(rebuilt, after);

        // 单格校验：违规消失
        var issues = Validator.ValidateEntryWithMatches(target, "en-US", "Open the Workspace", matches[target.Id]);
        Assert.DoesNotContain(issues, i => i.RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal));
    }

    [Fact]
    public void Coverage_Per_Culture_Matches_The_Combined_Computation()
    {
        var entries = Entries();

        var combined = CoverageCalculator.Compute(entries, Cultures, "zh-CN");
        foreach (var culture in Cultures)
        {
            Assert.Equal(
                combined.Single(c => c.Culture == culture),
                CoverageCalculator.ComputeCulture(entries, culture, "zh-CN"));
        }

        var en = combined.Single(c => c.Culture == "en-US");
        Assert.Equal(entries.Count, en.Total);
        Assert.Equal(en.Total, en.Approved + en.Ai + en.Review + en.Missing);
    }
}
