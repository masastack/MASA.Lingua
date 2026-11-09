using System.Net;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>阶段 4：术语库闭环 —— 禁用译法 / 可接受变体、统一写入校验、遵循率、共享影响面、只读术语端点。</summary>
public sealed class GlossaryClosureTests
{
    private static GlossaryTerm Term(string source, string? en = null, string[]? variants = null, string[]? forbidden = null,
        bool dnt = false, bool caseSensitive = false)
    {
        var t = new GlossaryTerm { SourceTerm = source, DoNotTranslate = dnt, CaseSensitive = caseSensitive };
        if (en is not null) t.TargetTranslations["en-US"] = en;
        if (variants is not null) t.AcceptedVariants["en-US"] = variants.ToList();
        if (forbidden is not null) t.ForbiddenTranslations["en-US"] = forbidden.ToList();
        return t;
    }

    // --- 规则（纯逻辑）-------------------------------------------------------

    [Fact]
    public void Rules_Should_Accept_Standard_Or_Variant_And_Flag_Missing_Standard()
    {
        var term = Term("工作台", "Workspace", variants: ["Work space"]);

        Assert.True(GlossaryRules.Evaluate(term, "en-US", "Open the Workspace").Followed);
        Assert.True(GlossaryRules.Evaluate(term, "en-US", "Open the work space").Followed); // 变体 + 忽略大小写

        var miss = GlossaryRules.Evaluate(term, "en-US", "Open the dashboard");
        Assert.True(miss.Applies);
        var v = Assert.Single(miss.Violations);
        Assert.Equal(GlossaryRules.TermMismatch, v.RuleCode);
        Assert.Contains("Work space", v.Message); // 提示里带出可接受变体

        // 没有该语种的约束：不适用，不计入遵循率
        Assert.False(GlossaryRules.Evaluate(term, "ja-JP", "なんでも").Applies);
    }

    [Fact]
    public void Rules_Should_Flag_Forbidden_Words_Even_With_Standard_Present_Using_Word_Boundary()
    {
        var term = Term("工作台", "Workspace", forbidden: ["Dashboard", "Work"]);

        // 禁用词出现即违规，即使同时含标准译法
        var both = GlossaryRules.Evaluate(term, "en-US", "Workspace dashboard");
        var f = Assert.Single(both.Violations);
        Assert.Equal(GlossaryRules.ForbiddenTerm, f.RuleCode);
        Assert.Equal(IssueSeverity.Error, f.Severity);
        Assert.Equal("Workspace", f.SuggestedFix);

        // 拉丁词按词边界：禁用 "Work" 不误伤 "Workspace"
        Assert.True(GlossaryRules.Evaluate(term, "en-US", "Open Workspace").Followed);
        Assert.False(GlossaryRules.Evaluate(term, "en-US", "Open Workspace to Work").Followed);

        // 只有禁用译法、没有标准译法也成立
        var onlyForbidden = Term("提交", forbidden: ["Commit"]);
        Assert.True(GlossaryRules.Evaluate(onlyForbidden, "en-US", "Submit").Followed);
        Assert.False(GlossaryRules.Evaluate(onlyForbidden, "en-US", "Commit now").Followed);
    }

    [Fact]
    public void Rules_Should_Require_Dnt_Source_Term_And_Mark_Forbidden_As_Blocking()
    {
        var dnt = Term("Lingua", dnt: true);
        Assert.True(GlossaryRules.Evaluate(dnt, "en-US", "Welcome to Lingua").Followed);
        Assert.Equal(GlossaryRules.DntViolation,
            Assert.Single(GlossaryRules.Evaluate(dnt, "en-US", "Welcome").Violations).RuleCode);

        var entry = new TranslationEntry { Namespace = new TranslationNamespace { Code = "n" }, Key = "k", SourceText = "打开工作台" };
        var issues = new DeterministicTranslationValidator()
            .ValidateEntry(entry, "en-US", "Open dashboard", [Term("工作台", "Workspace", forbidden: ["dashboard"])]);
        Assert.Contains(issues, i => i.RuleCode == GlossaryRules.ForbiddenTerm && i.IsBlocking);
        Assert.Contains(issues, i => i.RuleCode == GlossaryRules.TermMismatch && i.IsBlocking); // 术语违规都阻断
    }

