using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace MASA.Lingua.Tests;

/// <summary>
/// 缺词上报端点（POST /api/v1/telemetry/missing-keys）的项目写密钥鉴权：
/// 唯一带鉴权的写端点——无头 401、错钥 403、正确 200 并入库、项目不存在仍 404。
/// 客户端 SDK（MAUI / Vue）已移除上报能力，该端点只服务携带密钥的服务端 SDK。
/// </summary>
public sealed class MissingKeyEndpointTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Without_Key_Should_Return_401()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/v1/telemetry/missing-keys",
            new StringContent(Body("shop-vue"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task With_Wrong_Key_Should_Return_403()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/missing-keys")
        {
            Content = new StringContent(Body("shop-vue"), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WriteKeys.HeaderName, "lingua_sk_0000000000000000000000000000000f");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task With_Correct_Key_Should_Record_Missing_Key()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();
        var repo = factory.Services.GetRequiredService<ILinguaRepository>();
        var project = await repo.GetProjectByCodeAsync("shop-vue");
        Assert.False(string.IsNullOrWhiteSpace(project?.WriteKey), "种子项目应生成写密钥");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/missing-keys")
        {
            Content = new StringContent(Body("shop-vue"), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WriteKeys.HeaderName, project!.WriteKey);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>(WebJson);
        Assert.NotNull(payload);
        Assert.Equal(1, int.Parse(payload!["addedCount"]?.ToString() ?? "-1"));

        // 词条确实入库（IsAutoReported）
        var stored = await repo.GetProjectByCodeAsync("shop-vue");
        var entry = stored!.Namespaces
            .First(n => n.Code == "order")
            .Entries.Single(e => e.Key == "checkoutNow");
        Assert.True(entry.IsAutoReported);
    }

    [Fact]
    public async Task Unknown_Project_Should_Return_404_Even_With_A_Key()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/telemetry/missing-keys")
        {
            Content = new StringContent(Body("no-such-project"), Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WriteKeys.HeaderName, "lingua_sk_0000000000000000000000000000000f");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>项目骨架创建端点在新建时返回生成的写密钥（脚本化接入不需要再进管理后台查一次）。</summary>
    [Fact]
    public async Task Skeleton_Create_Should_Return_WriteKey()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/v1/projects/brand-new-proj", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, object?>>(WebJson);
        Assert.Equal("True", payload?["created"]?.ToString());
        var writeKey = payload?["writeKey"]?.ToString();
        Assert.NotNull(writeKey);
        Assert.StartsWith("lingua_sk_", writeKey!);

        // 再创建一次是幂等回读：不再带 writeKey，避免密钥在日志 / 响应里反复出现
        using var second = await client.PostAsync("/api/v1/projects/brand-new-proj", content: null);
        var secondPayload = await second.Content.ReadFromJsonAsync<Dictionary<string, object?>>(WebJson);
        Assert.Equal("False", secondPayload?["created"]?.ToString());
        Assert.False(secondPayload!.ContainsKey("writeKey"));
    }

    private static string Body(string projectCode) => JsonSerializer.Serialize(new
    {
        projectCode,
        culture = "zh-CN",
        items = new[]
        {
            new { @namespace = "order", key = "checkoutNow", defaultText = "立即去结算" }
        }
    }, WebJson);
}
