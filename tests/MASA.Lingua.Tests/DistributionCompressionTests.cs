using System.IO.Compression;
using System.Net;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 分发热路径的响应压缩：语言包（application/json）按协商编码压缩，
/// SSE（text/event-stream）必须保持不压缩、不缓冲。
/// </summary>
public sealed class DistributionCompressionTests
{
    private const string BundlePath = "/i18n/shop-vue/en-US.json";

    [Fact]
    public async Task Bundle_Should_Be_Compressed_When_Client_Accepts_Gzip()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        // 不带 Accept-Encoding：得到未压缩的原始负载作为比对基准
        using var plainResponse = await client.GetAsync(BundlePath);
        Assert.Equal(HttpStatusCode.OK, plainResponse.StatusCode);
        Assert.Empty(plainResponse.Content.Headers.ContentEncoding);
        var expected = await plainResponse.Content.ReadAsByteArrayAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, BundlePath);
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));

        // 压缩不改变内容：解压后必须与未压缩响应逐字节一致
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        await gzip.CopyToAsync(decompressed);
        Assert.Equal(expected, decompressed.ToArray());

        // 同一 URL 有两种表示，缓存必须按编码区分
        Assert.Contains("Accept-Encoding", string.Join(",", response.Headers.Vary), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sse_Should_Not_Be_Compressed()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream?project=shop-vue");
        request.Headers.AcceptEncoding.ParseAdd("gzip, br");

        // 只读响应头：SSE 是长连接，拿到头就撤，不等事件
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}