    [Fact]
    public void Rules_Should_Match_Dnt_Term_At_Word_Boundary_Only()
    {
        // 整词出现：遵循；不区分大小写时小写也算保留
        var dnt = Term("Lingua", dnt: true);
        Assert.True(GlossaryRules.Evaluate(dnt, "en-US", "Welcome to Lingua").Followed);
        Assert.True(GlossaryRules.Evaluate(dnt, "en-US", "welcome to lingua").Followed);

        // 子串不算保留：LinguaPhone 判违规（与禁用译法同口径）
        Assert.False(GlossaryRules.Evaluate(dnt, "en-US", "Welcome to LinguaPhone").Followed);

        // 区分大小写时，小写不算保留
        var cs = Term("Lingua", dnt: true, caseSensitive: true);
        Assert.False(GlossaryRules.Evaluate(cs, "en-US", "welcome to lingua").Followed);

        // 中日韩 DNT 术语仍按子串匹配
        var cjk = Term("灵格", dnt: true);
        Assert.True(GlossaryRules.Evaluate(cjk, "en-US", "灵格出品").Followed);
    }

    [Fact]
    public void Matcher_Should_Dedupe_Overlapping_Matches_Longest_First()
    {
        // 等长重叠（中日韩按子串命中）：只有先声明的「工作台设」命中，「台设置」不在该处重复命中
        var equal = new List<GlossaryTerm> { Term("工作台设"), Term("台设置") };
        Assert.Equal(["工作台设"], GlossaryMatcher.FindMatchedTerms("工作台设置", equal).Select(t => t.SourceTerm));

        // 嵌套去重回归：短术语在其它位置的命中不受影响
        var nested = new List<GlossaryTerm> { Term("结算"), Term("结算中心") };
        Assert.Equal(["结算", "结算中心"],
            GlossaryMatcher.FindMatchedTerms("结算中心与结算", nested).Select(t => t.SourceTerm));

        // 跨位置回归：Workspace 只占一处，Work 在前一个位置的命中保留
        var latin = new List<GlossaryTerm> { Term("Work"), Term("Workspace") };
        Assert.Equal(["Work", "Workspace"],
            GlossaryMatcher.FindMatchedTerms("Work Workspace", latin).Select(t => t.SourceTerm));
    }

    // --- 遵循率与共享影响面 -----------------------------------------------------

    private static TranslationEntry Entry(string ns, string key, string source, params (string Culture, string Text)[] tr)
    {
        var e = new TranslationEntry { Namespace = new TranslationNamespace { Code = ns }, Key = key, SourceText = source };
        foreach (var (c, t) in tr)
        {
            e.Translations.Add(new TranslationValue { Culture = c, Text = t, Status = TranslationStatus.Approved });
        }

        return e;
    }

    [Fact]
    public void ComplianceCalculator_Should_Count_Only_Constrained_Hits_With_Translations()
    {
        var glossary = new List<GlossaryTerm> { Term("工作台", "Workspace", variants: ["Work space"], forbidden: ["Dashboard"]) };
        var entries = new[]
        {
            Entry("a", "ok", "打开工作台", ("en-US", "Open Workspace")),
            Entry("a", "variant", "进入工作台", ("en-US", "Enter work space")),
            Entry("a", "bad", "工作台设置", ("en-US", "Dashboard settings")),
            Entry("a", "missing", "返回工作台"),                          // 无译文：不计入
            Entry("a", "unrelated", "确定", ("en-US", "OK")),             // 没命中术语：不计入
        };

        var result = GlossaryComplianceCalculator.Compute(entries, ["zh-CN", "en-US", "ja-JP"], "zh-CN", glossary);

        Assert.DoesNotContain(result, r => r.Culture == "zh-CN"); // 源语种不统计
        var en = Assert.Single(result, r => r.Culture == "en-US");
        Assert.Equal(3, en.Checked);
        Assert.Equal(2, en.Followed);
        Assert.Equal(66, en.Percent);

        var ja = Assert.Single(result, r => r.Culture == "ja-JP");
        Assert.Equal(0, ja.Checked);
        Assert.Null(ja.Percent);
    }

