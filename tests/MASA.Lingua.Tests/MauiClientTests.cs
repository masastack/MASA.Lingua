using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.Maui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace MASA.Lingua.Tests;

public sealed class MauiClientTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), $"lingua_maui_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    private LinguaClientOptions Options(string? endpoint) => new()
    {
        ProjectCode = "shop-vue",
        ServerEndpoint = endpoint,
        LocalJsonDirectory = _cache
    };

    [Fact]
    public async Task Initialize_Should_Sync_All_Platform_Cultures_To_Cache_And_Serve_Them()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        using var localizer = new LinguaLocalizer(Options(http.BaseAddress!.ToString()), http);

        var raised = new List<string?>();
        localizer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await localizer.InitializeAsync();

        // 三个语种都来自平台，客户端没有配置任何语种
        Assert.True(File.Exists(Path.Combine(_cache, "zh-CN.json")));
        Assert.True(File.Exists(Path.Combine(_cache, "en-US.json")));
        Assert.True(File.Exists(Path.Combine(_cache, "ja-JP.json")));
        Assert.NotNull(localizer.Version);
        Assert.Contains("Item[]", raised);

        // 平台 manifest 的语种清单透出给语言切换器
        Assert.Equal(3, localizer.AvailableCultures.Count);
        Assert.Contains("zh-CN", localizer.AvailableCultures);
        Assert.Contains("en-US", localizer.AvailableCultures);
        Assert.Contains("ja-JP", localizer.AvailableCultures);

        localizer.SetCulture("en-US");
        Assert.Equal("Checkout (3)", localizer.Get("order.submitBtn", new { count = 3 }));
        Assert.Equal("Welcome back, Alice!", localizer.Get("common:welcomeUser", new { userName = "Alice" }));
        Assert.Contains(nameof(LinguaLocalizer.Culture), raised);

        localizer.SetCulture("ja-JP");
        Assert.Equal("チェックアウト (3)", localizer.Get("order.submitBtn", new { count = 3 }));

        // 缺失的 key 返回 key 本身；未发布的语种回退到平台默认语种
        Assert.Equal("no.such", localizer["no.such"]);
        localizer.SetCulture("fr-FR");
        Assert.Equal("去结算 (3)", localizer.Get("order.submitBtn", new { count = 3 }));
    }

    [Fact]
    public async Task Second_Sync_Should_Not_Change_And_Restart_Should_Reuse_Persisted_Etags()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();

        using (var first = new LinguaLocalizer(Options(http.BaseAddress!.ToString()), http))
        {
            await first.InitializeAsync();
            Assert.False(await first.SyncAsync()); // manifest 304
        }

        Assert.True(File.Exists(Path.Combine(_cache, "lingua.etags")));

        // 模拟重启：新实例先读缓存与已保存的 ETag，不应重复下载任何语种
        using var second = new LinguaLocalizer(Options(http.BaseAddress!.ToString()), http);
        await second.InitializeAsync();
        Assert.False(await second.SyncAsync());
        second.SetCulture("zh-CN");
        Assert.Equal("去结算 (1)", second.Get("order.submitBtn", new { count = 1 }));
    }

    [Fact]
    public async Task Deleted_Cache_File_Should_Be_Redownloaded_On_Next_Sync()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        using var localizer = new LinguaLocalizer(Options(http.BaseAddress!.ToString()), http);

        await localizer.InitializeAsync();
        Assert.False(await localizer.SyncAsync()); // manifest 304：一切未变

        // 缓存文件被系统清理 / 用户删除（进程不重启，manifest ETag 还在内存里）
        File.Delete(Path.Combine(_cache, "en-US.json"));

        // 不能被 304 短路：缺文件的语种必须重新落盘
        Assert.True(await localizer.SyncAsync());
        Assert.True(File.Exists(Path.Combine(_cache, "en-US.json")));

        // 补齐之后恢复 304 短路
        Assert.False(await localizer.SyncAsync());
    }

    [Fact]
    public async Task Sync_Failure_Should_Not_Expose_Cultures_That_Are_Not_On_Disk()
    {
        var handler = new PartialFailureHandler { FailJa = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        using var localizer = new LinguaLocalizer(Options("http://stub"), http);

        // ja-JP 语种文件下载失败：整次同步抛出，语言切换器不能提前列出还没落盘的语种
        await Assert.ThrowsAnyAsync<Exception>(() => localizer.SyncAsync());
        Assert.Empty(localizer.AvailableCultures);

        // 网络恢复后重试成功：缺失语种补上，语种清单这时才完整
        handler.FailJa = false;
        Assert.True(await localizer.SyncAsync());
        Assert.Equal(new[] { "zh-CN", "en-US", "ja-JP" }, localizer.AvailableCultures);
        Assert.True(File.Exists(Path.Combine(_cache, "ja-JP.json")));

        localizer.SetCulture("ja-JP");
        Assert.Equal("ja-JP (1)", localizer.Get("order.submitBtn", new { count = 1 }));
    }

    [Fact]
    public async Task Offline_Start_Should_Use_Cache_And_Not_Throw()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        using (var online = new LinguaLocalizer(Options(http.BaseAddress!.ToString()), http))
        {
            await online.InitializeAsync();
        }

        // 服务端不可达：不抛异常，缓存继续可用
        using var broken = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var offline = new LinguaLocalizer(Options("http://127.0.0.1:1"), broken);
        await offline.InitializeAsync();
        Assert.False(await offline.TrySyncAsync());
        // 直接调用 SyncAsync 会把网络错误（拒绝连接，或超时）抛给调用方
        await Assert.ThrowsAnyAsync<Exception>(() => offline.SyncAsync());
        offline.SetCulture("en-US");
        Assert.Equal("Checkout (2)", offline.Get("order.submitBtn", new { count = 2 }));
    }

    [Fact]
    public async Task Seed_Should_Only_Apply_When_No_Cache_Exists()
    {
        using var localizer = new LinguaLocalizer(Options(null));
        var seed = Encoding.UTF8.GetBytes("""{"order":{"submitBtn":"内置 ({count})"}}""");

        Assert.True(localizer.Seed("zh-CN", seed));
        await localizer.InitializeAsync(); // 没有服务地址：只用本地内容
        localizer.SetCulture("zh-CN");
        Assert.Equal("内置 (1)", localizer.Get("order.submitBtn", new { count = 1 }));

        // 已经有缓存（可能比内置的新）：不覆盖
        var newer = Encoding.UTF8.GetBytes("""{"order":{"submitBtn":"更新 ({count})"}}""");
        Assert.False(localizer.Seed("zh-CN", newer));
        Assert.Equal("内置 (1)", localizer.Get("order.submitBtn", new { count = 1 }));

        // 语种名会变成文件名：拒绝路径穿越
        Assert.False(localizer.Seed("..\\evil", seed));
        Assert.False(localizer.Seed("a/b", seed));
    }

    [Fact]
    public async Task Unpublished_Project_Should_Not_Throw()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var http = factory.CreateClient();
        var options = Options(http.BaseAddress!.ToString());
        options.ProjectCode = "no-such-project";
        using var localizer = new LinguaLocalizer(options, http);

        await localizer.InitializeAsync();
        Assert.False(await localizer.SyncAsync());
        Assert.Null(localizer.Version);
    }

    [Fact]
    public async Task AddLinguaMaui_Should_Register_Localizer_And_StringLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLinguaMaui(o =>
        {
            o.ProjectCode = "shop-vue";
            o.LocalJsonDirectory = _cache;
        });
        await using var provider = services.BuildServiceProvider();

        var localizer = provider.GetRequiredService<LinguaLocalizer>();
        Assert.Same(localizer, provider.GetRequiredService<LinguaLocalizer>());
        Assert.Equal(_cache, localizer.CacheDirectory);

        localizer.Seed("zh-CN", Encoding.UTF8.GetBytes("""{"common":{"welcomeUser":"欢迎，{userName}"}}"""));
        localizer.SetCulture("zh-CN");

        var l = provider.GetRequiredService<IStringLocalizer>();
        Assert.Equal("欢迎，Alice", l["common:welcomeUser", new { userName = "Alice" }].Value);
        Assert.True(l["missing.key"].ResourceNotFound);

        var generic = provider.GetRequiredService<IStringLocalizer<MauiClientTests>>();
        Assert.Equal("欢迎，Bob", generic["common.welcomeUser", new { userName = "Bob" }].Value);

        // 同一个通知器：语种更新事件（MAUI / Blazor Hybrid 的自定义重绘逻辑可订阅它）
        var notified = new List<string>();
        provider.GetRequiredService<ILinguaChangeNotifier>().OnCultureUpdated += notified.Add;
        localizer.SetCulture("en-US");
        Assert.Contains("en-US", notified);
    }

    [Fact]
    public void Relative_Cache_Directory_Should_Live_Under_Local_App_Data()
    {
        var options = new LinguaClientOptions { ProjectCode = "shop-vue" };
        using var localizer = new LinguaLocalizer(options);

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(root, "Lingua", "shop-vue", "i18n"), localizer.CacheDirectory);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, string Path)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); // manifest 404 = 未发布
        }
    }

    /// <summary>manifest 正常、zh-CN / en-US 正常、ja-JP 可控失败（模拟下载中途挂掉，验证语种清单不提前暴露）。</summary>
    private sealed class PartialFailureHandler : HttpMessageHandler
    {
        public volatile bool FailJa;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/manifest", StringComparison.Ordinal))
            {
                var manifest = new ProjectManifest(
                    "shop-vue",
                    "zh-CN",
                    "v1",
                    DateTimeOffset.UnixEpoch,
                    new ManifestCulture[]
                    {
                        new("zh-CN", "中文", LinguaProtocol.BundlePath("shop-vue", "zh-CN"), "\"zh\"", 1, 100),
                        new("en-US", "English", LinguaProtocol.BundlePath("shop-vue", "en-US"), "\"en\"", 1, 100),
                        new("ja-JP", "日本語", LinguaProtocol.BundlePath("shop-vue", "ja-JP"), "\"ja\"", 1, 100),
                    },
                    LinguaProtocol.ManifestPath("shop-vue"),
                    LinguaProtocol.StreamPath("shop-vue"));

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(manifest, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });
            }

            if (FailJa && path.EndsWith("/ja-JP.json", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var culture = Path.GetFileNameWithoutExtension(path);
            var json = "{\"order\":{\"submitBtn\":\"" + culture + " ({count})\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            });
        }
    }

    [Fact]
    public async Task Missing_Key_Should_Never_Be_Reported_By_Client_Sdk()
    {
        // 客户端（MAUI）不做缺词上报：密钥无法安全保管在终端设备，服务端也只接受携带项目写密钥的上报。
        // 回归点：任何代码路径都不允许再向 telemetry 端点发请求。
        var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var options = Options("http://stub");
        options.EnableMissingKeyTelemetry = true; // 即使误开也不得上报
        using var localizer = new LinguaLocalizer(options, http);

        await localizer.InitializeAsync();
        _ = localizer["no.such"];
        _ = localizer["another.miss"];
        await Task.Delay(200);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }
}
