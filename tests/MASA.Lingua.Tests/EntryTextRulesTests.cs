using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Components.Shared;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 从页面里抽出来的纯口径：<see cref="EntryTranslations"/>（译文文本怎么取）与
/// <see cref="EntryValidationSet"/>（校验一遍怎么算、索引怎么建）、<see cref="EntryMatrix"/>（AI 相关判定）。
/// 这些是页面渲染、单格保存、检查报告共用的地基，口径错一次就会三处不一致，所以逐条钉住。
/// </summary>
public sealed class EntryTextRulesTests
{
    private static TranslationEntry Entry(
        string key = "k",
        string source = "源文案",
        string? namespaceCode = "order",
        int? maxLength = null,
        params (string Culture, string Text, TranslationStatus Status)[] translations)
    {
        var entry = new TranslationEntry
        {
            Key = key,
            SourceText = source,
            MaxLength = maxLength,
            Namespace = namespaceCode is null ? null : new TranslationNamespace { Code = namespaceCode }
        };
        foreach (var (culture, text, status) in translations)
        {
            entry.Translations.Add(new TranslationValue { EntryId = entry.Id, Culture = culture, Text = text, Status = status });
        }

        return entry;
    }

    // ---------------------------------------------------------------------
    // EntryTranslations：显示 / 校验 / 保存三处必须同一口径
    // ---------------------------------------------------------------------

    [Fact]
    public void Translation_Lookup_Is_Case_Insensitive_And_Missing_Returns_Null()
    {
        var entry = Entry(translations: [("en-US", "Save", TranslationStatus.Approved)]);

        Assert.Same(entry.Translations[0], EntryTranslations.Find(entry, "en-US"));
        Assert.Same(entry.Translations[0], EntryTranslations.Find(entry, "EN-us")); // 语种代码大小写不敏感
        Assert.Null(EntryTranslations.Find(entry, "ja-JP"));
        Assert.Empty(EntryTranslations.Text(entry, "ja-JP", "zh-CN"));
    }

    [Fact]
    public void Source_Culture_Falls_Back_To_SourceText_When_The_Row_Is_Missing()
    {
        var withRow = Entry(source: "源文案", translations: [("zh-CN", "改写后的源文案", TranslationStatus.Approved)]);
        Assert.Equal("改写后的源文案", EntryTranslations.Text(withRow, "zh-CN", "zh-CN")); // 有译文行就用行里的

        var withoutRow = Entry(source: "源文案");
        Assert.Equal("源文案", EntryTranslations.Text(withoutRow, "zh-CN", "zh-CN")); // 没有行才回退源文案

        // 非源语种没有译文行就是空（不回退源文案，否则会被当成「已翻译」）
        Assert.Equal("", EntryTranslations.Text(withoutRow, "en-US", "zh-CN"));

        Assert.True(EntryTranslations.IsSource("zh-cn", "zh-CN"));
        Assert.False(EntryTranslations.IsSource("en-US", "zh-CN"));
    }

    // ---------------------------------------------------------------------
    // EntryValidationSet：全量校验建好的三张表要能各自独立使用
    // ---------------------------------------------------------------------

    private static readonly IReadOnlyList<GlossaryTerm> Glossary =
    [
        new() { SourceTerm = "Lingua", DoNotTranslate = true, CaseSensitive = true }
    ];

    [Fact]
    public void Full_Check_Builds_Consistent_Tables()
    {
        var entries = new List<TranslationEntry>
        {
            Entry("brand", "欢迎使用 Lingua", translations: [("en-US", "Welcome to Linguo", TranslationStatus.Approved)]),
            Entry("placeholder", "共 {count} 条", translations: [("en-US", "Total items", TranslationStatus.Approved)]),
            Entry("long", "提交", maxLength: 4, translations: [("en-US", "Submit now", TranslationStatus.Approved)]),
            Entry("empty", "待翻译"), // 没有 en-US 译文
        };

        var result = EntryValidationSet.Compute(
            new DeterministicTranslationValidator(), entries, ["zh-CN", "en-US"], "zh-CN", Glossary);

        // 命中术语：只有源文案含 Lingua 的那条
        Assert.NotEmpty(result.MatchedTerms[entries[0].Id]);
        Assert.Empty(result.MatchedTerms[entries[1].Id]);

        // 按词条聚合的问题：占位符缺失与超长都在
        Assert.Contains(result.IssuesByEntry[entries[1].Id], i => i.RuleCode == "PLACEHOLDER_MISSING");
        Assert.Contains(result.IssuesByEntry[entries[2].Id], i => i.RuleCode == "MAX_LENGTH_EXCEEDED");
        Assert.Contains(result.IssuesByEntry[entries[0].Id], i => i.RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal));
        Assert.Contains(result.IssuesByEntry[entries[3].Id], i => i.RuleCode == "EMPTY_TRANSLATION");

        // 单元格备注：有问题的格子有备注，没问题的格子不留键
        Assert.Contains((entries[1].Id, "en-US"), result.Lookup.Keys);
        Assert.DoesNotContain((entries[3].Id, "en-US"), result.Lookup.Keys); // 「没有译文」只在报告里，不进单元格备注
        Assert.DoesNotContain((entries[1].Id, "zh-CN"), result.Lookup.Keys);

