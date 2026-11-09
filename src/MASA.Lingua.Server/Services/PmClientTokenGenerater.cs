using IdentityModel.Client;
using Masa.Contrib.StackSdks.Caller;
using Microsoft.Extensions.Caching.Memory;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// PM 外呼的「应用身份」token 生成器：向 SSO 走 <c>client_credentials</c>（无需 secret，scope=MasaStack），
/// 按 <c>ExpiresIn</c> 扣安全余量缓存，进程内约每小时换取一次。
/// <para>
/// 为什么不用当前登录用户的 token：PM 项目/应用清单是平台级数据，用户身份带不来权限差异；
/// 用户 token 在 Blazor Server 交互电路里又不可靠（交互渲染没有可用的 HTTP 上下文），
/// 且登录票据请求的 scope（openid profile）不含 PM 网关实测认可的 MasaStack。
/// 这也是 MASA Stack 后台服务调 PM 的既定形态。
/// </para>
/// </summary>
public sealed class PmClientTokenGenerater(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<PmClientTokenGenerater> logger,
    string clientId,
    string ssoDomain,
    string[] scopes) : ITokenGenerater
{
    private const string ClientTokenCacheKey = "lingua.pm.client-token";

    /// <summary>token 的提前作废余量：避免「刚好卡在过期线上」的请求被网关拒绝。</summary>
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <see cref="ITokenGenerater"/> 是同步签名，内部必须阻塞等待异步网络调用（首次换取；之后命中缓存）。
    /// <para>
    /// Blazor Server 的组件生命周期跑在渲染器的串行同步上下文里：在该上下文上直接
    /// <c>.GetAwaiter().GetResult()</c>，被等待任务的续体要回到同一个（已被阻塞的）上下文 —— 死锁
    ///（已用 Blazor Dispatcher 实测复现）。因此整体卸到线程池线程执行。
    /// </para>
    /// </summary>
    public TokenProvider Generater() => Task.Run(GetOrRequestToken).GetAwaiter().GetResult();

    private TokenProvider GetOrRequestToken() =>
        cache.GetOrCreate(ClientTokenCacheKey, entry =>
        {
            var token = RequestClientTokenAsync().GetAwaiter().GetResult();
            entry.AbsoluteExpiration = DateTimeOffset.UtcNow
                + TimeSpan.FromSeconds(token.ExpiresIn)
                - ExpirySafetyMargin;
            return token;
        }) ?? throw new InvalidOperationException("client_credentials token 缓存获取失败。");

    private async Task<TokenProvider> RequestClientTokenAsync()
    {
        var address = ssoDomain.TrimEnd('/') + "/connect/token";
        // token 步骤的起始标记：成功结果有约 1h 缓存（每小时一条，低噪）。
        // 若日志停在「获取 token」后无下文直至 PM 整链超时，即可断定 SSO 地址是黑洞（挂起无异常可抛）。
        logger.LogInformation("向 SSO 请求 client_credentials token：客户端 {ClientId}，地址 {Address}", clientId, address);

        using var client = httpClientFactory.CreateClient("lingua-pm-client-token");

        var tokenTask = client.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
        {
            Address = address,
            GrantType = "client_credentials",
            ClientId = clientId,
            Scope = string.Join(' ', scopes)
        });

        TokenResponse response;
        try
        {
            response = await tokenTask;
        }
        catch (OperationCanceledException ex)
        {
            // Generater() 是同步签名、没有 CancellationToken：走到这里的 OCE 只可能是 HttpClient 超时
            //（未注册命名客户端时默认 100s）。裸 OCE 向上会被「调用方取消」的过滤器放行——既不记日志也不进
            // 失败缓存，SSO 黑洞就这样无痕。转成带明确语义的异常再抛（非 OCE，会正常进失败日志与缓存）。
            throw new InvalidOperationException(
                $"向 SSO 请求 client_credentials token 超时（{clientId} @ {address}）—— 部署网络到 SSO 不通或对端无响应。", ex);
        }

        if (response.IsError)
        {
            // IdentityModel 的协议错误（SSO 返回 4xx）不是传输层异常，HttpClient 的 Warning 日志抓不到，必须在这里记。
            logger.LogWarning(
                "向 SSO 请求 client_credentials token 失败：客户端 {ClientId}，地址 {Address}，错误 {Error}",
                clientId, address, response.Error);
            throw new InvalidOperationException(
                $"向 SSO 请求 client_credentials token 失败（{clientId} @ {ssoDomain}）：{response.Error}");
        }

        return new TokenProvider
        {
            AccessToken = response.AccessToken ?? string.Empty,
            RefreshToken = response.RefreshToken ?? string.Empty,
            IdToken = response.IdentityToken ?? string.Empty,
            ExpiresIn = response.ExpiresIn
        };
    }
}
