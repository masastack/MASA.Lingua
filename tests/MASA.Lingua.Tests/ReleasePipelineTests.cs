using System.Text;
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
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>阶段 1：发布管线——按状态过滤、发布门禁、快照内容与回滚、重启恢复、草稿差异。</summary>
public sealed class ReleasePipelineTests
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

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class Harness : IDisposable
    {
        public required ILinguaRepository Repo { get; init; }
        public required string DatabaseName { get; init; }
        public required string Root { get; init; }

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

        // 发布产物刻意不在 wwwroot 下：只通过分发端点对外，物化目录仅用于重启恢复与磁盘一致性校准
        public string BundlePath(string culture) => Path.Combine(Root, "App_Data", "i18n", "shop-vue", $"{culture}.json");

        public void Dispose()
        {
            PostgresServer.Drop(DatabaseName);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_release");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();

        var root = Path.Combine(Path.GetTempPath(), $"lingua_release_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new Harness { Repo = provider.GetRequiredService<ILinguaRepository>(), DatabaseName = databaseName, Root = root };
    }

    // acknowledge=true：模拟"人工确认"，即使有 Error 级问题也保持传入状态（否则写入口会降级为待复核）
    private static Task Upsert(ILinguaRepository repo, string key, string source, string en, TranslationStatus status, bool acknowledge = false) =>
        repo.UpsertEntryAsync("shop-vue", "order", key, source, null, null,
            new Dictionary<string, string> { ["zh-CN"] = source, ["en-US"] = en },
            status: status, acknowledgeIssues: acknowledge);

    // ---------------------------------------------------------------------

    [Fact]
    public void CoverageCalculator_Should_Count_Statuses_And_Live_Percent()
    {
        TranslationEntry Entry(string key, params (string Culture, string Text, TranslationStatus Status)[] trs) => new()
        {
            Namespace = new TranslationNamespace { Code = "t" },
            Key = key,
            SourceText = "源文案",
            Translations = trs.Select(t => new TranslationValue { Culture = t.Culture, Text = t.Text, Status = t.Status }).ToList()
        };

        var entries = new[]
        {
            Entry("a", ("en-US", "A", TranslationStatus.Approved)),
            Entry("b", ("en-US", "B", TranslationStatus.AiTranslated)),
            Entry("c", ("en-US", "C", TranslationStatus.NeedsReview)),
            Entry("d"),
        };

        var coverage = CoverageCalculator.Compute(entries, ["zh-CN", "en-US"], "zh-CN");

        var zh = coverage.Single(c => c.Culture == "zh-CN");
        Assert.Equal(4, zh.Approved);               // 源语种没有译文行：按源文案计入已审核
        var en = coverage.Single(c => c.Culture == "en-US");
        Assert.Equal((1, 1, 1, 1), (en.Approved, en.Ai, en.Review, en.Missing));
        Assert.Equal(25, en.LivePercent(PublishContentMode.ApprovedOnly));
        Assert.Equal(50, en.LivePercent(PublishContentMode.ApprovedAndAi));
        Assert.Equal(75, en.LivePercent(PublishContentMode.All));
    }

    [Fact]
    public async Task Publish_Should_Filter_By_Status_And_Fall_Back_To_Default_Culture()
    {
        using var h = await CreateHarnessAsync();
        await Upsert(h.Repo, "approvedKey", "已审核句子", "Approved text", TranslationStatus.Approved);
        await Upsert(h.Repo, "aiKey", "AI句子", "Ai text", TranslationStatus.AiTranslated);
        await Upsert(h.Repo, "reviewKey", "待复核句子", "Review text", TranslationStatus.NeedsReview);

        var publisher = h.NewPublisher();
        await publisher.PublishProjectAsync("shop-vue", new PublishRequest("t", Force: true, ForceReason: "test"));
        var en = await publisher.GetOrCompileBundleAsync("shop-vue", "en-US");

        Assert.NotNull(en);
        Assert.Equal("Approved text", en!.LookupEntries["order.approvedKey"]);
        Assert.Equal("Ai text", en.LookupEntries["order.aiKey"]);
        Assert.Equal("待复核句子", en.LookupEntries["order.reviewKey"]); // 待复核不上线，回退到默认语种

        // 策略改为仅已审核：AI 译文也不上线
        var policy = new PublishPolicy { Content = PublishContentMode.ApprovedOnly };
        await h.Repo.SavePublishPolicyAsync("shop-vue", policy);
        await publisher.PublishProjectAsync("shop-vue", new PublishRequest("t2", Force: true, ForceReason: "test"));
        en = await publisher.GetOrCompileBundleAsync("shop-vue", "en-US");
        Assert.Equal("AI句子", en!.LookupEntries["order.aiKey"]);

        // 导出是草稿视图（不过滤状态）
        var layers = await h.Repo.BuildTranslationLayersAsync("shop-vue", "en-US");
        var draft = new BundleCompiler().Compile("shop-vue", "en-US", layers);
        Assert.Equal("Review text", draft.LookupEntries["order.reviewKey"]);
    }

    [Fact]
    public async Task PublishGate_Should_Block_Violations_Require_Reason_For_Force_And_Record_It()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();
        await publisher.EnsurePublishedAsync("shop-vue");

        // 已审核的英文译文缺失占位符 {amount}，属于「即将上线」的 Error 级问题
        // 写入口统一校验：未确认时有 Error 级问题的译文会被降级为待复核，不会进入门禁
        await Upsert(h.Repo, "gateKey", "支付 {amount} 元", "Pay now", TranslationStatus.Approved);
        Assert.True((await publisher.CheckProjectAsync("shop-vue")).CanPublish);
        // 人工确认后保留已审核状态，才会被发布门禁拦截
        await Upsert(h.Repo, "gateKey", "支付 {amount} 元", "Pay now", TranslationStatus.Approved, acknowledge: true);

        var report = await publisher.CheckProjectAsync("shop-vue");
        Assert.False(report.CanPublish);
        Assert.Contains(report.Blockers, b => b.Code == PublishChecker.ErrorsCode);

        var blocked = await Assert.ThrowsAsync<PublishBlockedException>(
            () => publisher.PublishProjectAsync("shop-vue", "尝试发布"));
        Assert.False(blocked.Report.CanPublish);

        // 强制发布但不填原因：仍然被拒绝
        await Assert.ThrowsAsync<PublishBlockedException>(
            () => publisher.PublishProjectAsync("shop-vue", new PublishRequest("x", Force: true, ForceReason: "  ")));

        var snapshot = await publisher.PublishProjectAsync(
            "shop-vue", new PublishRequest("紧急上线", Force: true, ForceReason: "线上紧急修复"));
        Assert.True(snapshot.Forced);
        Assert.Equal("线上紧急修复", snapshot.ForceReason);

        var history = await h.Repo.GetPublishSnapshotsAsync("shop-vue");
        Assert.True(history[0].Forced);
        Assert.Contains("未通过", history[0].GateSummary);

        // 同一条译文改为待复核后不再上线，门禁随之通过（只检查即将上线的内容）
        await Upsert(h.Repo, "gateKey", "支付 {amount} 元", "Pay now", TranslationStatus.NeedsReview);
        Assert.True((await publisher.CheckProjectAsync("shop-vue")).CanPublish);
    }

    [Fact]
    public async Task PublishGate_Should_Enforce_Min_Coverage_And_Allow_Disabling_Blockers()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();

        await h.Repo.SavePublishPolicyAsync("shop-vue", new PublishPolicy { MinCoveragePercent = 100 });
        await Upsert(h.Repo, "untranslated", "还没翻译", "", TranslationStatus.Draft);

        var report = await publisher.CheckProjectAsync("shop-vue");
        Assert.Contains(report.Blockers, b => b.Code == PublishChecker.CoverageCode && b.Message.Contains("en-US"));

        // 关闭 Error 阻断：不再因为占位符问题拦截
        await h.Repo.SavePublishPolicyAsync("shop-vue", new PublishPolicy { BlockOnErrors = false });
        await Upsert(h.Repo, "ph", "支付 {amount} 元", "Pay now", TranslationStatus.Approved, acknowledge: true);
        Assert.DoesNotContain((await publisher.CheckProjectAsync("shop-vue")).Blockers, b => b.Code == PublishChecker.ErrorsCode);
    }

    [Fact]
    public async Task Rollback_Should_Republish_Snapshot_Content_Without_Touching_Draft()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();

        Assert.True(await publisher.EnsurePublishedAsync("shop-vue")); // 引导发布
        var v1History = await h.Repo.GetPublishSnapshotsAsync("shop-vue");
        var v1 = Assert.Single(v1History);
        Assert.True(v1.CanRollback);
        var v1Zh = await publisher.GetOrCompileBundleAsync("shop-vue", "zh-CN");
        var v1ZhBytes = File.ReadAllBytes(h.BundlePath("zh-CN"));

        // 草稿新增一条并发布 v2
        await Upsert(h.Repo, "rollbackKey", "只在 v2 里", "Only in v2", TranslationStatus.Approved);
        var diff = await publisher.GetDraftDiffAsync("shop-vue");
        Assert.True(diff.HasChanges);
        Assert.True(diff.ChangedKeyCount >= 1);

        var v2 = await publisher.PublishProjectAsync("shop-vue", "v2");
        Assert.NotEqual(v1.VersionHash, v2.VersionHash);
        Assert.Contains("只在 v2 里", Encoding.UTF8.GetString(File.ReadAllBytes(h.BundlePath("zh-CN"))));
        Assert.False((await publisher.GetDraftDiffAsync("shop-vue")).HasChanges);

        // 回滚到 v1：线上产物逐字节一致（ETag 相同），版本指纹相同，历史新增一条回滚记录
        var rollback = await publisher.RollbackProjectAsync("shop-vue", v1.Id);
        Assert.Equal(SnapshotKind.Rollback, rollback.Kind);
        Assert.Equal(v1.Id, rollback.RollbackOfSnapshotId);
        Assert.Equal(v1.VersionHash, rollback.VersionHash);

        var afterZh = await publisher.GetOrCompileBundleAsync("shop-vue", "zh-CN");
        Assert.Equal(v1Zh!.ETag, afterZh!.ETag);
        Assert.Equal(v1ZhBytes, File.ReadAllBytes(h.BundlePath("zh-CN")));

        var history = await h.Repo.GetPublishSnapshotsAsync("shop-vue");
        Assert.Equal(3, history.Count);
        Assert.Equal(SnapshotKind.Rollback, history[0].Kind);
        Assert.Equal(rollback.VersionHash, (await h.Repo.GetProjectByCodeAsync("shop-vue"))!.LastPublishedVersionHash);

        // 草稿词条没有被回滚，并且页面能看到「草稿比线上新」
        var project = await h.Repo.GetProjectByCodeAsync("shop-vue");
        Assert.Contains(project!.Namespaces.SelectMany(n => n.Entries), e => e.Key == "rollbackKey");
        Assert.True((await publisher.GetDraftDiffAsync("shop-vue")).HasChanges);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.RollbackProjectAsync("shop-vue", Guid.NewGuid()));
    }

    [Fact]
    public async Task Restart_Should_Restore_From_Snapshot_And_Not_Create_New_Version()
    {
        using var h = await CreateHarnessAsync();
        var first = h.NewPublisher();
        await first.EnsurePublishedAsync("shop-vue");
        await Upsert(h.Repo, "persist", "持久化", "Persist", TranslationStatus.Approved);
        var published = await first.PublishProjectAsync("shop-vue", "v2");
        var etag = (await first.GetOrCompileBundleAsync("shop-vue", "en-US"))!.ETag;

        // 草稿继续修改但没有发布；模拟重启：新的发布器实例，且磁盘上的静态文件丢失
        await Upsert(h.Repo, "draftOnly", "只是草稿", "Draft only", TranslationStatus.Approved);
        Directory.Delete(Path.Combine(h.Root, "App_Data"), recursive: true);

        var restarted = h.NewPublisher();
        Assert.False(await restarted.EnsurePublishedAsync("shop-vue"));

        // 线上版本不变（草稿不会被推上线），文件由快照补回，没有新增发布记录
        var en = await restarted.GetOrCompileBundleAsync("shop-vue", "en-US");
        Assert.Equal(etag, en!.ETag);
        Assert.False(en.LookupEntries.ContainsKey("order.draftOnly"));
        Assert.True(File.Exists(h.BundlePath("en-US")));
        var history = await h.Repo.GetPublishSnapshotsAsync("shop-vue");
        Assert.Equal(2, history.Count);
        Assert.Equal(published.VersionHash, history[0].VersionHash);
    }

    [Fact]
    public async Task Snapshot_Content_Should_Be_Retained_Only_For_Recent_Snapshots_And_List_Excludes_Blob()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();

        for (var i = 0; i < EfCoreLinguaRepository.SnapshotContentRetention + 2; i++)
        {
            await Upsert(h.Repo, "k" + i, "文案" + i, "Text" + i, TranslationStatus.Approved);
            await publisher.PublishProjectAsync("shop-vue", $"v{i}");
        }

        var history = await h.Repo.GetPublishSnapshotsAsync("shop-vue", take: 100);
        Assert.Equal(EfCoreLinguaRepository.SnapshotContentRetention + 2, history.Count);
        Assert.All(history, s => Assert.Null(s.Content)); // 列表不带大字段
        Assert.Equal(EfCoreLinguaRepository.SnapshotContentRetention, history.Count(s => s.CanRollback));
        Assert.False(history[^1].CanRollback);             // 最老的两条只保留审计记录

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => publisher.RollbackProjectAsync("shop-vue", history[^1].Id));
    }

    [Fact]
    public async Task History_Page_Should_Report_Total_Page_Without_Overlap_And_Newest_First()
    {
        using var h = await CreateHarnessAsync();
        var publisher = h.NewPublisher();

        const int total = 12;
        for (var i = 0; i < total; i++)
        {
            await Upsert(h.Repo, "p" + i, "文案" + i, "Text" + i, TranslationStatus.Approved);
            await publisher.PublishProjectAsync("shop-vue", $"p{i}");
        }

        var first = await h.Repo.GetPublishSnapshotPageAsync("shop-vue", skip: 0, take: 10);
        var second = await h.Repo.GetPublishSnapshotPageAsync("shop-vue", skip: 10, take: 10);
        var beyond = await h.Repo.GetPublishSnapshotPageAsync("shop-vue", skip: total, take: 10);

        // 总数与页大小无关；两页条数互补且不重叠（时间相同的行靠 Id 兜底排序，不能翻页漏行/重复）
        Assert.Equal(total, first.TotalCount);
        Assert.Equal(total, second.TotalCount);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(beyond.Items);
        Assert.Empty(first.Items.Select(s => s.Id).Intersect(second.Items.Select(s => s.Id)));

        // 首页首行就是最新的一条，整页仍按时间新到旧（同一时刻写入的多条靠 Id 兜底，顺序稳定）
        var all = first.Items.Concat(second.Items).ToList();
        var unpaged = await h.Repo.GetPublishSnapshotsAsync("shop-vue", take: 100);
        Assert.Equal(unpaged.Select(s => s.Id), all.Select(s => s.Id));
        Assert.Equal("p11", all[0].ReleaseNote);
        Assert.Equal("p0", all[^1].ReleaseNote);
        Assert.True(all.Zip(all.Skip(1)).All(pair => pair.First.PublishedAt >= pair.Second.PublishedAt));

        // 分页列表同样不加载产物大字段
        Assert.All(all, s => Assert.Null(s.Content));
    }

    [Fact]
    public void SnapshotContentCodec_Should_RoundTrip_Bytes_Exactly()
    {
        var compiler = new BundleCompiler();
        var bundle = compiler.Compile("p", "zh-CN",
            [new TranslationLayer("Project:zh-CN", [new TranslationItem("common", "ok", "确定 \"引号\" {x}")])]);

        var packed = SnapshotContentCodec.Pack([("zh-CN", bundle.NestedJsonUtf8Bytes)]);
        var unpacked = SnapshotContentCodec.Unpack(packed);

        Assert.Equal(bundle.NestedJsonUtf8Bytes.ToArray(), unpacked["zh-CN"]);
        Assert.Equal(bundle.ETag, compiler.FromPublishedBytes("p", "zh-CN", unpacked["zh-CN"]).ETag);
    }

    [Fact]
    public void BundleDiff_Should_Count_Added_Changed_Removed()
    {
        var published = Encoding.UTF8.GetBytes("""{ "a": { "x": "1", "y": "2", "z": "3" } }""");
        var draft = Encoding.UTF8.GetBytes("""{ "a": { "x": "1", "y": "22", "w": "4" } }""");

        var diff = BundleDiff.Compare(published, draft);
        Assert.Equal((1, 1, 1), (diff.Added, diff.Changed, diff.Removed));
        Assert.Equal(3, diff.ChangedKeys.Count);

        var neverPublished = BundleDiff.Compare(default, draft, hasPublished: false);
        Assert.Equal(3, neverPublished.Added);
    }
}
