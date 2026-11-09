using Masa.Contrib.Configuration.ConfigurationApi.Dcc.Options;
using Masa.Contrib.StackSdks.Caller;
using Masa.Contrib.StackSdks.Config;
using MASA.Lingua.Server.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MASA.Lingua.Server.Startup;

/// <summary>
/// 启动期解析管理后台 OIDC Authority（SSO 域名）。
/// <para>
/// 取值优先级：<c>Auth:Authority</c> 显式覆盖 → DCC 公共配置（MasaStackConfig.GetSsoDomain）→ 启动失败。
/// 覆盖分支只服务两个真实场景：自动化测试宿主与 DCC 不可达时的本地调试；生产不配置该键即自动走 DCC。
/// </para>
/// </summary>
public static class MasaStackStartup
{
    /// <summary>显式覆盖键（测试宿主 / 离线调试）。生产留空即走 DCC。</summary>
    public const string OverrideKey = "Auth:Authority";

    /// <summary>DCC 连接段（与 masa.specdoc 的 appsettings 结构一致）。</summary>
    public const string DccSectionName = "DCC";

    /// <summary>
    /// 解析并返回非空的 SSO 站点根地址（已去掉末尾 <c>/</c>）。缺失即抛出可执行的启动失败异常。
    /// </summary>
    /// <remarks>
    /// 副作用：走 DCC 分支时会把宿主本地配置源整体迁移走（MasaStackConfig 的已知行为），
    /// 因此调用前必须快照、调用后原样加回内存源，否则 <c>Auth:ClientId</c> 等本地段读空。
    /// 本方法内部完成「快照 → 初始化 → 回填」三步，调用方不得在此之前读取 <c>Auth:*</c> 配置段。
    /// </remarks>
    public static async Task<string> InitializeAsync(WebApplicationBuilder builder)
    {
        var overridden = builder.Configuration[OverrideKey];
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Normalize(overridden);
        }

        var dccOptions = builder.Configuration.GetSection(DccSectionName).Get<DccOptions>();
        if (dccOptions is null || string.IsNullOrWhiteSpace(dccOptions.ManageServiceAddress))
        {
            throw new InvalidOperationException(
                $"SSO Authority 未解析：{OverrideKey} 未配置，且 {DccSectionName}:ManageServiceAddress 缺失。"
                + $"管理后台强制 SSO 登录（无匿名模式）。请配置 {DccSectionName} 段"
                + "（ManageServiceAddress / RedisOptions / Secret / AppId / Environment / Cluster），"
                + $"或仅限本地调试时用 {OverrideKey}（环境变量 Auth__Authority）显式覆盖。");
        }

        if (dccOptions.RedisOptions is null || dccOptions.RedisOptions.Servers.Count == 0)
        {
            throw new InvalidOperationException(
                $"{DccSectionName}:RedisOptions 未配置 —— MasaStackConfig 需要 Redis 连接"
                + "（Servers / DefaultDatabase / Password）才能拉取 DCC 公共配置。");
        }

        var localConfigSnapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Flatten(builder.Configuration, localConfigSnapshot, prefix: "");

        await builder.Services.AddMasaStackConfigAsync(
            MasaStackProject.TSC, MasaStackApp.WEB, dccOptions: dccOptions);

        // 回填宿主本地配置：AddMasaStackConfigAsync 会把本地数据源迁进 Masa 配置根，
        // 不回填则 Auth:ClientId / Lingua:* 等本地段在 Options 绑定时读空。
        builder.Configuration.AddInMemoryCollection(localConfigSnapshot);

        var masaStackConfig = builder.Services.GetMasaStackConfig();

        var pmClientId = builder.Configuration["Auth:ClientId"] ?? "MASA_STACK";
        var ssoDomain = masaStackConfig.GetSsoDomain();

        // 显式注册 ITokenGenerater（必须先于 AddPmClient，后者的 TryAddScoped 见已存在即跳过）。
        // PM 清单是平台级数据，统一用应用身份（client_credentials，按 ExpiresIn 缓存），不依赖当前请求/用户：
        // SDK 默认的「透传入站 Authorization」在 Blazor 里没有可透传的头，而 SDK 自带的 ClientTokenGenerater
        // 在有 HttpContext 时会走透传分支，同样不可靠。
        builder.Services.TryAddScoped<ITokenGenerater>(sp => new PmClientTokenGenerater(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<ILogger<PmClientTokenGenerater>>(),
            pmClientId,
            ssoDomain,
            ["MasaStack"]));

        // token 请求用的命名客户端：不注册则落 HttpClient 默认 100s 超时，SSO 黑洞时整条 PM 链路被它拖死。
        // 内网正常亚秒级，3s 已极宽松；取小于 PM 整链超时（默认 5s，见 PmProjectDirectory）的值，
        // 让「SSO 慢/挂」先以明确的 token 失败日志呈现，而不是笼统的整体超时。
        builder.Services.AddHttpClient("lingua-pm-client-token")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(3));

        // PM 客户端（顶栏项目切换器的数据源）：域名与 SSO 同源解析（DCC 公共配置），
        // 必须排在 AddMasaStackConfigAsync 之后 —— 该步骤会迁移配置数据源，此前注册会在错误的配置根上解析域名。
        // callerAction 里的 UseClientAuthentication 负责把 AuthenticationService 中间件挂到 PM caller 上
        // （token 从上面的 ITokenGenerater 解析），并设置 SSO 地址等静态配置。
        builder.Services.AddPmClient(
            () => masaStackConfig.GetPmServiceDomain(),
            callerAction: callerBuilder => callerBuilder.UseClientAuthentication(
                pmClientId, ssoDomain));

        if (string.IsNullOrWhiteSpace(ssoDomain))
        {
            throw new InvalidOperationException(
                "MasaStackConfig 未提供 SSO 域名（GetSsoDomain 为空）—— 检查 DCC 公共配置"
                + "（$public.DefaultConfig）的 MASA_STACK 清单中 auth 项目的 sso.domain，"
                + $"以及 {DccSectionName}:Environment / Cluster / Secret 是否指向正确环境。");
        }

        return Normalize(ssoDomain);
    }

    private static string Normalize(string authority) => authority.Trim().TrimEnd('/');

    /// <summary>把配置递归摊平成 <c>a:b:c = value</c>，用于事后回填内存配置源。</summary>
    private static void Flatten(IConfiguration configuration, Dictionary<string, string?> target, string prefix)
    {
        foreach (var child in configuration.GetChildren())
        {
            var key = prefix.Length == 0 ? child.Key : $"{prefix}:{child.Key}";
            if (child.Value is not null)
            {
                target[key] = child.Value;
            }

            Flatten(child, target, key);
        }
    }
}
