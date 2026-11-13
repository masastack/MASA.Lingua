using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.AspNetCore;
using MASA.Lingua.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 阶段 2：发布到使用的串联——采用情况统计、manifest 协商、客户端按 manifest 同步与热更新。
/// 发布后通知业务端的两条路径是 SSE 与轮询兜底（webhook 已整链路移除）。
/// </summary>
public sealed class DeliveryLoopTests
{
    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void AdoptionTracker_Should_Group_By_Version_Dedupe_Instances_And_Expire()
    {
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var tracker = new AdoptionTracker(time);

        tracker.Record("shop-vue", "vue/aaa", "v2");
        tracker.Record("shop-vue", "vue/bbb", "v1");
        tracker.Record("shop-vue", "aspnetcore/ccc", "v2");
        tracker.Record("shop-vue", "vue/aaa", "v2"); // 同一实例重复上报不重复计数
        tracker.Record("shop-vue", "vue/ddd", null);

        var summary = tracker.GetSummary("shop-vue");
        Assert.Equal(4, summary.TotalInstances);
        var v2 = summary.Versions.Single(v => v.Version == "v2");
        Assert.Equal(2, v2.Instances);
        Assert.Equal(["aspnetcore", "vue"], v2.Kinds);
        Assert.Contains(summary.Versions, v => v.Version == AdoptionTracker.NoVersion && v.Instances == 1);

        // 实例升级版本：同一实例只算在新版本上
        tracker.Record("shop-vue", "vue/bbb", "v2");
        Assert.Equal(3, tracker.GetSummary("shop-vue").Versions.Single(v => v.Version == "v2").Instances);

        // 超出活跃窗口的实例不再统计
        time.Advance(AdoptionTracker.ActiveWindow + TimeSpan.FromMinutes(1));
        Assert.Equal(0, tracker.GetSummary("shop-vue").TotalInstances);
        Assert.Equal(0, tracker.GetSummary("other").TotalInstances);
    }

    [Fact]
    public async Task Manifest_Should_Support_304_And_Record_Adoption_Headers()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/v1/projects/shop-vue/manifest");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag?.Tag;
        Assert.False(string.IsNullOrWhiteSpace(etag));

        var manifest = await first.Content.ReadFromJsonAsync<ProjectManifest>(LinguaJson.Protocol);
        Assert.NotNull(manifest);
        Assert.Equal("shop-vue", manifest!.Project);
        Assert.Contains(manifest.Cultures, c => c.Culture == "en-US" && c.Url.EndsWith("/i18n/shop-vue/en-US.json"));

        using var second = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/shop-vue/manifest");
        second.Headers.TryAddWithoutValidation("If-None-Match", etag);
        second.Headers.TryAddWithoutValidation("X-Lingua-Client", "testclient/abc123");
        second.Headers.TryAddWithoutValidation("X-Lingua-Version", manifest.Version);
        using var secondRes = await client.SendAsync(second);
        Assert.Equal(HttpStatusCode.NotModified, secondRes.StatusCode);

        var summary = factory.Services.GetRequiredService<AdoptionTracker>().GetSummary("shop-vue");
        Assert.Contains(summary.Versions, v => v.Version == manifest.Version && v.Kinds.Contains("testclient"));

        using var missing = await client.GetAsync("/api/v1/projects/nope/manifest");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SyncService_Should_Sync_By_Manifest_Then_Skip_When_Unchanged()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();

