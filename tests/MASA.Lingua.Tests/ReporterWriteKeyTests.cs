using System.Net;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 缺词上报器的项目写密钥鉴权：配置了 <c>LinguaClientOptions.WriteKey</c> 的请求必须携带
/// <c>X-Lingua-Key</c> 头（服务端只接受带密钥的上报）；未配置时静默跳过并只警告一次。
/// </summary>
public sealed class ReporterWriteKeyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(TimeSpan timeout) => _received.Task.WaitAsync(timeout);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            _received.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpMissingKeyReporter Reporter, CapturingHandler Handler) CreateReporter(LinguaClientOptions options)
    {
        var handler = new CapturingHandler();
        var services = new ServiceCollection();
        services.AddHttpClient("LinguaClient")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();

        var reporter = new HttpMissingKeyReporter(
            Options.Create(options),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<HttpMissingKeyReporter>.Instance);
        return (reporter, handler);
    }

    [Fact]
    public async Task Configured_WriteKey_Should_Be_Sent_As_Header()
    {
        var (reporter, handler) = CreateReporter(new LinguaClientOptions
        {
            ProjectCode = "auth-web-staging",
            ServerEndpoint = "http://stub",
            EnableMissingKeyTelemetry = true,
            WriteKey = "lingua_sk_abcdef0123456789abcdef0123456789"
        });

        reporter.ReportMissingKey("zh-CN", "errors.UserNotExist", "用户不存在");
        await handler.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(WriteKeys.HeaderName, handler.LastRequest!.Headers.First(h => h.Key == "X-Lingua-Key").Key);
        Assert.Equal("lingua_sk_abcdef0123456789abcdef0123456789", handler.LastRequest.Headers.GetValues("X-Lingua-Key").Single());
    }

    [Fact]
    public async Task Missing_WriteKey_Should_Not_Send_Request()
    {
        // 开关开着但没配密钥：请求直接不发（会被服务端 401），只记一次警告
        var (reporter, handler) = CreateReporter(new LinguaClientOptions
        {
            ProjectCode = "auth-web-staging",
            ServerEndpoint = "http://stub",
            EnableMissingKeyTelemetry = true,
            WriteKey = null
        });

        reporter.ReportMissingKey("zh-CN", "errors.UserNotExist");
        await Task.Delay(100);

        Assert.Null(handler.LastRequest);
    }
}
