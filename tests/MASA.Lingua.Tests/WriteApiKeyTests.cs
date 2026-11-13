using System.Net;
using System.Text;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 分发写端点的可选共享密钥门禁（<c>Lingua:Distribution:WriteApiKey</c>）：
/// 未配置 = 匿名（内网默认）；配置后创建 / 删除 / 导入 / 导出必须带匹配的 X-Lingua-Key（缺失 401、不匹配 403），
/// 读端点与管理后台的 SSO 入口不受影响。
/// </summary>
public sealed class WriteApiKeyTests
{
    private const string Key = "gate-secret-0123456789abcdef";

    private static HttpRequestMessage Request(HttpMethod method, string url, string? key, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation(WriteKeys.HeaderName, key);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, HttpMethod method, string url, string? key, string? body = null)
    {
        using var request = Request(method, url, key, body);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>四个受门禁保护的端点；每项自带「放行时」期望的状态码。</summary>
    private static (string Name, HttpMethod Method, string Url, string? Body)[] GatedEndpoints(string code) =>
    [
        ("create", HttpMethod.Post, $"/api/v1/projects/{code}", null),
        ("import-json", HttpMethod.Post, $"/api/v1/projects/{code}/import-json?culture=zh-CN", """{ "hello": "你好" }"""),
        ("export-zip", HttpMethod.Get, $"/api/v1/projects/{code}/export-zip", null),
        ("delete", HttpMethod.Delete, $"/api/v1/projects/{code}", null)
    ];

    [Fact]
    public async Task Without_A_Configured_Key_All_Write_Endpoints_Stay_Anonymous()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        const string code = "gate-open";

        foreach (var (name, method, url, body) in GatedEndpoints(code))
        {
            var status = await SendAsync(client, method, url, key: null, body);
            Assert.True(status == HttpStatusCode.OK, $"{name}：未配置密钥时应匿名放行，实际 {(int)status}");
        }
    }

    [Fact]
    public async Task A_Whitespace_Key_Is_Treated_As_Not_Configured()
    {
        await using var factory = new IsolatedLinguaFactory(writeApiKey: "   ");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(client, HttpMethod.Post, "/api/v1/projects/gate-blank", key: null));
    }

    [Fact]
    public async Task Configured_Key_Should_Reject_Missing_And_Wrong_Keys_On_Every_Write_Endpoint_Without_Side_Effects()
    {
        await using var factory = new IsolatedLinguaFactory(writeApiKey: Key);
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();

        // 一个已存在的项目：删除 / 导入被拒时它必须原封不动
        const string existing = "gate-existing";
        await repo.CreateProjectAsync(existing);

        foreach (var (name, method, url, body) in GatedEndpoints(existing))
        {
            Assert.True(
                HttpStatusCode.Unauthorized == await SendAsync(client, method, url, key: null, body),
                $"{name}：缺少 X-Lingua-Key 应为 401");
            Assert.True(
                HttpStatusCode.Forbidden == await SendAsync(client, method, url, key: "wrong-key", body),
                $"{name}：密钥不匹配应为 403");
            // 长度相同但内容不同、以及只差一个字符：都不能放行
            Assert.True(
                HttpStatusCode.Forbidden == await SendAsync(client, method, url, key: Key[..^1] + "X", body),
                $"{name}：差一个字符的密钥应为 403");
        }

        Assert.NotNull(await repo.GetProjectByCodeAsync(existing));

        // 被拒的创建没有留下项目
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(client, HttpMethod.Post, "/api/v1/projects/gate-ghost", key: null));
        Assert.Null(await repo.GetProjectByCodeAsync("gate-ghost"));
    }

    [Fact]
    public async Task Configured_Key_Should_Let_A_Matching_Key_Through_On_Every_Write_Endpoint()
    {
        await using var factory = new IsolatedLinguaFactory(writeApiKey: Key);
        using var client = factory.CreateClient();
        const string code = "gate-allowed";

        foreach (var (name, method, url, body) in GatedEndpoints(code))
        {
            var status = await SendAsync(client, method, url, Key, body);
            Assert.True(status == HttpStatusCode.OK, $"{name}：带正确密钥应放行，实际 {(int)status}");
        }
    }

    [Fact]
    public async Task Read_Endpoints_Stay_Anonymous_When_A_Key_Is_Configured()
    {
        await using var factory = new IsolatedLinguaFactory(writeApiKey: Key);
        using var client = factory.CreateClient();

        foreach (var url in new[]
                 {
                     "/api/v1/projects/shop-vue/manifest",
                     "/api/v1/projects/shop-vue/glossary",
                     "/i18n/shop-vue/zh-CN.json",
                     "/api/v1/bundles/shop-vue/zh-CN"
                 })
        {
            var status = await SendAsync(client, HttpMethod.Get, url, key: null);
            Assert.True(status == HttpStatusCode.OK, $"{url}：读端点必须保持匿名，实际 {(int)status}");
        }
    }

    [Fact]
    public async Task Admin_Export_Route_Requires_Sso_And_Ignores_The_Shared_Key()
    {
        await using var factory = new IsolatedLinguaFactory(writeApiKey: Key);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 管理后台的下载按钮走这条路由：只认 SSO 登录，未登录 302 到 /login——
        // 共享密钥是给脚本 / CLI 用的另一套凭据，不能当作管理员登录
        using var anonymous = await client.GetAsync("/admin/projects/shop-vue/export-zip");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/login", anonymous.Headers.Location!.ToString());

        using var withKey = Request(HttpMethod.Get, "/admin/projects/shop-vue/export-zip", Key);
        using var withKeyResponse = await client.SendAsync(withKey);
        Assert.Equal(HttpStatusCode.Redirect, withKeyResponse.StatusCode);
        Assert.Contains("/login", withKeyResponse.Headers.Location!.ToString());
    }
}
