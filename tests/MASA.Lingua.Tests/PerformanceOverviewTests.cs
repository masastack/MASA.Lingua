using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Server.Options;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 性能改造的新增能力：轻量概览 / 影响面投影、发布页合并加载（检查 + 差异一次算出）、批量译文写入。
/// 合并加载与批量写入都有「与原有路径逐项等价」的口径测试，防止改造改变行为。
/// </summary>
public sealed class PerformanceOverviewTests
{
    private sealed class TestEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MASA.Lingua.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Harness(ILinguaRepository repo, string databaseName, string root) : IDisposable
    {
        public ILinguaRepository Repo { get; } = repo;
        public string DatabaseName { get; } = databaseName;
        public string Root { get; } = root;

        public LinguaSseBroadcaster Sse { get; } = new();

        public StaticBundlePublisher NewPublisher() => new(
            Repo, // IProjectCatalog
            Repo, // IGlossaryCatalog
            Repo, // IPublishArchive
            new BundleCompiler(),
            Sse,
            new TestEnv(Root),
            new ConfigurationBuilder().Build(),
            NullLogger<StaticBundlePublisher>.Instance);

        public void Dispose()
        {
            PostgresServer.Drop(DatabaseName);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_perf");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();

        var root = Path.Combine(Path.GetTempPath(), $"lingua_perf_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new Harness(provider.GetRequiredService<ILinguaRepository>(), databaseName, root);
    }

    // ---------------------------------------------------------------------
    // GetReleaseOverviewAsync：与 CheckProjectAsync + GetDraftDiffAsync 逐项等价
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetReleaseOverview_Should_Match_Separate_Check_And_Diff()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");

        // 线上版本已固定；再写两条草稿（一新一改），制造非零差异
        await h.Repo.UpsertEntryAsync("shop-vue", "order", "addedKey", "新增词条", null, null,
            new Dictionary<string, string> { ["en-US"] = "Added entry" }, status: TranslationStatus.Approved);
        await h.Repo.UpsertEntryAsync("shop-vue", "order", "changedKey", "原文案", null, null,
            new Dictionary<string, string> { ["en-US"] = "Changed draft text" }, status: TranslationStatus.Draft);

        var overview = await publisher.GetReleaseOverviewAsync("shop-vue");
        var report = await publisher.CheckProjectAsync("shop-vue");
        var diff = await publisher.GetDraftDiffAsync("shop-vue");

        // 检查报告：阻断项与完成度逐项一致
        Assert.Equal(report.CanPublish, overview.Report.CanPublish);
        Assert.Equal(report.Blockers.Select(b => b.Code), overview.Report.Blockers.Select(b => b.Code));
        Assert.Equal(report.EntryCount, overview.Report.EntryCount);
        Assert.Equal(report.ExcludedTranslations, overview.Report.ExcludedTranslations);
        Assert.Equal(
            report.Coverage.Select(c => (c.Culture, c.Approved, c.Ai, c.Review, c.Missing)),
            overview.Report.Coverage.Select(c => (c.Culture, c.Approved, c.Ai, c.Review, c.Missing)));

        // 草稿差异：口径与逐项加载的版本一致
        Assert.Equal(diff.NeverPublished, overview.Diff.NeverPublished);
        Assert.Equal(diff.Added, overview.Diff.Added);
        Assert.Equal(diff.Changed, overview.Diff.Changed);
        Assert.Equal(diff.Removed, overview.Diff.Removed);
        Assert.Equal(diff.ChangedKeyCount, overview.Diff.ChangedKeyCount);
        Assert.Equal(
            diff.ChangesByCulture.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase),
            overview.Diff.ChangesByCulture.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase));
        Assert.True(overview.Diff.HasChanges);
    }

    // ---------------------------------------------------------------------
    // GetProjectOverviewsAsync：元数据投影正确，且不含词条
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProjectOverviews_Should_Project_Metadata()
    {
        using var h = await CreateHarnessAsync();

        await h.Repo.CreateProjectAsync("perf-overview-proj");
        await h.Repo.UpsertEntryAsync("perf-overview-proj", "ns", "key", "源文案", null, null,
            new Dictionary<string, string> { ["en-US"] = "Text" }, status: TranslationStatus.Approved);
        var policy = new PublishPolicy { Content = PublishContentMode.ApprovedOnly, MinCoveragePercent = 80 };
        await h.Repo.SavePublishPolicyAsync("perf-overview-proj", policy);
        var publisher = h.NewPublisher();
        await publisher.PublishProjectAsync("perf-overview-proj", new PublishRequest("t", Force: true, ForceReason: "test"));

        var overviews = await h.Repo.GetProjectOverviewsAsync();
        var overview = overviews.Single(p => p.Code == "perf-overview-proj");

        Assert.Equal("zh-CN", overview.DefaultCulture);
        Assert.Equal(PublishContentMode.ApprovedOnly, overview.PublishPolicy.Content);
        Assert.Equal(80, overview.PublishPolicy.MinCoveragePercent);
        Assert.Equal(["zh-CN", "en-US", "ja-JP"], overview.SupportedCultures);
        Assert.NotNull(overview.LastPublishedAt);
        Assert.False(string.IsNullOrEmpty(overview.LastPublishedVersionHash));

        // 共享作用域与缺失项目：概览只描述真实存在的项目
        Assert.DoesNotContain(overviews, p => p.Code == "__shared__");
    }

    // ---------------------------------------------------------------------
    // GetProjectImpactSummariesAsync：命名空间 + 词条 Key 投影，影响面计算与全量加载一致
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetProjectImpactSummaries_Should_Match_Full_Load_Impact()
    {
        using var h = await CreateHarnessAsync();

        // 种子项目默认也继承 common：先取基线，再做增量断言
        var baseline = SharedImpact.Compute(await h.Repo.GetProjectImpactSummariesAsync(), "common", "greet");

        await h.Repo.CreateProjectAsync("perf-impact-a");
        await h.Repo.CreateProjectAsync("perf-impact-b");

        // 两个项目都继承 common（默认 SharedNamespaceCodes = ["common"]），
        // a 项目用共享词条 common.greet，b 项目自己覆盖了 common.greet
        await h.Repo.UpsertEntryAsync("__shared__", "common", "greet", "你好", null, null, null);
        await h.Repo.UpsertEntryAsync("perf-impact-b", "common", "greet", "你好", null, null,
            new Dictionary<string, string> { ["en-US"] = "Hello (override)" }, status: TranslationStatus.Approved);

        var impacts = await h.Repo.GetProjectImpactSummariesAsync();
        var a = impacts.Single(p => p.Code == "perf-impact-a");
        var b = impacts.Single(p => p.Code == "perf-impact-b");

        Assert.Contains("common", a.SharedNamespaceCodes);
        Assert.DoesNotContain(a.Namespaces, n => n.Code == "common");      // a 没有自己的 common 命名空间
        var bCommon = Assert.Single(b.Namespaces, n => n.Code == "common");
        Assert.False(bCommon.IsShared);
        Assert.Contains("greet", bCommon.EntryKeys);

        // 共享词条影响面：继承项目数比基线多 2（新项目都继承），覆盖只多了 b
        var impact = SharedImpact.Compute(impacts, "common", "greet");
        Assert.Equal(baseline.InheritingProjects + 2, impact.InheritingProjects);
        Assert.Equal(baseline.OverridingProjects + 1, impact.OverridingProjects);
        Assert.Contains("perf-impact-b", impact.OverridingProjectCodes);
        Assert.DoesNotContain("perf-impact-a", impact.OverridingProjectCodes);
    }

    // ---------------------------------------------------------------------
    // UpsertTranslationsBatchAsync：与逐条 UpsertEntryAsync 行为等价
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpsertTranslationsBatch_Should_Behave_Like_Sequential_Upserts()
    {
        using var h = await CreateHarnessAsync();
        await h.Repo.CreateProjectAsync("perf-batch-proj");
        await h.Repo.CreateProjectAsync("perf-seq-proj");

        // (key, 源文案, 英文译文, 期望状态)。badKey 的源文案带占位符而译文缺失 → 应降级为待复核
        var cases = new[]
        {
            (Key: "okKey", Source: "普通句子", En: "Plain text", Status: TranslationStatus.AiTranslated),
            (Key: "badKey", Source: "支付 {amount} 元", En: "Pay now", Status: TranslationStatus.AiTranslated),
            (Key: "reviewKey", Source: "待复核句子", En: "Review text", Status: TranslationStatus.NeedsReview),
        };

        var items = cases.Select(c => new TranslationUpsertBatchItem(
            "ns", c.Key, c.Source, "ctx", 40,
            new Dictionary<string, string> { ["en-US"] = c.En },
            c.Status)).ToList();
        var batchSaved = await h.Repo.UpsertTranslationsBatchAsync("perf-batch-proj", items);
        Assert.Equal(cases.Length, batchSaved.Count);

        foreach (var c in cases)
        {
            await h.Repo.UpsertEntryAsync("perf-seq-proj", "ns", c.Key, c.Source, "ctx", 40,
                new Dictionary<string, string> { ["en-US"] = c.En }, status: c.Status);
        }

        var projects = await h.Repo.GetAllProjectsAsync();
        var batchNs = projects.Single(p => p.Code == "perf-batch-proj").Namespaces.Single(n => n.Code == "ns");
        var seqNs = projects.Single(p => p.Code == "perf-seq-proj").Namespaces.Single(n => n.Code == "ns");

        foreach (var c in cases)
        {
            var batchEntry = batchNs.Entries.Single(e => e.Key == c.Key);
            var seqEntry = seqNs.Entries.Single(e => e.Key == c.Key);

            Assert.Equal(seqEntry.SourceText, batchEntry.SourceText);
            Assert.Equal(seqEntry.Context, batchEntry.Context);
            Assert.Equal(seqEntry.MaxLength, batchEntry.MaxLength);

            var batchTr = batchEntry.Translations.Single(t => t.Culture == "en-US");
            var seqTr = seqEntry.Translations.Single(t => t.Culture == "en-US");
            Assert.Equal(seqTr.Text, batchTr.Text);
            Assert.Equal(seqTr.Status, batchTr.Status);
            Assert.Equal(seqTr.ReviewComment, batchTr.ReviewComment);
            Assert.Equal(seqTr.SourceTextHash, batchTr.SourceTextHash);
        }

        // 违规降级在批量路径同样生效：badKey 应为待复核且写明原因
        var badBatch = batchNs.Entries.Single(e => e.Key == "badKey").Translations.Single(t => t.Culture == "en-US");
        Assert.Equal(TranslationStatus.NeedsReview, badBatch.Status);
        Assert.False(string.IsNullOrEmpty(badBatch.ReviewComment));
    }
}
