using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 分发端点的缓存契约：强 ETag + no-cache。
/// 不允许再出现 max-age / stale-while-revalidate —— 那会让「已发布」与「客户端能看到」之间
/// 出现最长接近一天的窗口（审查里被列为中危问题）。
/// </summary>
public sealed class CachePolicyTests
{
    [Fact]
    public async Task Bundle_Should_Be_NoCache_And_Revalidate_With_ETag()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var first = await client.GetAsync("/i18n/shop-vue/zh-CN.json");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var cacheControl = first.Headers.CacheControl!.ToString();
        Assert.Contains("no-cache", cacheControl);
        Assert.DoesNotContain("max-age", cacheControl);
        Assert.DoesNotContain("stale-while-revalidate", cacheControl);

        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        Assert.True(etag!.IsWeak is false);

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/i18n/shop-vue/zh-CN.json");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", etag.ToString());
        using var second = await client.SendAsync(revalidate);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }
}
