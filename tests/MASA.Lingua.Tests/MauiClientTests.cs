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
using Microsoft.Extensions.Logging;
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
        Assert.NotNull(offline.LastSyncError); // 不抛，但失败原因留给排查
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

    /// <summary>
    /// manifest 正常、zh-CN / en-US 正常、ja-JP 可控失败（模拟下载中途挂掉，验证语种清单不提前暴露）。
    /// 另外可控地模拟「酒店 Wi-Fi 登录页」类的坏响应：manifest 返回 200 + HTML、ja-JP 返回 200 + 声称是 JSON 的 HTML；
    /// 以及平台把 ja-JP 发布成新版本（ETag 变化）。
    /// </summary>
    private sealed class PartialFailureHandler : HttpMessageHandler
    {
        public volatile bool FailJa;
        public volatile bool HtmlManifest;
        public volatile bool HtmlJa;
        public volatile string JaETag = "\"ja\"";

        private static Task<HttpResponseMessage> CaptivePortal(string contentType) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>请先登录 Wi-Fi</body></html>", Encoding.UTF8, contentType)
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/manifest", StringComparison.Ordinal))
            {
                if (HtmlManifest)
                {
                    return CaptivePortal("text/html");
                }

                var manifest = new ProjectManifest(
                    "shop-vue",
                    "zh-CN",
                    "v1",
                    DateTimeOffset.UnixEpoch,
                    new ManifestCulture[]
                    {
                        new("zh-CN", "中文", LinguaProtocol.BundlePath("shop-vue", "zh-CN"), "\"zh\"", 1, 100),
                        new("en-US", "English", LinguaProtocol.BundlePath("shop-vue", "en-US"), "\"en\"", 1, 100),
                        new("ja-JP", "日本語", LinguaProtocol.BundlePath("shop-vue", "ja-JP"), JaETag, 1, 100),
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

            if (HtmlJa && path.EndsWith("/ja-JP.json", StringComparison.OrdinalIgnoreCase))
            {
                // 状态码与内容类型都像一份正常的语言包，内容却不是 JSON：只有解析阶段才能发现
                return CaptivePortal("application/json");
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

    [Fact]
    public async Task TrySync_Should_Never_Throw_And_Should_Record_LastSyncError()
    {
        // 酒店 / 机场 Wi-Fi 登录页：manifest 请求返回 200 + HTML。
        // 响应解析阶段抛出的不是 HttpRequestException，旧实现里会一路抛进调用方。
        var handler = new PartialFailureHandler { HtmlManifest = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        using var localizer = new LinguaLocalizer(Options("http://stub"), http);
        Assert.Null(localizer.LastSyncError);

        Assert.False(await localizer.TrySyncAsync());
        Assert.IsAssignableFrom<Exception>(localizer.LastSyncError);

        // InitializeAsync 同样不抛（启动路径上 await 它也不会崩）
        await localizer.InitializeAsync();
        Assert.NotNull(localizer.LastSyncError);

        // SyncAsync 保持原语义：照常抛出，同时记录
        await Assert.ThrowsAnyAsync<Exception>(() => localizer.SyncAsync());
        Assert.NotNull(localizer.LastSyncError);

        // 网络恢复后同步成功：错误清空
        handler.HtmlManifest = false;
        Assert.True(await localizer.TrySyncAsync());
        Assert.Null(localizer.LastSyncError);
    }

    [Fact]
    public async Task TrySync_Should_Swallow_Misconfigured_Endpoint_But_Not_Caller_Cancellation()
    {
        // 漏写 scheme：HttpClient 在发请求前就抛 InvalidOperationException（不是 HttpRequestException）
        using var http = new HttpClient(new StubHandler());
        using var broken = new LinguaLocalizer(Options("lingua.example.com"), http);
        Assert.False(await broken.TrySyncAsync());
        Assert.IsType<InvalidOperationException>(broken.LastSyncError);

        // 调用方主动取消：仍然抛出，并且不记为同步失败
        using var okHttp = new HttpClient(new PartialFailureHandler()) { BaseAddress = new Uri("http://stub") };
        using var ok = new LinguaLocalizer(Options("http://stub"), okHttp);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ok.TrySyncAsync(cts.Token));
        Assert.Null(ok.LastSyncError);
    }

    private sealed class RecordingLogger : ILogger<LinguaLocalizer>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, exception));
    }

    [Fact]
    public async Task Failed_Sync_Should_Be_Logged_As_A_Warning_Once_But_Cancellation_And_Success_Should_Not()
    {
        // TrySyncAsync / InitializeAsync 不向外抛：日志是线上排查「为什么一直没更新」的第二条线索
        var handler = new PartialFailureHandler { HtmlManifest = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        var logger = new RecordingLogger();
        using var localizer = new LinguaLocalizer(Options("http://stub"), http, logger: logger);

        Assert.False(await localizer.TrySyncAsync());
        var failure = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, failure.Level);
        Assert.NotNull(failure.Exception);
        Assert.Same(localizer.LastSyncError, failure.Exception);

        // 调用方主动取消不是同步故障：不记日志
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => localizer.TrySyncAsync(cts.Token));
        Assert.Single(logger.Entries);

        // 恢复后成功的同步同样不产生警告
        handler.HtmlManifest = false;
        Assert.True(await localizer.TrySyncAsync());
        Assert.Single(logger.Entries);
        Assert.Null(localizer.LastSyncError);
    }

    [Fact]
    public async Task Concurrent_Syncs_Should_Settle_LastSyncError_To_The_Last_Completed_Result()
    {
        // 并发同步在闸门内串行并在闸门内记录结果：先失败的调用不会在后成功的调用之后把旧错误写回来
        var handler = new PartialFailureHandler { HtmlManifest = true };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        using var localizer = new LinguaLocalizer(Options("http://stub"), http);

        var failing = Enumerable.Range(0, 4).Select(_ => localizer.TrySyncAsync()).ToList();
        await Task.WhenAll(failing);
        Assert.All(failing, t => Assert.False(t.Result));
        Assert.NotNull(localizer.LastSyncError);

        handler.HtmlManifest = false;
        var recovering = Enumerable.Range(0, 4).Select(_ => localizer.TrySyncAsync()).ToList();
        await Task.WhenAll(recovering);

        Assert.Null(localizer.LastSyncError);
        Assert.Contains(recovering, t => t.Result); // 有一次真正同步了内容，其余发现 ETag 没变
    }

    [Fact]
    public async Task Invalid_Bundle_Response_Should_Not_Overwrite_Good_Cache()
    {
        var handler = new PartialFailureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        using var localizer = new LinguaLocalizer(Options("http://stub"), http);

        Assert.True(await localizer.SyncAsync());
        var jaPath = Path.Combine(_cache, "ja-JP.json");
        var good = await File.ReadAllBytesAsync(jaPath);
        localizer.SetCulture("ja-JP");
        Assert.Equal("ja-JP (1)", localizer.Get("order.submitBtn", new { count = 1 }));

        // 平台发布了新版本（ja-JP 的 ETag 变了），但这次下载拿到的是「声称是 JSON 的 HTML」
        handler.JaETag = "\"ja-v2\"";
        handler.HtmlJa = true;
        await Assert.ThrowsAnyAsync<JsonException>(() => localizer.SyncAsync());
        Assert.IsAssignableFrom<JsonException>(localizer.LastSyncError);

        // 好缓存原样保留（离线重启仍可用），内存里的旧词条也没动，没有 .tmp 残留
        Assert.Equal(good, await File.ReadAllBytesAsync(jaPath));
        Assert.Equal("ja-JP (1)", localizer.Get("order.submitBtn", new { count = 1 }));
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));

        // ETag 没推进：恢复后下次同步会重新下载这个语种
        handler.HtmlJa = false;
        Assert.True(await localizer.SyncAsync());
        Assert.Null(localizer.LastSyncError);
    }

    [Fact]
    public async Task Failed_Cache_Write_Should_Not_Update_Memory_Or_Advance_ETag()
    {
        // 缓存目录位置被一个普通文件占住：Directory.CreateDirectory 抛 IOException，
        // 模拟磁盘满 / 权限不足 / 被占用等「内容合法但写不进」的情况。
        var blocked = Path.Combine(_cache, "blocked");
        Directory.CreateDirectory(_cache);
        await File.WriteAllTextAsync(blocked, "not a directory");

        var options = Options("http://stub");
        options.LocalJsonDirectory = blocked;
        var handler = new PartialFailureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub") };
        using var localizer = new LinguaLocalizer(options, http);
        Assert.Equal(blocked, localizer.CacheDirectory);

        // 同步：内容能解析，但写盘失败 → 内存里的词条、版本、语种清单都不能被更新，ETag 也不推进
        Assert.False(await localizer.TrySyncAsync());
        Assert.IsAssignableFrom<IOException>(localizer.LastSyncError);
        Assert.Null(localizer.Version);
        Assert.Empty(localizer.AvailableCultures);
        localizer.SetCulture("ja-JP");
        Assert.False(localizer.TryGetValue("order.submitBtn", out _)); // 不会出现「本次会话有新词条、重启后消失」
        Assert.Empty(localizer.GetAll());

        // Seed 同理：解析通过、写盘失败时不得把内容放进内存
        var seed = Encoding.UTF8.GetBytes("""{"order":{"submitBtn":"内置 ({count})"}}""");
        Assert.ThrowsAny<IOException>(() => localizer.Seed("fr-FR", seed));
        Assert.False(localizer.TryGetValue("order.submitBtn", out _));

        // 障碍移除后两条路径都能恢复（失败的那次没有被记成「已同步」）
        File.Delete(blocked);
        handler.JaETag = "\"ja-v2\"";
        Assert.True(await localizer.SyncAsync());
        Assert.Null(localizer.LastSyncError);
        Assert.Equal("ja-JP (1)", localizer.Get("order.submitBtn", new { count = 1 }));
        Assert.True(localizer.Seed("fr-FR", seed)); // 上次没落盘，缓存里没有这个语种，现在能正常放入
        Assert.Equal(
            new[] { "en-US.json", "fr-FR.json", "ja-JP.json", "zh-CN.json" },
            Directory.GetFiles(blocked, "*.json").Select(Path.GetFileName).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Seed_Should_Normalize_Culture_File_Name_And_Reject_Invalid_Json()
    {
        using var localizer = new LinguaLocalizer(Options(null));
        var seed = Encoding.UTF8.GetBytes("""{"order":{"submitBtn":"内置 ({count})"}}""");

        // Android 区分文件名大小写、iOS 不区分：不规范化会在 Android 上写出与 manifest 的 zh-CN.json 并存的第二个文件
        Assert.True(localizer.Seed("zh-cn", seed));
        Assert.Equal(new[] { "zh-CN.json" }, Directory.GetFiles(_cache, "*.json").Select(Path.GetFileName).ToArray());

        // 换一种写法再放同一个语种：同样识别为「缓存里已有」
        Assert.False(localizer.Seed("ZH_cn", seed));
        localizer.SetCulture("zh-cn");
        Assert.Equal("内置 (1)", localizer.Get("order.submitBtn", new { count = 1 }));

        // 通过了安全校验但不符合 ll[-Ssss][-RR] 形状的写法保持原样，不被误拒
        Assert.True(localizer.Seed("x-lingua", seed));
        Assert.True(File.Exists(Path.Combine(_cache, "x-lingua.json")));

        // 内容不是合法 JSON：抛出，并且不留下会永远挡住后续 Seed 的坏缓存文件
        var html = Encoding.UTF8.GetBytes("<html>login</html>");
        Assert.ThrowsAny<JsonException>(() => localizer.Seed("en-US", html));
        Assert.False(File.Exists(Path.Combine(_cache, "en-US.json")));
        Assert.True(localizer.Seed("en-US", seed));
    }

    [Theory]
    [InlineData("lingua.example.com")]
    [InlineData("localhost:5000")]
    [InlineData("ftp://lingua.example.com")]
    public void AddLinguaMaui_Should_Reject_Invalid_ServerEndpoint(string endpoint)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddLinguaMaui(o =>
            {
                o.ProjectCode = "shop-vue";
                o.ServerEndpoint = endpoint;
            }));

        Assert.Contains(endpoint, ex.Message);
    }

    [Fact]
    public void AddLinguaMaui_Should_Reject_Missing_ProjectCode()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddLinguaMaui(o => o.ServerEndpoint = "https://lingua.example.com"));

        Assert.Contains("ProjectCode", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://lingua.example.com")]
    [InlineData("http://10.0.2.2:5000")]
    public void AddLinguaMaui_Should_Accept_Empty_Or_Http_Endpoint(string? endpoint)
    {
        // 空地址是合法的「纯离线 / 仅 Seed」模式；http 允许（开发环境，Android 模拟器走 10.0.2.2）
        new ServiceCollection().AddLinguaMaui(o =>
        {
            o.ProjectCode = "shop-vue";
            o.ServerEndpoint = endpoint;
        });
    }

    [Fact]
    public void AddLinguaMaui_Should_Set_Request_Timeout_That_App_Can_Override()
    {
        var services = new ServiceCollection();
        services.AddLinguaMaui(o => o.ProjectCode = "shop-vue");
        using (var defaults = services.BuildServiceProvider())
        {
            Assert.Equal(
                LinguaMauiExtensions.DefaultRequestTimeout,
                defaults.GetRequiredService<IHttpClientFactory>().CreateClient("LinguaClient").Timeout);
        }

        // 应用对同名客户端再配置一次即可覆盖
        services.AddHttpClient("LinguaClient").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(5));
        using var overridden = services.BuildServiceProvider();
        Assert.Equal(
            TimeSpan.FromSeconds(5),
            overridden.GetRequiredService<IHttpClientFactory>().CreateClient("LinguaClient").Timeout);
    }

    [Fact]
    public async Task Notifications_Should_Be_Raised_On_The_Callers_Context()
    {
        // 线程契约（见 LinguaLocalizer 类注释）：同步里的所有 await 刻意不使用 ConfigureAwait(false)，
        // PropertyChanged / OnCultureUpdated 都在「调用 InitializeAsync / SyncAsync 的上下文」里触发。
        // 在 UI 线程发起同步（App 构造、OnResume），通知就在 UI 线程上。
        // 如果有人给其中某个 await 补了 ConfigureAwait(false)，通知会跑到线程池线程，这个测试会失败。
        using var inner = new PartialFailureHandler();
        using var http = new HttpClient(new ThreadHoppingHandler(inner)) { BaseAddress = new Uri("http://stub") };
        var notifier = new LinguaChangeNotifier();
        using var localizer = new LinguaLocalizer(Options("http://stub"), http, notifier: notifier);
        using var ui = new SingleThreadContext();

        var raisedOn = new ConcurrentBag<int>();
        localizer.PropertyChanged += (_, _) => raisedOn.Add(Environment.CurrentManagedThreadId);
        notifier.OnCultureUpdated += _ => raisedOn.Add(Environment.CurrentManagedThreadId);

        await ui.RunAsync(async () =>
        {
            await localizer.InitializeAsync();
            await localizer.TrySyncAsync();
            localizer.SetCulture("en-US");
        });

        // 确认同步真的跑完了、也真的触发了通知（不是空跑）
        Assert.Equal(3, localizer.AvailableCultures.Count);
        Assert.NotEmpty(raisedOn);
        Assert.All(raisedOn, threadId => Assert.Equal(ui.ThreadId, threadId));
    }

    /// <summary>
    /// 让每次响应都在线程池线程上完成：这样 SDK 里的 await 才真有「回到调用方上下文」与「留在线程池」之分，
    /// 否则同步完成的响应不会发生线程切换，测试发现不了 ConfigureAwait(false) 的回归。
    /// </summary>
    private sealed class ThreadHoppingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(5, ct).ConfigureAwait(false);
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    /// <summary>最小的「UI 线程」：一条专用线程 + 消息队列，await 的续体会被 Post 回这条线程。</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "lingua-test-ui" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task RunAsync(Func<Task> body)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await body();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }, null);
            return done.Task;
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