    [Fact]
    public void SharedImpact_Should_Count_Inheriting_And_Overriding_Projects()
    {
        Project Project(string code, string[] shared, params (string Ns, string Key)[] own)
        {
            var p = new Project { Code = code, SharedNamespaceCodes = shared.ToList() };
            foreach (var group in own.GroupBy(o => o.Ns))
            {
                var ns = new TranslationNamespace { Code = group.Key };
                ns.Entries.AddRange(group.Select(o => Entry(o.Ns, o.Key, "x")));
                p.Namespaces.Add(ns);
            }

            return p;
        }

        ProjectImpact Impact(Project p) => new(
            p.Code,
            p.SharedNamespaceCodes,
            p.Namespaces.Select(n => new ImpactNamespace(n.Code, n.IsShared, n.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal))).ToList());

        var projects = new[]
        {
            Project("a", ["common"]),
            Project("b", ["common"], ("common", "ok")),   // 覆盖了 common.ok
            Project("c", ["common"], ("common", "other")), // 同命名空间但不同 Key：没覆盖
            Project("d", ["errors"], ("common", "ok")),    // 没继承 common：不算
        };

        var impact = SharedImpact.Compute(projects.Select(Impact), "common", "ok");
        Assert.Equal(3, impact.InheritingProjects);
        Assert.Equal(1, impact.OverridingProjects);
        Assert.Equal(["b"], impact.OverridingProjectCodes);
    }

    // --- 仓储：术语保存校验与统一写入校验 ---------------------------------------------

    private static async Task<(ILinguaRepository Repo, string DbPath)> CreateRepositoryAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_glossary4");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider.GetRequiredService<ILinguaRepository>(), databaseName);
    }

    private static void Cleanup(string databaseName) => PostgresServer.Drop(databaseName);

    [Fact]
    public async Task SaveGlossaryTerm_Should_Normalize_Word_Lists_And_Reject_Clashes()
    {
        var (repo, dbPath) = await CreateRepositoryAsync();
        try
        {
            // 清洗：去空白 / 去重 / 去空值；没有标准译法的语种里，变体被丢弃
            var saved = await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "购物篮",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = " Basket " },
                AcceptedVariants = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["en-US"] = ["Cart ", "cart", " "],
                    ["ja-JP"] = ["カゴ"]
                },
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Bag", "bag"] }
            });
            Assert.Equal("Basket", saved.TargetTranslations["en-US"]);
            Assert.Equal(["Cart"], saved.AcceptedVariants["en-US"]);
            Assert.DoesNotContain("ja-JP", saved.AcceptedVariants.Keys);
            Assert.Equal(["Bag"], saved.ForbiddenTranslations["en-US"]);

            // 只写禁用译法也有效
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "提交",
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Commit"] }
            });

            // 禁用译法与标准译法 / 变体冲突
            await Assert.ThrowsAsync<GlossaryValidationException>(() => repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "冲突1",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" },
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["workspace"] }
            }));
            await Assert.ThrowsAsync<GlossaryValidationException>(() => repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "冲突2",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" },
                AcceptedVariants = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Work space"] },
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Work Space"] }
            }));

            // DNT 术语不保留任何目标侧约束
            var dnt = await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "Blazor2",
                DoNotTranslate = true,
                AcceptedVariants = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["x"] },
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["y"] }
            });
            Assert.Empty(dnt.AcceptedVariants);
            Assert.Empty(dnt.ForbiddenTranslations);

            // 持久化往返（JSON 列转换）
            var loaded = (await repo.GetGlossaryTermsByScopeAsync(null)).Single(t => t.SourceTerm == "购物篮");
            Assert.Equal(["Cart"], loaded.AcceptedVariants["en-US"]);
            Assert.Equal(["Bag"], loaded.ForbiddenTranslations["en-US"]);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task UpsertEntry_Should_Downgrade_Violations_To_NeedsReview_Unless_Acknowledged()
    {
        var (repo, dbPath) = await CreateRepositoryAsync();
        try
        {
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                SourceTerm = "控制台",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" },
                ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Dashboard"] }
            });

            async Task<TranslationValue> Save(string text, bool ack = false, TranslationStatus status = TranslationStatus.Approved)
            {
                await repo.UpsertEntryAsync("shop-vue", "home", "title", "打开控制台", null, null,
                    new Dictionary<string, string> { ["en-US"] = text },
                    status: status, acknowledgeIssues: ack);
                var project = (await repo.GetProjectByCodeAsync("shop-vue"))!;
                return project.Namespaces.Single(n => n.Code == "home").Entries.Single(e => e.Key == "title")
                    .Translations.Single(t => t.Culture == "en-US");
            }

            // 违反禁用译法：未确认（ack=false）的写入（AI 补全 / API 等写入口）一律进入待复核，并写明原因；
            // 管理页的手工编辑固定带 ack（编辑即定稿，违规问题交给校验提示与发布门禁），不走这条降级路径
            var bad = await Save("Open Dashboard");
            Assert.Equal(TranslationStatus.NeedsReview, bad.Status);
            Assert.Contains("Dashboard", bad.ReviewComment);

            var ai = await Save("Open Dashboard", status: TranslationStatus.AiTranslated);
            Assert.Equal(TranslationStatus.NeedsReview, ai.Status);

            // 人工确认（误报等）后按请求的状态入库，并清掉原因
            var acked = await Save("Open Dashboard", ack: true);
            Assert.Equal(TranslationStatus.Approved, acked.Status);
            Assert.Null(acked.ReviewComment);

            var good = await Save("Open Workspace");
            Assert.Equal(TranslationStatus.Approved, good.Status);
            Assert.Null(good.ReviewComment);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task EffectiveGlossary_Should_Merge_Global_And_Project_And_Return_Null_For_Unknown_Project()
    {
        var (repo, dbPath) = await CreateRepositoryAsync();
        try
        {
            var project = (await repo.GetProjectByCodeAsync("shop-vue"))!;
            await repo.SaveGlossaryTermAsync(new GlossaryTerm
            {
                ProjectId = project.Id,
                SourceTerm = "结算",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Settlement" }
            });

            var terms = await repo.GetEffectiveGlossaryByProjectCodeAsync("shop-vue");
            Assert.NotNull(terms);
            Assert.Equal("Settlement", Assert.Single(terms!, t => t.SourceTerm == "结算").TargetTranslations["en-US"]);
            Assert.Contains(terms!, t => t.SourceTerm == "工作台"); // 其他全局术语继承

            Assert.Null(await repo.GetEffectiveGlossaryByProjectCodeAsync("no-such-project"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // --- HTTP 端点 ----------------------------------------------------------------

    [Fact]
    public async Task Glossary_Endpoint_Should_Return_Effective_Terms_With_ETag()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        var project = (await repo.GetProjectByCodeAsync("shop-vue"))!;
        await repo.SaveGlossaryTermAsync(new GlossaryTerm
        {
            ProjectId = project.Id,
            SourceTerm = "购物篮",
            TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Basket" },
            AcceptedVariants = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Cart"] },
            ForbiddenTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = ["Bag"] }
        });

        using var res = await client.GetAsync("/api/v1/projects/shop-vue/glossary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var etag = res.Headers.ETag!.Tag;
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("shop-vue", body.GetProperty("project").GetString());
        var terms = body.GetProperty("terms").EnumerateArray().ToList();
        var basket = Assert.Single(terms, t => t.GetProperty("sourceTerm").GetString() == "购物篮");
        Assert.Equal("project", basket.GetProperty("scope").GetString());
        Assert.Equal("Basket", basket.GetProperty("targets").GetProperty("en-US").GetString());
        Assert.Equal("Cart", basket.GetProperty("acceptedVariants").GetProperty("en-US")[0].GetString());
        Assert.Equal("Bag", basket.GetProperty("forbidden").GetProperty("en-US")[0].GetString());
        Assert.Contains(terms, t => t.GetProperty("scope").GetString() == "global");

        using var cond = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/shop-vue/glossary");
        cond.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var notModified = await client.SendAsync(cond);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        using var missing = await client.GetAsync("/api/v1/projects/no-such/glossary");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
