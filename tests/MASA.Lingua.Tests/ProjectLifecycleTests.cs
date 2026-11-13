using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 项目生命周期（创建 → 发布 → 删除 → 同名重建）与分发缓存的一致性：
/// 项目删除后发布器里的内存缓存、磁盘产物必须随之清理；从未发布过的项目不能采信磁盘上的孤儿文件；
/// 并发创建同一项目只有赢家拿到写密钥。
/// </summary>
public sealed class ProjectLifecycleTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static string BundleDir(IsolatedLinguaFactory factory, string code) =>
        Path.Combine(factory.WebRoot, "i18n", code);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>创建项目、导入一条 zh-CN 根级词条并强制发布（跳过门禁，只关心分发链路）。</summary>
    private static async Task CreateAndPublishAsync(
        IsolatedLinguaFactory factory, HttpClient client, string code, string value)
    {
        using var create = await client.PostAsync($"/api/v1/projects/{code}", content: null);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var import = await client.PostAsync(
            $"/api/v1/projects/{code}/import-json?culture=zh-CN",
            Json($$"""{ "hello": "{{value}}" }"""));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var publisher = factory.Services.GetRequiredService<StaticBundlePublisher>();
        var snapshot = await publisher.PublishProjectAsync(code, new PublishRequest("test", Force: true, ForceReason: "test"));
        Assert.NotNull(snapshot);
    }

    // --- 删除项目：缓存与磁盘产物一并清理 -------------------------------------

    [Fact]
    public async Task Delete_Should_Purge_Cache_And_Published_Files_So_A_Recreated_Project_Starts_Clean()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "lifecycle-app";

        await CreateAndPublishAsync(factory, client, code, "HELLO-OLD");

        using (var live = await client.GetAsync($"/i18n/{code}/zh-CN.json"))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Contains("HELLO-OLD", await live.Content.ReadAsStringAsync());
        }
        Assert.True(File.Exists(Path.Combine(BundleDir(factory, code), "zh-CN.json")));

        using (var delete = await client.DeleteAsync($"/api/v1/projects/{code}"))
        {
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        }

        // 项目没了：内存缓存必须一起没了，不能再对外返回 200
        using (var gone = await client.GetAsync($"/i18n/{code}/zh-CN.json"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        using (var goneManifest = await client.GetAsync($"/api/v1/projects/{code}/manifest"))
        {
            Assert.Equal(HttpStatusCode.NotFound, goneManifest.StatusCode);
        }
        Assert.False(Directory.Exists(BundleDir(factory, code)));

        // 同名重建：没发布过之前读不到上一个项目的任何产物，也没有 manifest
        using (var recreate = await client.PostAsync($"/api/v1/projects/{code}", content: null))
        {
            var payload = await recreate.Content.ReadFromJsonAsync<Dictionary<string, object?>>(WebJson);
            Assert.Equal("True", payload?["created"]?.ToString());
        }
        using (var fresh = await client.GetAsync($"/i18n/{code}/zh-CN.json"))
        {
            Assert.Equal(HttpStatusCode.NotFound, fresh.StatusCode);
        }
        using (var freshManifest = await client.GetAsync($"/api/v1/projects/{code}/manifest"))
        {
            Assert.Equal(HttpStatusCode.NotFound, freshManifest.StatusCode);
        }

        // 发布新内容后只出新项目的内容
        using var import = await client.PostAsync(
            $"/api/v1/projects/{code}/import-json?culture=zh-CN", Json("""{ "hello": "HELLO-NEW" }"""));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var publisher = factory.Services.GetRequiredService<StaticBundlePublisher>();
        await publisher.PublishProjectAsync(code, new PublishRequest("test", Force: true, ForceReason: "test"));

        using var republished = await client.GetAsync($"/i18n/{code}/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, republished.StatusCode);
        var body = await republished.Content.ReadAsStringAsync();
        Assert.Contains("HELLO-NEW", body);
        Assert.DoesNotContain("HELLO-OLD", body);
    }

    [Fact]
    public async Task Delete_Should_Forget_The_Last_Sse_Event_Of_The_Project()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "lifecycle-sse";
        var broadcaster = factory.Services.GetRequiredService<LinguaSseBroadcaster>();

        await CreateAndPublishAsync(factory, client, code, "HELLO");
        Assert.True(broadcaster.TryGetLast(code, out _));

        using var delete = await client.DeleteAsync($"/api/v1/projects/{code}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        // 同名重建的项目不能被补发一条指向已不存在产物的陈旧事件
        Assert.False(broadcaster.TryGetLast(code, out _));
    }

    // --- 从未发布过的项目不采信磁盘上的孤儿文件 -----------------------------

    [Fact]
    public async Task Never_Published_Project_Should_Not_Serve_Or_Diff_Against_Orphan_Files()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "orphan-app";

        using var create = await client.PostAsync($"/api/v1/projects/{code}", content: null);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // 模拟遗留：目录里有同名旧项目留下的产物
        var dir = BundleDir(factory, code);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "zh-CN.json"), """{"stale":"STALE-VALUE"}""");

        using var res = await client.GetAsync($"/i18n/{code}/zh-CN.json");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);

        // 草稿差异不能拿孤儿文件当「线上版本」
        var publisher = factory.Services.GetRequiredService<StaticBundlePublisher>();
        Assert.True((await publisher.GetDraftDiffAsync(code)).NeverPublished);
    }

    [Fact]
    public async Task Startup_Recovery_Should_Bootstrap_Publish_Instead_Of_Restoring_Orphan_Files()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "orphan-boot";

        using var create = await client.PostAsync($"/api/v1/projects/{code}", content: null);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var dir = BundleDir(factory, code);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "zh-CN.json"), """{"stale":"STALE-VALUE"}""");
        // 不属于任何语种的残留
        await File.WriteAllTextAsync(Path.Combine(dir, "fr-FR.json"), """{"stale":"STALE-FR"}""");

        // 启动恢复：数据库里没有发布记录，必须走引导发布（返回 true），而不是把孤儿文件恢复成线上版本
        var publisher = factory.Services.GetRequiredService<StaticBundlePublisher>();
        Assert.True(await publisher.EnsurePublishedAsync(code));

        using var res = await client.GetAsync($"/i18n/{code}/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("STALE-VALUE", await res.Content.ReadAsStringAsync());

        // 发布会清掉不属于线上版本的残留文件
        Assert.False(File.Exists(Path.Combine(dir, "fr-FR.json")));
    }

    // --- 并发创建：只有赢家拿到写密钥 --------------------------------------

    [Fact]
    public async Task Concurrent_Skeleton_Create_Should_Hand_Out_The_Write_Key_Only_To_The_Creator()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "race-app";

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var res = await client.PostAsync($"/api/v1/projects/{code}", content: null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadFromJsonAsync<Dictionary<string, object?>>(WebJson);
        }));

        var creators = responses.Where(r => r!["created"]?.ToString() == "True").ToList();
        var others = responses.Where(r => r!["created"]?.ToString() != "True").ToList();

        var creator = Assert.Single(creators);
        Assert.StartsWith("lingua_sk_", creator!["writeKey"]?.ToString());
        Assert.All(others, r => Assert.False(r!.ContainsKey("writeKey")));

        // 创建者拿到的密钥就是库里的那一把
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        Assert.Equal(creator["writeKey"]?.ToString(), (await repo.GetProjectByCodeAsync(code))!.WriteKey);
    }

    [Fact]
    public async Task CreateProjectIfAbsent_Should_Report_Created_Only_For_The_Inserting_Call()
    {
        await using var factory = new IsolatedLinguaFactory();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();

        var first = await repo.CreateProjectIfAbsentAsync("created-flag");
        var second = await repo.CreateProjectIfAbsentAsync("created-flag");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Project.Id, second.Project.Id);
        Assert.Equal(first.Project.Id, (await repo.CreateProjectAsync("created-flag")).Id);
    }

    // --- 项目编码校验 ---------------------------------------------------------

    [Theory]
    [InlineData("bad.code")]
    [InlineData("bad%20code")]
    [InlineData("%E9%A1%B9%E7%9B%AE")] // 「项目」
    public async Task Create_Should_Reject_Unsafe_Project_Codes_With_400_And_Create_Nothing(string code)
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"/api/v1/projects/{code}", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("项目编码不合法", await response.Content.ReadAsStringAsync());

        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        Assert.Null(await repo.GetProjectByCodeAsync(Uri.UnescapeDataString(code)));
        Assert.False(Directory.Exists(BundleDir(factory, Uri.UnescapeDataString(code))));
    }

    [Fact]
    public async Task Create_Should_Reject_A_Code_Longer_Than_The_Column_Width()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var tooLong = await client.PostAsync($"/api/v1/projects/{new string('a', LinguaProjectCodes.MaxLength + 1)}", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var atLimit = await client.PostAsync($"/api/v1/projects/{new string('a', LinguaProjectCodes.MaxLength)}", content: null);
        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public async Task Repository_Should_Refuse_To_Create_A_Project_With_A_Path_Like_Code(string code)
    {
        await using var factory = new IsolatedLinguaFactory();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();

        // 校验在仓储层兜底：即使绕过 HTTP 端点（后台界面、PM 同步）也建不出会写到发布目录之外的项目
        await Assert.ThrowsAsync<LinguaValidationException>(() => repo.CreateProjectAsync(code));
        await Assert.ThrowsAsync<LinguaValidationException>(() => repo.CreateProjectIfAbsentAsync(code));
        Assert.Null(await repo.GetProjectByCodeAsync(code));
    }

    // --- 遗留的非法编码项目 -------------------------------------------------

    [Fact]
    public async Task Legacy_Project_With_Unsafe_Code_Should_Get_404_Not_500_And_Remain_Deletable()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "legacy.app"; // 「.」不在合法字符集内：模拟校验上线前入库的项目

        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(new Project
            {
                Code = code,
                DefaultCulture = "zh-CN",
                SupportedCultures = ["zh-CN"],
                SharedNamespaceCodes = []
            });
            await db.SaveChangesAsync();
        }

        using var bundle = await client.GetAsync($"/i18n/{code}/zh-CN.json");
        Assert.Equal(HttpStatusCode.NotFound, bundle.StatusCode);

        using var manifest = await client.GetAsync($"/api/v1/projects/{code}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, manifest.StatusCode);

        // 文档约定的处置方式是「删除后用合规编码重建」：删除不能被发布目录校验卡住
        using var delete = await client.DeleteAsync($"/api/v1/projects/{code}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    // --- If-None-Match：弱校验器 --------------------------------------------

    [Fact]
    public async Task IfNoneMatch_Should_Match_Weak_Validators_And_Lists()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/i18n/shop-vue/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var strong = first.Headers.ETag!.Tag; // 带引号的不透明标签

        async Task<HttpStatusCode> Conditional(string ifNoneMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/i18n/shop-vue/zh-CN.json");
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            using var res = await client.SendAsync(request);
            return res.StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotModified, await Conditional(strong));
        Assert.Equal(HttpStatusCode.NotModified, await Conditional($"W/{strong}")); // 压缩中间件会把强标签改弱
        Assert.Equal(HttpStatusCode.NotModified, await Conditional($"\"other\", W/{strong}"));
        Assert.Equal(HttpStatusCode.NotModified, await Conditional("*"));
        Assert.Equal(HttpStatusCode.OK, await Conditional("\"other\""));
        Assert.Equal(HttpStatusCode.OK, await Conditional($"WW/{strong}")); // 不是合法的弱前缀，不能被多吃掉
    }

    // --- 缺词上报：语种线索 --------------------------------------------------

    [Fact]
    public async Task MissingKey_Report_Should_Record_The_Reporting_Culture_As_Context_Only()
    {
        await using var factory = new IsolatedLinguaFactory();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        var project = await repo.GetProjectByCodeAsync("shop-vue");
        Assert.NotNull(project);

        var added = await repo.RecordMissingKeysAsync(new MissingKeyBatchReport(
            "shop-vue", "en-US", [new MissingKeyReportItem("order", "missingCtx", "源文案")]));
        Assert.Equal(1, added);

        // 外部传入的语种不是安全片段时不落库
        await repo.RecordMissingKeysAsync(new MissingKeyBatchReport(
            "shop-vue", "x\n'; DROP TABLE", [new MissingKeyReportItem("order", "missingHostile", "源文案")]));

        var stored = await repo.GetProjectByCodeAsync("shop-vue");
        var entries = stored!.Namespaces.First(n => n.Code == "order").Entries;

        var entry = entries.Single(e => e.Key == "missingCtx");
        Assert.Contains("en-US", entry.Context);
        // 源语言文案只作为默认语种的草稿存在，不会被当成 en-US 的译文
        var translation = Assert.Single(entry.Translations);
        Assert.Equal(project!.DefaultCulture, translation.Culture);

        var hostile = entries.Single(e => e.Key == "missingHostile");
        Assert.DoesNotContain("DROP", hostile.Context);
    }
}
