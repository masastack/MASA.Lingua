using System.Net;
using System.Text;
using MASA.Lingua.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 分发链路的访问策略：内网部署不设密钥，读发布产物、上报缺失词条、导入全部匿名可用；
/// 跨域全放开（AllowAnyOrigin）。唯一保留的门禁是「项目必须存在」。
/// </summary>
public sealed class DistributionAccessTests
{
    [Fact]
    public async Task Bundle_Should_Be_Publicly_Readable_And_Cacheable_By_Shared_Caches()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var res = await client.GetAsync("/i18n/shop-vue/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("public, no-cache", res.Headers.CacheControl!.ToString());

        using var missing = await client.GetAsync("/i18n/no-such-project/zh-CN.json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ImportJson_Should_Work_Anonymously_And_Never_Create_Projects()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var ok = await client.SendAsync(Post(
            "/api/v1/projects/shop-vue/import-json?culture=en-US",
            """{ "order": { "submitBtn": "Checkout" } }"""));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // 导入不会顺带创建项目：编码写错时必须是 404，而不是凭空多一个空项目
        using var missing = await client.SendAsync(Post(
            "/api/v1/projects/no-such-project/import-json?culture=zh-CN",
            """{ "a": "b" }"""));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var projects = factory.Services.GetRequiredService<IProjectCatalog>();
        Assert.Null(await projects.GetProjectSummaryAsync("no-such-project"));
    }

    [Fact]
    public async Task Cors_Should_Allow_Any_Origin_For_Browser_Clients()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var get = new HttpRequestMessage(HttpMethod.Get, "/i18n/shop-vue/zh-CN.json");
        get.Headers.TryAddWithoutValidation("Origin", "https://any.example.com");
        using var res = await client.SendAsync(get);
        Assert.Equal("*", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        // 客户端要能读到 ETag 才能做条件请求
        Assert.Contains("ETag", res.Headers.GetValues("Access-Control-Expose-Headers").Single());

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects/shop-vue/import-json");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://any.example.com");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");
        using var pre = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, pre.StatusCode);
        Assert.Equal("*", pre.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    private static HttpRequestMessage Post(string url, string json) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