        // 三张表的口径一致：Lookup 里的每条都能在 IssuesByEntry 里找到同样的对象
        foreach (var ((entryId, culture), notes) in result.Lookup)
        {
            Assert.All(notes, note => Assert.Contains(note, result.IssuesByEntry[entryId]));
            Assert.Equal(culture, notes[0].Culture);
        }
    }

    [Fact]
    public void Empty_Glossary_Skips_Matching_Entirely()
    {
        var entry = Entry("brand", "欢迎使用 Lingua", translations: [("en-US", "Lingua", TranslationStatus.Approved)]);

        var result = EntryValidationSet.Compute(
            new DeterministicTranslationValidator(), [entry], ["zh-CN", "en-US"], "zh-CN", []);

        Assert.Empty(result.MatchedTerms[entry.Id]);
        Assert.Empty(result.IssuesByEntry[entry.Id]); // 没有术语库就没有术语违规
    }

    [Fact]
    public void CheckOne_Rewrites_Cell_Notes_So_Stale_Remarks_Do_Not_Linger()
    {
        var validator = new DeterministicTranslationValidator();
        var lookup = new Dictionary<(Guid, string), List<ValidationIssue>>();
        var entry = Entry("k", "共 {count} 条", translations: [("en-US", "Total", TranslationStatus.Approved)]);
        IReadOnlyList<GlossaryTerm> none = [];

        var (_, first) = EntryValidationSet.CheckOne(validator, entry, ["zh-CN", "en-US"], "zh-CN", none, lookup);
        Assert.Contains(first, i => i.RuleCode == "PLACEHOLDER_MISSING");
        Assert.Contains((entry.Id, "en-US"), lookup.Keys);

        // 译文改对之后重新校验同一格：旧备注必须被清掉，而不是留着上一轮的问题
        entry.Translations[0].Text = "Total {count}";
        var (_, second) = EntryValidationSet.CheckOne(validator, entry, ["zh-CN", "en-US"], "zh-CN", none, lookup);
        Assert.DoesNotContain(second, i => i.RuleCode == "PLACEHOLDER_MISSING");
        Assert.DoesNotContain((entry.Id, "en-US"), lookup.Keys);
    }

    // ---------------------------------------------------------------------
    // EntryMatrix：AI 相关判定（决定按钮文案、是否弹覆盖确认）
    // ---------------------------------------------------------------------

    [Fact]
    public void NeedsAi_Covers_Missing_Blank_Draft_And_Review_But_Not_Approved_Or_Ai()
    {
        var entry = Entry(translations:
        [
            ("en-US", "Approved text", TranslationStatus.Approved),
            ("ja-JP", "AI text", TranslationStatus.AiTranslated),
            ("ko-KR", "Draft text", TranslationStatus.Draft),
            ("fr-FR", "Review text", TranslationStatus.NeedsReview),
            ("de-DE", "   ", TranslationStatus.Approved), // 空白即使已审核也算待补
        ]);

        Assert.False(EntryMatrix.NeedsAi(entry, "en-US"));
        Assert.False(EntryMatrix.NeedsAi(entry, "ja-JP"));
        Assert.True(EntryMatrix.NeedsAi(entry, "ko-KR"));
        Assert.True(EntryMatrix.NeedsAi(entry, "fr-FR"));
        Assert.True(EntryMatrix.NeedsAi(entry, "de-DE"));
        Assert.True(EntryMatrix.NeedsAi(entry, "it-IT")); // 完全没有译文行

        Assert.Equal(["ko-KR", "fr-FR", "de-DE", "it-IT"],
            EntryMatrix.PendingAiCultures(entry, ["en-US", "ja-JP", "ko-KR", "fr-FR", "de-DE", "it-IT"]));
    }

    [Fact]
    public void ApprovedCultures_Drive_The_Overwrite_Confirmation()
    {
        var entry = Entry(translations:
        [
            ("en-US", "Approved", TranslationStatus.Approved),
            ("ja-JP", "AI", TranslationStatus.AiTranslated),
            ("ko-KR", "  ", TranslationStatus.Approved), // 已审核但空白：覆盖它不需要确认
        ]);

        var approved = EntryMatrix.ApprovedCultures(entry, ["en-US", "ja-JP", "ko-KR"]);
        Assert.Equal(["en-US"], approved);
        Assert.Contains("会覆盖 1 项已人工审核的译文（en-US）", EntryMatrix.OverwriteTitle(approved));

        // 没有已审核内容：不弹确认，提示也说明会直接覆盖
        var none = EntryMatrix.ApprovedCultures(entry, ["ja-JP", "ko-KR"]);
        Assert.Empty(none);
        Assert.Contains("不再确认", EntryMatrix.OverwriteTitle(none));
    }

    [Fact]
    public void StatusKey_And_DisplayKey_Follow_The_Matrix_Conventions()
    {
        Assert.Equal("missing", EntryMatrix.StatusKey(null, missing: true));
        Assert.Equal("ai", EntryMatrix.StatusKey(new TranslationValue { Culture = "en-US", Text = "x", Status = TranslationStatus.AiTranslated }, false));
        Assert.Equal("review", EntryMatrix.StatusKey(new TranslationValue { Culture = "en-US", Text = "x", Status = TranslationStatus.NeedsReview }, false));
        Assert.Equal("review", EntryMatrix.StatusKey(new TranslationValue { Culture = "en-US", Text = "x", Status = TranslationStatus.Draft }, false));
        Assert.Equal("approved", EntryMatrix.StatusKey(new TranslationValue { Culture = "en-US", Text = "x", Status = TranslationStatus.Approved }, false));

        Assert.Equal("order.k", EntryMatrix.DisplayKey(Entry("k")));
        Assert.Equal("k", EntryMatrix.DisplayKey(Entry("k", namespaceCode: null))); // 项目根不显示前缀
    }
}