        var dir = Path.Combine(Path.GetTempPath(), $"lingua_sync_{Guid.NewGuid():N}");
        try
        {
            var store = new LinguaFrozenMemoryStore();
            var options = new LinguaClientOptions
            {
                ProjectCode = "shop-vue",
                ServerEndpoint = http.BaseAddress!.ToString(),
                LocalJsonDirectory = dir,
                // 没有语种与默认语种选项：以平台项目配置为准
                PollInterval = TimeSpan.Zero,
                EnableServerEvents = false
            };
            using var sync = new LinguaJsonSyncService(
                store, Options.Create(options), new HandlerFactory(new HttpClientHandler()), NullLogger<LinguaJsonSyncService>.Instance);

            Assert.Null(sync.CurrentVersion);
            Assert.True(await sync.SyncAllAsync(http));
            Assert.False(string.IsNullOrWhiteSpace(sync.CurrentVersion));
            Assert.True(store.TryGetValue("en-US", "order.submitBtn", out var text));
            Assert.Equal("Checkout ({count})", text);
            Assert.True(File.Exists(Path.Combine(dir, "en-US.json")));
            Assert.True(File.Exists(Path.Combine(dir, "zh-CN.json")));
            Assert.True(File.Exists(Path.Combine(dir, "ja-JP.json"))); // 平台项目配置了三个语种，业务端没配置也全部同步

            // 版本没变：manifest 304，不再有语种更新
            Assert.False(await sync.SyncAllAsync(http));

            // 服务端已记录这个实例的采用版本（同步成功后会回报）；实例标识 = {ProjectCode}/{随机 12 位}
            var summary = factory.Services.GetRequiredService<AdoptionTracker>().GetSummary("shop-vue");
            Assert.Contains(summary.Versions, v => v.Version == sync.CurrentVersion && v.Kinds.Contains("shop-vue"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task WasmLoader_Initialize_Should_Use_Platform_Default_And_Published_Cultures()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        var store = new LinguaFrozenMemoryStore();
        var sync = new LinguaJsonSyncService(
            store,
            Options.Create(new LinguaClientOptions
            {
                ProjectCode = "shop-vue",
                ServerEndpoint = http.BaseAddress!.ToString(),
                PollInterval = TimeSpan.Zero,
                EnableServerEvents = false
            }),
            new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            NullLogger<LinguaJsonSyncService>.Instance);

        // 请求平台没有发布的语种：同步后仍以本地已加载内容为准；平台已发布语种正常同步
        await sync.SyncAllAsync(http);
        Assert.True(store.TryGetValue("zh-CN", "order.submitBtn", out _));
        Assert.True(store.TryGetValue("en-US", "order.submitBtn", out var en));
        Assert.Equal("Checkout ({count})", en);
    }

    [Fact]
    public async Task SyncService_Should_Apply_Sse_Event_Without_Manifest_Roundtrip()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();

        var dir = Path.Combine(Path.GetTempPath(), $"lingua_event_{Guid.NewGuid():N}");
        try
        {
            var store = new LinguaFrozenMemoryStore();
            var options = new LinguaClientOptions
            {
                ProjectCode = "shop-vue",
                ServerEndpoint = http.BaseAddress!.ToString(),
                LocalJsonDirectory = dir,
                PollInterval = TimeSpan.Zero,
                EnableServerEvents = false
            };
            using var sync = new LinguaJsonSyncService(
                store, Options.Create(options), new HandlerFactory(new HttpClientHandler()), NullLogger<LinguaJsonSyncService>.Instance);

            // 事件载荷就是服务端广播的形状：版本指纹 + 各语种 ETag（不含内容、不含 URL），
            // 客户端据此直接拉变化的语种，不需要先问 manifest「哪个语种变了」。
            var manifest = await http.GetFromJsonAsync<ProjectManifest>("/api/v1/projects/shop-vue/manifest", LinguaJson.Protocol);
            Assert.NotNull(manifest);
            var evt = new BundlePublishedEvent(
                "shop-vue",
                manifest!.Cultures.Select(c => new PublishedCulture(c.Culture, c.ETag)).ToList(),
                manifest.DefaultCulture,
                manifest.Version,
                DateTimeOffset.UtcNow);

            await sync.SyncFromEventAsync(evt, http);

            Assert.Equal(manifest.Version, sync.CurrentVersion);
            Assert.True(store.TryGetValue("en-US", "order.submitBtn", out var text));
            Assert.Equal("Checkout ({count})", text);
            // 事件里列出的语种全部落盘（并行下载，限流 3）
            Assert.True(File.Exists(Path.Combine(dir, "zh-CN.json")));
            Assert.True(File.Exists(Path.Combine(dir, "ja-JP.json")));

            // 同一版本重复投递：不产生任何下载，版本不变
            await sync.SyncFromEventAsync(evt, http);
            Assert.Equal(manifest.Version, sync.CurrentVersion);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // --- 本地文件对账 / 语种名安全 ---------------------------------------------

    /// <summary>
    /// 可编排的假平台：按 <see cref="Cultures"/> / <see cref="Version"/> 返回 manifest 与语言包，
    /// 支持 If-None-Match → 304，并记录每个请求（路径 + 是否带条件头）。语种名不做任何校验（模拟异常 / 被篡改的 manifest）。
    /// </summary>
    private sealed class StubPlatform : HttpMessageHandler
    {
        public const string Project = "stub-app";

        public string Version { get; set; } = "v1";

        public List<string> Cultures { get; set; } = ["en-US"];

        public List<(string PathAndQuery, bool Conditional)> Requests { get; } = [];

        private string BundleETag(string culture) => $"\"stub-{culture}-{Version}\"";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ifNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values) ? string.Join(",", values) : null;
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.PathAndQuery, ifNoneMatch is not null));
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/manifest", StringComparison.Ordinal))
            {
                var manifest = new ProjectManifest(
                    Project, "en-US", Version, DateTimeOffset.UtcNow,
                    Cultures.Select(c => new ManifestCulture(
                        c, c, $"http://stub.test/i18n/{Project}/{c}.json", BundleETag(c), 1, 100)).ToList(),
                    $"http://stub.test/api/v1/projects/{Project}/manifest",
                    $"http://stub.test/api/v1/stream?project={Project}");
                return Task.FromResult(Respond(manifest.ETag, JsonSerializer.Serialize(manifest, LinguaJson.Protocol), ifNoneMatch));
            }

            if (path.EndsWith(".json", StringComparison.Ordinal))
            {
                var culture = Path.GetFileNameWithoutExtension(path);
                return Task.FromResult(Respond(BundleETag(culture), $$"""{ "hello": "hello-{{culture}}" }""", ifNoneMatch));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Respond(string etag, string json, string? ifNoneMatch)
        {
            if (ifNoneMatch is not null && ifNoneMatch.Contains(etag, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return response;
        }
    }

    private static LinguaJsonSyncService NewSync(
        LinguaFrozenMemoryStore store, string projectCode, string localDirectory) =>
        new(
            store,
            Options.Create(new LinguaClientOptions
            {
                ProjectCode = projectCode,
                ServerEndpoint = "http://stub.test/",
                LocalJsonDirectory = localDirectory,
                PollInterval = TimeSpan.Zero,
                EnableServerEvents = false
            }),
            new HandlerFactory(new HttpClientHandler()),
            NullLogger<LinguaJsonSyncService>.Instance);

    [Fact]
    public async Task SyncService_Should_Restore_Deleted_Local_File_Even_When_Manifest_Is_Unchanged()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();

        var dir = Path.Combine(Path.GetTempPath(), $"lingua_restore_{Guid.NewGuid():N}");
        try
        {
            using var sync = NewSync(new LinguaFrozenMemoryStore(), "shop-vue", dir);

            Assert.True(await sync.SyncAllAsync(http));
            var zh = Path.Combine(dir, "zh-CN.json");
            Assert.True(File.Exists(zh));

            // 线上没变，但本地文件被清理 / 误删：不能被 manifest 的 304 永远拦在门外
            File.Delete(zh);
            Assert.True(await sync.SyncAllAsync(http));
            Assert.True(File.Exists(zh));

            // 补回之后恢复 304 快路径
            Assert.False(await sync.SyncAllAsync(http));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SyncService_Should_Not_Fall_Back_To_Full_Manifest_Forever_After_A_Culture_Is_Removed()
    {
        var platform = new StubPlatform { Cultures = ["en-US", "fr-FR"], Version = "v1" };
        using var http = new HttpClient(platform, disposeHandler: false);
        var dir = Path.Combine(Path.GetTempPath(), $"lingua_stale_{Guid.NewGuid():N}");
        try
        {
            using var sync = NewSync(new LinguaFrozenMemoryStore(), StubPlatform.Project, dir);

            Assert.True(await sync.SyncAllAsync(http));
            Assert.True(File.Exists(Path.Combine(dir, "fr-FR.json")));

            // 平台移除 fr-FR 并发布新版本；本地 fr-FR.json 保留给离线兜底，之后被人清掉
            platform.Cultures = ["en-US"];
            platform.Version = "v2";
            Assert.True(await sync.SyncAllAsync(http));
            File.Delete(Path.Combine(dir, "fr-FR.json"));

            // 已被平台移除的语种不再算「缺文件」：manifest 仍走条件请求，拿到 304，不下载任何语种
            platform.Requests.Clear();
            Assert.False(await sync.SyncAllAsync(http));
            var manifestRequest = Assert.Single(platform.Requests);
            Assert.EndsWith("/manifest", manifestRequest.PathAndQuery);
            Assert.True(manifestRequest.Conditional, "已移除语种的陈旧 ETag 不应让对账退回全量 manifest。");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SyncService_Should_Skip_Unsafe_Culture_Names_From_Manifest_And_Events()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lingua_unsafe_{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "i18n");
        try
        {
            // manifest 路径：「../evil」拼进路径会落到 i18n 目录之外（root/evil.json）
            var platform = new StubPlatform { Cultures = ["en-US", "../evil", "a/b", "x\\y"] };
            using var http = new HttpClient(platform, disposeHandler: false);
            using (var sync = NewSync(new LinguaFrozenMemoryStore(), StubPlatform.Project, dir))
            {
                Assert.True(await sync.SyncAllAsync(http));
            }

            Assert.Equal(["en-US.json"], Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).ToArray());
            Assert.False(File.Exists(Path.Combine(root, "evil.json")));
            Assert.DoesNotContain(platform.Requests, r => r.PathAndQuery.Contains("evil", StringComparison.Ordinal));

            // SSE 事件路径：同样的过滤
            platform.Requests.Clear();
            using (var sync = NewSync(new LinguaFrozenMemoryStore(), StubPlatform.Project, Path.Combine(root, "i18n-event")))
            {
                var evt = new BundlePublishedEvent(
                    StubPlatform.Project,
                    [new PublishedCulture("en-US", "\"stub-en-US-v1\""), new PublishedCulture("../evil", "\"stub-../evil-v1\"")],
                    "en-US",
                    "v1",
                    DateTimeOffset.UtcNow);
                await sync.SyncFromEventAsync(evt, http);
            }

            Assert.Equal(["en-US.json"], Directory.GetFiles(Path.Combine(root, "i18n-event")).Select(f => Path.GetFileName(f)!).ToArray());
            Assert.False(File.Exists(Path.Combine(root, "evil.json")));
            Assert.DoesNotContain(platform.Requests, r => r.PathAndQuery.Contains("evil", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("zh CN")]
    public async Task SyncCultureFileAsync_Should_Reject_Unsafe_Culture_Names(string culture)
    {
        using var sync = NewSync(new LinguaFrozenMemoryStore(), StubPlatform.Project, Path.Combine(Path.GetTempPath(), $"lingua_guard_{Guid.NewGuid():N}"));
        using var http = new HttpClient(new StubPlatform(), disposeHandler: false);

        await Assert.ThrowsAsync<ArgumentException>(() => sync.SyncCultureFileAsync(culture, http));
    }

    [Fact]
    public void SyncService_Backoff_Should_Grow_Exponentially_And_Cap()
    {
        Assert.True(LinguaJsonSyncService.BackoffDelay(1) < LinguaJsonSyncService.BackoffDelay(3));
        Assert.True(LinguaJsonSyncService.BackoffDelay(50) <= TimeSpan.FromSeconds(61));
    }
}
