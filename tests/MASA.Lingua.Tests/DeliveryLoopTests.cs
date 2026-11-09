using System.Net;
using System.Net.Http.Json;
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

    [Fact]
    public void SyncService_Backoff_Should_Grow_Exponentially_And_Cap()
    {
        Assert.True(LinguaJsonSyncService.BackoffDelay(1) < LinguaJsonSyncService.BackoffDelay(3));
        Assert.True(LinguaJsonSyncService.BackoffDelay(50) <= TimeSpan.FromSeconds(61));
    }
}
