using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Components.Pages;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

// 直接驱动 Renderer 来跑页面生命周期（与 EntryRowRenderTests 同一手法，不引入 bUnit）。
#pragma warning disable BL0006

namespace MASA.Lingua.Tests;

/// <summary>
/// 顶栏切换项目后页面必须「结束加载骨架并显示新项目的数据」。
/// <para>
/// 这条测试防的是一个真实事故：<c>Home.OnScopeChanged</c> 用 <c>_loadVersion + 1</c> 取值而没有调用
/// <see cref="LinguaPageBase.NextLoadVersion"/>，代数永远追不上自己 → 加载结果被判为过期丢弃、
/// <c>EndLoad</c> 也不会结束加载态，页面永远停在「正在加载当前项目的词条数据…」。
/// </para>
/// </summary>
public sealed class ProjectSwitchLoadingTests
{
    private const string FirstProject = "switch-first";
    private const string SecondProject = "switch-second";

    /// <summary>
    /// 页面宿主：经 RenderTreeBuilder 打开 <see cref="Home"/>，让框架照常做 [Inject] 属性注入
    /// （直接 new 再用 AssignRootComponentId 挂成根组件不会注入服务，页面一初始化就 NRE）。
    /// </summary>
    private sealed class Host : ComponentBase
    {
        public ProbeHome? Page { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ProbeHome>(0);
            builder.AddComponentReferenceCapture(1, c => Page = (ProbeHome)c);
            builder.CloseComponent();
        }
    }

    /// <summary>探针页面：<c>_isLoading</c> 是基类的 protected 成员，词条列表则只能反射读取（页面私有状态）。</summary>
    private sealed class ProbeHome : Home
    {
        private static readonly FieldInfo FilteredEntries = typeof(Home)
            .GetField("_filteredEntries", BindingFlags.NonPublic | BindingFlags.Instance)!;

        public bool Loading => _isLoading;

        public IReadOnlyList<TranslationEntry> Entries =>
            (IReadOnlyList<TranslationEntry>)FilteredEntries.GetValue(this)!;
    }

    /// <summary>本用例不会调用 AI：翻译入口只要求「存在」，不参与加载。</summary>
    private sealed class StubAi : IAiTranslationService
    {
        public Task<IReadOnlyList<AiTranslationResultItem>> TranslateBatchAsync(
            string projectCode,
            string sourceCulture,
            IReadOnlyList<string> targetCultures,
            IReadOnlyList<AiTranslationRequestItem> items,
            IReadOnlyList<GlossaryTerm> glossaryTerms,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiTranslationResultItem>>([]);
    }

    private sealed class StubJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(InvokeCore<TValue>());

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(InvokeCore<TValue>());

        // Masa 的 JSModule.DisposeAsync 会在模块未加载（返回 null）时空引用：给一个可释放的桩模块
        private static TValue InvokeCore<TValue>() =>
            typeof(TValue) == typeof(IJSObjectReference) ? (TValue)(object)new StubObjectReference() : default!;
    }

    private sealed class StubObjectReference : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
    {
        public TestNavigationManager() => Initialize("https://test.localhost/", "https://test.localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class TestEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MASA.Lingua.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        // Masa 组件（MNavigationDrawer 等）会读取 ElementReference 的 Context 生成选择器：
        // 测试渲染器没有浏览器，给一个空上下文让 GetSelector 走通（选择器内容不参与断言）
        private sealed class TestElementReferenceContext : Microsoft.AspNetCore.Components.ElementReferenceContext
        {
        }

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public Task RenderRootAsync(IComponent component)
        {
            ElementReferenceContext = new TestElementReferenceContext();
            var id = AssignRootComponentId(component);
            return Dispatcher.InvokeAsync(() => RenderRootComponentAsync(id));
        }
    }

    private static byte[] Bundle(params (string Key, string Text)[] items) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(items.ToDictionary(i => i.Key, i => i.Text)));

