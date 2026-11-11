using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Masa.Blazor;
using Xunit;

// 这组测试要直接驱动 Renderer / RenderBatch 才能统计「这一行真正重渲染了几次」；不引入 bUnit 就绕不开这些框架内部类型。
// 抑制范围限定在本文件，BL0006 提示的是「框架未来可能改这些类型」，风险仅在测试内。
#pragma warning disable BL0006

namespace MASA.Lingua.Tests;

/// <summary>
/// 词条矩阵的一行（<see cref="EntryRow"/>）：渲染内容正确，且只有「这一行用到的输入」变了才重渲染。
/// 页面（Home）每次重渲染都会让这一页上百行重新走一遍参数传递，ShouldRender 是它不至于全量 diff 的关键。
/// 没有 bUnit：渲染内容用官方 <see cref="HtmlRenderer"/> 取 HTML，重渲染次数用自建的最小 Renderer + 计数子类统计。
/// </summary>
public sealed class EntryRowRenderTests
{
    private static readonly IServiceProvider Services = CreateServices();

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // EntryRow 迁移 Masa.Blazor 后的 MChip / MButton / MProgressCircular / MIcon 需要 MasaBlazor 服务
        services.AddMasaBlazor();
        // MButton / MChip 注入 NavigationManager（Href 处理用）
        services.AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(new StubNavigationManager());
        // MButton ripple 等 OnAfterRender 阶段的 JS 调用：测试渲染走短路桩
        services.AddSingleton<IJSRuntime>(new StubJs());
        return services.BuildServiceProvider();
    }

    private sealed class StubNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
    {
        public StubNavigationManager() => Initialize("https://test.localhost/", "https://test.localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    /// <summary>
    /// JS 桩：MButton 的 ripple、MMenu 的 activatable 模块都会在 AfterRender 阶段调 JS。
    /// 返回非 null 的 <see cref="IJSObjectReference"/> 是必须的 —— 激活类组件会继续对它调
    /// <c>invokeMethodAsync</c>，给 null 会 NRE（不是「JS 未实现」而是「引用为空」）。
    /// </summary>
    private sealed class StubJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (typeof(TValue) == typeof(IJSObjectReference) || typeof(TValue) == typeof(IJSInProcessObjectReference))
            {
                return ValueTask.FromResult((TValue)(object)new StubJsObject());
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    /// <summary>JS 模块引用桩：所有 invoke 调用都是空实现。</summary>
    private sealed class StubJsObject : IJSObjectReference, IJSInProcessObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public TValue Invoke<TValue>(string identifier, params object?[]? args) => default!;

        void IDisposable.Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly string[] AllCultures = ["zh-CN", "en-US", "ja-JP"];
    private static readonly string[] Targets = ["en-US", "ja-JP"];

    private static readonly IReadOnlyList<ValidationIssue> NoNotes = [];

    // ---------------------------------------------------------------------
    // 参数构造
    // ---------------------------------------------------------------------

    private static TranslationEntry NewEntry(
        string key = "submit",
        string source = "提交",
        string? namespaceCode = "order",
        params (string Culture, string Text, TranslationStatus Status)[] translations)
    {
        var entry = new TranslationEntry
        {
            Key = key,
            SourceText = source,
            Namespace = namespaceCode is null ? null : new TranslationNamespace { Code = namespaceCode }
        };
        entry.Translations.Add(new TranslationValue { EntryId = entry.Id, Culture = "zh-CN", Text = source, Status = TranslationStatus.Approved });
        foreach (var (culture, text, status) in translations)
        {
            entry.Translations.Add(new TranslationValue { EntryId = entry.Id, Culture = culture, Text = text, Status = status });
        }

        return entry;
    }

    private sealed record RowState(
        TranslationEntry Entry,
        IReadOnlyList<string> Cultures,
        IReadOnlyList<string> TargetCultures,
        IReadOnlyList<IReadOnlyList<ValidationIssue>> Notes,
        string DefaultCulture = "zh-CN",
        bool IsSharedScope = false,
        SharedEntryImpact? Impact = null,
        bool Busy = false,
        EntryRow.AiState Ai = EntryRow.AiState.Idle,
        bool AnyAiBusy = false,
        int DiscardToken = 0)
    {
        public static RowState For(TranslationEntry entry) =>
            new(entry, AllCultures, Targets, [NoNotes, NoNotes, NoNotes]);
    }

    // ---------------------------------------------------------------------
    // 渲染内容
    // ---------------------------------------------------------------------

    /// <summary>渲染并解码 HTML 实体（HtmlRenderer 会把非 ASCII 字符编码成 &#x…;），便于直接按中文文案断言。</summary>
    private static async Task<string> RenderHtmlAsync(RowState s) => WebUtility.HtmlDecode(await RenderRawAsync(s));

    /// <summary>原始 HTML（保留编码）：用来断言文本确实被转义。</summary>
    private static async Task<string> RenderRawAsync(RowState s)
    {
        await using var renderer = new HtmlRenderer(Services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(EntryRow.Entry)] = s.Entry,
                [nameof(EntryRow.Cultures)] = s.Cultures,
                [nameof(EntryRow.TargetCultures)] = s.TargetCultures,
                [nameof(EntryRow.DefaultCulture)] = s.DefaultCulture,
                [nameof(EntryRow.Notes)] = s.Notes,
                [nameof(EntryRow.IsSharedScope)] = s.IsSharedScope,
                [nameof(EntryRow.Impact)] = s.Impact,
                [nameof(EntryRow.Busy)] = s.Busy,
                [nameof(EntryRow.Ai)] = s.Ai,
                [nameof(EntryRow.AnyAiBusy)] = s.AnyAiBusy,
                [nameof(EntryRow.DiscardToken)] = s.DiscardToken,
            };
            var root = await renderer.RenderComponentAsync<EntryRow>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }

    /// <summary>按钮标签 → 该按钮开始标签里是否带 disabled。</summary>
    private static bool IsDisabled(string html, string buttonText)
    {
        foreach (Match m in Regex.Matches(html, @"<button(?<attrs>[^>]*)>(?<body>.*?)</button>", RegexOptions.Singleline))
        {
            if (m.Groups["body"].Value.Contains(buttonText, StringComparison.Ordinal))
            {
                return Regex.IsMatch(m.Groups["attrs"].Value, @"\bdisabled\b");
            }
        }

        throw new Xunit.Sdk.XunitException($"button with text '{buttonText}' not found in: {html}");
    }

    /// <summary>按钮标签 → 该按钮开始标签里是否带指定的颜色修饰类（MButton Color="Error" 会加 Error 类）。</summary>
    private static bool ButtonHasColor(string html, string buttonText, string colorToken)
    {
        foreach (Match m in Regex.Matches(html, @"<button(?<attrs>[^>]*)>(?<body>.*?)</button>", RegexOptions.Singleline))
        {
            if (m.Groups["body"].Value.Contains(buttonText, StringComparison.Ordinal))
            {
                return Regex.IsMatch(m.Groups["attrs"].Value, $@"\b{colorToken}\b");
            }
        }

        throw new Xunit.Sdk.XunitException($"button with text '{buttonText}' not found in: {html}");
    }

    [Fact]
    public async Task Renders_A_Cell_Per_Culture_With_Status_And_Missing_Placeholder()
    {
        var entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);

        var html = await RenderHtmlAsync(RowState.For(entry));

        Assert.Equal(3, Regex.Matches(html, @"<td lang=""").Count);
        Assert.Contains(@"lang=""zh-CN""", html);
        Assert.Contains(@"lang=""en-US""", html);
        Assert.Contains(@"lang=""ja-JP""", html);
        Assert.Contains(@"data-status=""approved""", html);               // zh-CN / en-US
        Assert.Contains(@"data-status=""missing""", html);                // ja-JP 没有译文
        Assert.Contains(@"placeholder=""未翻译""", html);
        Assert.Contains(@"value=""Submit""", html);
        Assert.Contains(@"aria-label=""order.submit · en-US""", html);
    }

    [Fact]
    public async Task Entry_Text_Is_Html_Encoded()
    {
        var entry = NewEntry(key: "xss", source: "<script>alert(1)</script>",
            translations: [("en-US", "<img src=x onerror=alert(1)>", TranslationStatus.Approved)]);

        var html = await RenderRawAsync(RowState.For(entry));

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public async Task Namespace_Prefix_Shows_Only_For_Namespaced_Entries()
    {
        var namespaced = await RenderHtmlAsync(RowState.For(NewEntry(namespaceCode: "order")));
        Assert.Contains(@"<span class=""text--secondary"">order.</span>", namespaced);

        // 项目根（没有命名空间）：不显示前缀，也不拼出带前导点的 key
        var root = await RenderHtmlAsync(RowState.For(NewEntry(namespaceCode: null)));
        Assert.DoesNotContain(@"<span class=""text--secondary"">", root);
        Assert.Contains(@"aria-label=""submit · zh-CN""", root);
    }

    [Fact]
    public async Task Shows_Review_Stale_And_Validation_Notes()
    {
        var entry = NewEntry(source: "提交 {count}", translations:
        [
            ("en-US", "Submit {count}", TranslationStatus.NeedsReview),
            ("ja-JP", "送信 {count}", TranslationStatus.Approved)
        ]);
        entry.Translations.Single(t => t.Culture == "en-US").ReviewComment = "占位符需要复核";
        entry.Translations.Single(t => t.Culture == "ja-JP").SourceTextHash = "outdated-hash";

        var issue = new ValidationIssue("order.submit", "en-US", "PLACEHOLDER_MISSING", IssueSeverity.Error, "译文遗漏了占位符: {count}");
        var warning = new ValidationIssue("order.submit", "ja-JP", "EMPTY_TRANSLATION", IssueSeverity.Warning, "尚未提供译文");
        var state = RowState.For(entry) with { Notes = [NoNotes, [issue], [warning]] };

        var html = await RenderHtmlAsync(state);

        Assert.Contains("待复核", html);
        // tooltip = 降级原因 + 操作指引（确认 / 直接修改）
        Assert.Contains("占位符需要复核。确认无误点「确认」保留现译文并恢复为已审核", html);
        Assert.Contains("源文案已变更", html);
        // 待复核（en-US）与源文案已变更（ja-JP）的单元格都带「确认」按钮（MButton 的内容 span）
        Assert.Equal(2, Regex.Matches(html, @">\s*确认\s*</span>").Count);
        // 校验备注按级别着色：Masa 没有 error--text / warning--text 工具类，
        // 级别改由 MChip 的 Color（映射到 --m-theme-error / --m-theme-warning）表达。
        Assert.Contains(@"title=""PLACEHOLDER_MISSING""", html);
        Assert.Contains(@"title=""EMPTY_TRANSLATION""", html);
        Assert.Contains("译文遗漏了占位符: {count}", html);
        Assert.Contains("尚未提供译文", html);
        // 备注不再是被 !important 覆盖的裸 span，而是带 Color 的 chip（Masa 把 Color 名首字母大写放进类名）
        Assert.Matches(@"class=""[^""]*m-chip[^""]*\bError\b", html);
        Assert.Matches(@"class=""[^""]*m-chip[^""]*\bWarning\b", html);
    }

    [Fact]
    public async Task Confirm_Button_Shows_Only_For_NeedsReview_Or_Stale_Cells()
    {
        // 全部已审核且没有 stale 标记：任何单元格都不出现确认按钮
        var clean = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);
        var cleanHtml = await RenderHtmlAsync(RowState.For(clean));
        Assert.DoesNotMatch(@">\s*确认\s*</span>", cleanHtml);

        // 仅 en-US 待复核：确认按钮只出现在该语种的单元格
        var review = NewEntry(translations:
        [
            ("en-US", "Submit", TranslationStatus.NeedsReview),
            ("ja-JP", "送信", TranslationStatus.Approved)
        ]);
        var reviewHtml = await RenderHtmlAsync(RowState.For(review));
        Assert.Single(Regex.Matches(reviewHtml, @">\s*确认\s*</span>"));
    }

    [Fact]
    public async Task Ai_Column_Shows_One_Primary_Action_And_Hides_When_Nothing_To_Do()
    {
        // en-US 已审核、ja-JP 缺失：只有「补全 1 项」这一个可见动作；
        // 「重新翻译全部」与「删除词条」都是低频/破坏性动作，收进 kebab 菜单。
        var entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);
        var idle = await RenderHtmlAsync(RowState.For(entry));
        Assert.Contains("补全 1 项", idle);
        Assert.DoesNotContain("补全缺失", idle);
        // 菜单项文案在静态 HTML 里仍然是可断言的（MMenu 的内容不被 portal 掉到不可渲染）
        Assert.False(IsDisabled(idle, "补全 1 项"));

        // 页面忙碌：主操作禁用，kebab 也禁用（菜单里的动作此时都不能点）
        var busy = await RenderHtmlAsync(RowState.For(entry) with { Busy = true });
        Assert.True(IsDisabled(busy, "补全 1 项"));

        // 别的行正在 AI 翻译：本行主操作仍可点（只有删除要禁），kebab 因删除被禁而禁用
        var otherBusy = await RenderHtmlAsync(RowState.For(entry) with { AnyAiBusy = true });
        Assert.False(IsDisabled(otherBusy, "补全 1 项"));

        // 这一行正在 AI 翻译：动作整块换成进度提示
        var filling = await RenderHtmlAsync(RowState.For(entry) with { Ai = EntryRow.AiState.Filling, Busy = true, AnyAiBusy = true });
        Assert.Contains("AI 补全中…", filling);
        Assert.DoesNotContain("补全 1 项", filling);
        Assert.DoesNotContain("补全缺失", filling);
        var retranslating = await RenderHtmlAsync(RowState.For(entry) with { Ai = EntryRow.AiState.Retranslating, Busy = true, AnyAiBusy = true });
        Assert.Contains("AI 重新翻译中…", retranslating);

        // 各语种都已就绪：无事可做 → 「补全」按钮整个不渲染（不显示一个禁用的空动作）
        var ready = NewEntry(translations:
        [
            ("en-US", "Submit", TranslationStatus.Approved),
            ("ja-JP", "送信", TranslationStatus.AiTranslated)
        ]);
        var done = await RenderHtmlAsync(RowState.For(ready));
        Assert.DoesNotContain("补全", done);
        // 但 kebab 仍在：删除与重译始终可达
        Assert.Contains(@"aria-label=""更多操作""", done);
    }

    [Fact]
    public async Task Cell_Editing_Shows_Save_Actions_Only_After_Blur_And_Does_Not_Save_On_Typing()
    {
        var entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);
        using var harness = await RowHarness.StartAsync(RowState.For(entry));
        var row = harness.Row;
        var rendersAfterFirstPaint = harness.Renders;

        // 键击阶段：只记草稿，不重渲染、不回调页面、不出现「未保存」条
        await harness.InvokeAsync(() => row.SimulateInput("en-US", "Submit order"));

        Assert.Equal(rendersAfterFirstPaint, harness.Renders);
        Assert.False(row.PendingReported);
        Assert.Equal(0, harness.SavedEdits);

        // 失焦（onchange）：出现「保存 / 撤销」，依然没有写库
        await harness.InvokeAsync(() => row.SimulateBlur("en-US", "Submit order"));

        Assert.Equal(rendersAfterFirstPaint + 1, harness.Renders);
        Assert.True(row.PendingReported);
        Assert.Equal(0, harness.SavedEdits);

        // 点「保存」：以草稿值回调页面一次
        await harness.InvokeAsync(() => row.SimulateSave("en-US"));

        Assert.Equal(1, harness.SavedEdits);
        Assert.Equal("en-US", harness.LastEdit.Culture);
        Assert.Equal("Submit order", harness.LastEdit.Value);
        Assert.False(row.PendingReported);
    }

    [Fact]
    public async Task DiscardToken_Clears_Pending_Drafts()
    {
        var entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);
        using var harness = await RowHarness.StartAsync(RowState.For(entry));
        var row = harness.Row;

        await harness.InvokeAsync(() => row.SimulateBlur("en-US", "Submit order"));
        Assert.True(row.PendingReported);

        // 页面点「放弃全部」：标记递增，行内草稿清空
        await harness.RerenderParentAsync(RowState.For(entry) with { DiscardToken = 1 });

        Assert.False(row.PendingReported);
        await harness.InvokeAsync(() => row.SimulateSave("en-US"));
        Assert.Equal(0, harness.SavedEdits); // 放弃后再点保存不该写库
    }

    [Fact]
    public async Task Disposing_Row_With_Pending_Draft_Takes_Back_The_Pending_Report()
    {
        // 行带着未保存改动被销毁（翻页 / 换筛选时 @key 换批）：必须带着 false 回调一次，
        // 否则页面的「N 处修改未保存」会挂着一个已经不在屏幕上的行。
        // 这条测试同时钉住 EntryRow 的 IDisposable 声明——少了 @implements IDisposable 时
        // Dispose 不会被框架调用，销毁撤账无从发生（曾出现过这样的回归）。
        var entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]);
        var harness = await RowHarness.StartAsync(RowState.For(entry));

        await harness.InvokeAsync(() => harness.Row.SimulateBlur("en-US", "Submit order"));
        Assert.True(harness.Row.PendingReported);
        Assert.Equal([true], harness.PendingReports);

        harness.Dispose(); // = 渲染器销毁行组件

        Assert.Equal([true, false], harness.PendingReports);
    }

    [Fact]
    public async Task Shared_Impact_Pill_Differs_Between_Project_And_Shared_Scope()
    {
        var entry = NewEntry();
        var impact = new SharedEntryImpact(3, 1, ["auth-web"]);

        var inProject = await RenderHtmlAsync(RowState.For(entry) with { Impact = impact });
        Assert.Contains("共享 · 3 个项目继承、1 个已覆盖", inProject);
        Assert.Contains("已覆盖的项目：auth-web", inProject);

        var inShared = await RenderHtmlAsync(RowState.For(entry) with { Impact = impact, IsSharedScope = true });
        Assert.Contains("被 3 个项目继承、1 个项目已覆盖", inShared);
        Assert.DoesNotContain("共享 · 3 个项目继承", inShared);

        // 没有继承者：不显示
        var none = await RenderHtmlAsync(RowState.For(entry) with { Impact = new SharedEntryImpact(0, 0, []) });
        Assert.DoesNotContain("个项目继承", none);
    }

    // ---------------------------------------------------------------------
    // ShouldRender：只有这一行的输入变了才重渲染
    // ---------------------------------------------------------------------

    /// <summary>计数子类：OnAfterRender 只在真正渲染之后调用，次数 = 实际渲染次数。</summary>
    private sealed class ProbeRow : EntryRow
    {
        public int Renders { get; private set; }

        protected override void OnAfterRender(bool firstRender)
        {
            base.OnAfterRender(firstRender);
            Renders++;
        }
    }

    /// <summary>模拟页面：每次渲染按当前 State 给行传参（回调每次都是新实例，如同页面里的 lambda）。</summary>
    private sealed class Host : ComponentBase
    {
        public RowState State { get; set; } = null!;
        public ProbeRow? Row { get; private set; }

        /// <summary>行内「保存」回调次数与最后一次的编辑内容（用来断言「输入不写库、点保存才写」）。</summary>
        public int SavedEdits { get; private set; }
        public EntryRow.CellEdit LastEdit { get; private set; }

        /// <summary>行组件每次上报的「有没有未保存改动」，按时间序（用来断言翻转时才回调、销毁时撤账）。</summary>
        public List<bool> PendingReports { get; } = [];

        public void Refresh() => StateHasChanged();

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var s = State;
            builder.OpenComponent<ProbeRow>(0);
            builder.AddComponentParameter(1, nameof(EntryRow.Entry), s.Entry);
            builder.AddComponentParameter(2, nameof(EntryRow.Cultures), s.Cultures);
            builder.AddComponentParameter(3, nameof(EntryRow.TargetCultures), s.TargetCultures);
            builder.AddComponentParameter(4, nameof(EntryRow.DefaultCulture), s.DefaultCulture);
            builder.AddComponentParameter(5, nameof(EntryRow.Notes), s.Notes);
            builder.AddComponentParameter(6, nameof(EntryRow.IsSharedScope), s.IsSharedScope);
            builder.AddComponentParameter(7, nameof(EntryRow.Impact), s.Impact);
            builder.AddComponentParameter(8, nameof(EntryRow.Busy), s.Busy);
            builder.AddComponentParameter(9, nameof(EntryRow.Ai), s.Ai);
            builder.AddComponentParameter(10, nameof(EntryRow.AnyAiBusy), s.AnyAiBusy);
            builder.AddComponentParameter(11, nameof(EntryRow.DiscardToken), s.DiscardToken);
            builder.AddComponentParameter(12, nameof(EntryRow.OnContextEdited), EventCallback.Factory.Create<string?>(this, _ => { }));
            builder.AddComponentParameter(13, nameof(EntryRow.OnTranslationEdited), EventCallback.Factory.Create<EntryRow.CellEdit>(this, edit =>
            {
                SavedEdits++;
                LastEdit = edit;
            }));
            builder.AddComponentParameter(14, nameof(EntryRow.OnRequestAi), EventCallback.Factory.Create<bool>(this, _ => { }));
            builder.AddComponentParameter(15, nameof(EntryRow.OnRequestDelete), EventCallback.Factory.Create(this, () => { }));
            builder.AddComponentParameter(16, nameof(EntryRow.OnPendingChanged), EventCallback.Factory.Create<bool>(this, p => PendingReports.Add(p)));
            builder.AddComponentReferenceCapture(17, c => Row = (ProbeRow)c);
            builder.CloseComponent();
        }
    }

    /// <summary>最小 Renderer：不输出 DOM，只驱动组件生命周期；组件内抛出的异常原样抛给测试。</summary>
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public Task RenderRootAsync(IComponent component)
        {
            var id = AssignRootComponentId(component);
            return Dispatcher.InvokeAsync(() => RenderRootComponentAsync(id));
        }
    }

    private sealed class RowHarness : IDisposable
    {
        private readonly TestRenderer _renderer = new(Services);
        private readonly Host _host = new();

        private RowHarness(RowState initial) => _host.State = initial;

        public int Renders => _host.Row!.Renders;

        public ProbeRow Row => _host.Row!;

        public int SavedEdits => _host.SavedEdits;

        public EntryRow.CellEdit LastEdit => _host.LastEdit;

        /// <summary>行组件按时间序上报的「有没有未保存改动」。</summary>
        public IReadOnlyList<bool> PendingReports => _host.PendingReports;

        /// <summary>在渲染器上下文里跑一段触发行内逻辑的代码（组件方法里有 StateHasChanged，必须在所属 Dispatcher 上执行）。</summary>
        public Task InvokeAsync(Action action) => _renderer.Dispatcher.InvokeAsync(action);

        public Task InvokeAsync(Func<Task> action) => _renderer.Dispatcher.InvokeAsync(action);

        public static async Task<RowHarness> StartAsync(RowState initial)
        {
            var harness = new RowHarness(initial);
            await harness._renderer.RenderRootAsync(harness._host);
            return harness;
        }

        /// <summary>页面重渲染一次（可带新状态）。</summary>
        public Task RerenderParentAsync(RowState? next = null)
        {
            if (next is not null)
            {
                _host.State = next;
            }

            return _renderer.Dispatcher.InvokeAsync(_host.Refresh);
        }

        public void Dispose() => _renderer.Dispose();
    }

    private static RowState Baseline() =>
        RowState.For(NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]));

    [Fact]
    public async Task Parent_Rerenders_With_Unchanged_Inputs_Do_Not_Rerender_The_Row()
    {
        var state = Baseline();
        using var harness = await RowHarness.StartAsync(state);
        Assert.Equal(1, harness.Renders);

        for (var i = 0; i < 5; i++)
        {
            await harness.RerenderParentAsync();
        }

        Assert.Equal(1, harness.Renders);
    }

    [Fact]
    public async Task Row_Rerenders_When_Any_Rendered_Input_Changes()
    {
        var issue = new ValidationIssue("order.submit", "en-US", "PLACEHOLDER_MISSING", IssueSeverity.Error, "msg");
        var mutations = new (string Name, Func<RowState, RowState> Apply)[]
        {
            ("entry replaced", s => s with { Entry = NewEntry(translations: [("en-US", "Submit", TranslationStatus.Approved)]) }),
            ("cultures replaced", s => s with { Cultures = [.. s.Cultures] }),
            ("target cultures replaced", s => s with { TargetCultures = [.. s.TargetCultures] }),
            ("default culture changed", s => s with { DefaultCulture = "en-US" }),
            ("note appears", s => s with { Notes = [NoNotes, [issue], NoNotes] }),
            ("shared scope toggled", s => s with { IsSharedScope = true }),
            ("impact set", s => s with { Impact = new SharedEntryImpact(2, 0, []) }),
            ("busy", s => s with { Busy = true }),
            ("ai state", s => s with { Ai = EntryRow.AiState.Filling }),
            ("any ai busy", s => s with { AnyAiBusy = true }),
        };

        foreach (var (name, apply) in mutations)
        {
            var before = Baseline();
            using var harness = await RowHarness.StartAsync(before);

            var after = apply(before);
            await harness.RerenderParentAsync(after);
            Assert.True(harness.Renders == 2, $"'{name}' should re-render the row (renders={harness.Renders})");

            // 渲染过后输入没再变：继续刷新页面不再重渲染（脏标记已复位）
            await harness.RerenderParentAsync();
            await harness.RerenderParentAsync();
            Assert.True(harness.Renders == 2, $"'{name}': row kept re-rendering with unchanged inputs (renders={harness.Renders})");
        }
    }

    [Fact]
    public async Task Equivalent_Empty_Notes_Do_Not_Rerender_But_Changed_Notes_Do()
    {
        var before = Baseline();
        using var harness = await RowHarness.StartAsync(before);

        // 校验重建后「无备注」换成了新的空列表实例：内容相同，不重渲染
        await harness.RerenderParentAsync(before with { Notes = [[], [], []] });
        Assert.Equal(1, harness.Renders);

        // 有内容的备注换新实例（重新校验的结果）：重渲染
        var issue = new ValidationIssue("order.submit", "en-US", "MAX_LENGTH_EXCEEDED", IssueSeverity.Error, "too long");
        IReadOnlyList<ValidationIssue> withIssue = [issue];
        await harness.RerenderParentAsync(before with { Notes = [NoNotes, withIssue, NoNotes] });
        Assert.Equal(2, harness.Renders);

        // 同一份备注实例再次传入：不重渲染
        await harness.RerenderParentAsync(before with { Notes = [NoNotes, withIssue, NoNotes] });
        Assert.Equal(2, harness.Renders);

        // 备注数量与语种数不一致（语种配置变化中间态）不会越界：按空备注渲染
        await harness.RerenderParentAsync(before with { Notes = [] });
        Assert.Equal(3, harness.Renders);
    }
}
