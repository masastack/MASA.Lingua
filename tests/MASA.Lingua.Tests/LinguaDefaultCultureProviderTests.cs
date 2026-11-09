using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.MasaI18n;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 默认语种来源：必须跟随平台项目的 <c>DefaultCulture</c>（随 manifest 同步进内存字典），
/// 而不是本地 <c>{culture}.json</c> 的文件枚举顺序。
/// </summary>
public sealed class LinguaDefaultCultureProviderTests
{
    private static LinguaFrozenMemoryStore CreateStore(string? defaultCulture = null)
    {
        var store = new LinguaFrozenMemoryStore();
        if (defaultCulture is not null)
        {
            store.SetDefaultCulture(defaultCulture);
        }

        return store;
    }

    [Fact]
    public async Task Provider_Should_Return_Platform_Default_Culture()
    {
        var provider = new LinguaDefaultCultureProvider(CreateStore("ja-JP"));

        var result = await provider.DetermineProviderCultureResult(new DefaultHttpContext());

        Assert.NotNull(result);
        Assert.Equal("ja-JP", Assert.Single(result!.Cultures).Value);
    }

    [Fact]
    public async Task Provider_Should_Return_Null_When_DefaultCulture_Unknown()
    {
        // 非 Lingua 的 store 实现可能没有默认语种（接口默认返回 null）：
        // 这时不能猜一个值，要返回 null 交给 ASP.NET 自己的 DefaultRequestCulture 兜底。
        var provider = new LinguaDefaultCultureProvider(new NullDefaultCultureStore());

        Assert.Null(await provider.DetermineProviderCultureResult(new DefaultHttpContext()));
    }

    /// <summary>
    /// provider 顺序是正确性的一部分：中间件命中第一个返回非空的 provider 就 break，
    /// 所以「平台默认语种」必须挂在末尾 —— 显式 ?culture= / Accept-Language 要先赢。
    /// 这里直接用 <see cref="RequestLocalizationMiddleware"/> 验证真实协商结果，
    /// 不通过 MASA 的 <c>UseI18n()</c>（它内部有静态初始化守卫，会污染其它测试）。
    /// </summary>
    [Theory]
    [InlineData("/?culture=ja-JP", null, "ja-JP")]                 // 查询串显式指定优先
    [InlineData("/", "ru-RU", "ru-RU")]                            // Accept-Language 次之
    [InlineData("/", null, "zh-CN")]                               // 都没给 → 平台默认语种
    [InlineData("/", "fr-FR", "zh-CN")]                            // 不支持的语种 → 平台默认语种
    public async Task Middleware_Should_Prefer_Explicit_Culture_Then_Platform_Default(
        string path,
        string? acceptLanguage,
        string expectedCulture)
    {
        var store = CreateStore("zh-CN");
        var options = new RequestLocalizationOptions
        {
            SupportedCultures = [new("zh-CN"), new("en-US"), new("ja-JP"), new("ru-RU")],
            SupportedUICultures = [new("zh-CN"), new("en-US"), new("ja-JP"), new("ru-RU")],
        };
        options.SetDefaultCulture("en-US"); // 故意设成 en-US：若 provider 没生效就能看出来
        options.RequestCultureProviders.Add(new LinguaDefaultCultureProvider(store));

        string? observed = null;
        var middleware = new RequestLocalizationMiddleware(
            context =>
            {
                // 从 feature 读取中间件本请求的判定结果（不用 CurrentUICulture：
                // async 流里会随 ExecutionContext 传递，断言不稳）
                observed = context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.Name;
                return Task.CompletedTask;
            },
            Options.Create(options),
            NullLoggerFactory.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = new PathString(path.Split('?')[0]);
        if (path.Contains("?culture=", StringComparison.Ordinal))
        {
            httpContext.Request.QueryString = new QueryString("?" + path.Split('?')[1]);
        }
        if (acceptLanguage is not null)
        {
            httpContext.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        await middleware.Invoke(httpContext);

        Assert.Equal(expectedCulture, observed);
    }

    /// <summary>没有默认语种的 store 实现（模拟非 Lingua 宿主）。</summary>
    private sealed class NullDefaultCultureStore : ILinguaMemoryStore
    {
        public bool TryGetValue(string culture, string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerable<KeyValuePair<string, string>> GetAll(string culture, string? namespacePrefix = null) => [];

        public void UpdateCultureFromJsonBytes(string culture, ReadOnlySpan<byte> utf8Json) { }

        public void LoadFromLocalDirectory(string directoryPath) { }
    }
}