    private static async Task SeedAsync(ILinguaRepository repo, string projectCode, params (string Key, string Text)[] items)
    {
        await repo.CreateProjectAsync(projectCode);
        await repo.ImportJsonBundleAsync(projectCode, "zh-CN", Bundle(items), "page");
    }

    /// <summary>在渲染线程上轮询条件：既读页面状态，也给加载的续体让出调度机会。</summary>
    private static async Task<bool> WaitAsync(Renderer renderer, Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await renderer.Dispatcher.InvokeAsync(condition))
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await renderer.Dispatcher.InvokeAsync(condition);
    }

    [Fact]
    public async Task Switching_Project_Ends_Loading_And_Shows_New_Project_Entries()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_switch");
        await SeedAsync(db.Repo, FirstProject, ("onlyInFirst", "第一个项目"));
        await SeedAsync(db.Repo, SecondProject, ("firstOfSecond", "第二个项目"), ("secondOfSecond", "第二个项目第二条"));

        var root = Path.Combine(Path.GetTempPath(), $"lingua_switch_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLinguaEfCoreStorage(db.ConnectionString);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<IWebHostEnvironment>(new TestEnv(root));
            services.AddSingleton<IAiTranslationService>(new StubAi());
            services.AddSingleton<IJSRuntime>(new StubJs());
            services.AddSingleton<DeterministicTranslationValidator>();
            services.AddSingleton<BundleCompiler>();
            services.AddSingleton<LinguaSseBroadcaster>();
            services.AddSingleton<AdoptionTracker>();
            services.AddSingleton<StaticBundlePublisher>();
            services.AddSingleton<EntryAiTranslator>();
            services.AddScoped<WorkspaceScope>();
            services.AddScoped<ToastService>();
            // Home 迁移 Masa.Blazor 后的组件（MSimpleTable/MSelect/MChip/MButton 等）需要 MasaBlazor 服务
            services.AddMasaBlazor();
            // MButton / MChip 注入 NavigationManager（Href 处理用）
            services.AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(new TestNavigationManager());

            await using var provider = services.BuildServiceProvider();
            // Masa 组件（TransitionJSModule 等）只实现 IAsyncDisposable：作用域必须异步释放
            await using var scope = provider.CreateAsyncScope();

            // 先把范围落到第一个项目：初次加载就按它走，随后的变化才是「切换项目」
            scope.ServiceProvider.GetRequiredService<WorkspaceScope>().Select(FirstProject);

            using var renderer = new TestRenderer(scope.ServiceProvider);
            var host = new Host();
            await renderer.RenderRootAsync(host);
            var home = await renderer.Dispatcher.InvokeAsync(() => host.Page!);

            Assert.True(
                await WaitAsync(renderer, () => !home.Loading, TimeSpan.FromSeconds(20)),
                "初次加载没有结束加载态（页面会一直停在加载骨架）");
            var firstKeys = await renderer.Dispatcher.InvokeAsync(() => home.Entries.Select(e => e.Key).ToArray());
            Assert.Contains("onlyInFirst", firstKeys);
            Assert.DoesNotContain("firstOfSecond", firstKeys);

            // 切换项目：加载必须收尾，并把新项目的词条换进来
            await renderer.Dispatcher.InvokeAsync(() =>
                scope.ServiceProvider.GetRequiredService<WorkspaceScope>().Select(SecondProject));

            Assert.True(
                await WaitAsync(renderer, () => !home.Loading, TimeSpan.FromSeconds(20)),
                "切换项目后加载态没有结束：页面会永远停在「正在加载当前项目的词条数据…」");

            var keys = await renderer.Dispatcher.InvokeAsync(() => home.Entries.Select(e => e.Key).ToArray());
            Assert.Contains("firstOfSecond", keys);
            Assert.Contains("secondOfSecond", keys);
            Assert.DoesNotContain("onlyInFirst", keys); // 上一个项目的词条不能残留
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
