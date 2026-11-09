using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.MasaBlazor;
using Microsoft.Extensions.DependencyInjection;

// 扩展方法挂在 DI 命名空间下便于宿主无 using 直接调用；
// hosted service / 合并器一律放 MASA.Lingua.SDK.MasaBlazor 命名空间（LinguaMasaBlazorServices.cs）：
// 类名同时是日志类别，挂在 Microsoft.* 前缀下会被宿主常见的 "Microsoft": "Warning" 日志规则过滤，
// 启动/热更新结论日志就静默丢失了。
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Blazor WebAssembly / Masa.Blazor 应用的 Lingua 词条接入（一行注册，按宿主形态自动选模式）：
///
/// <code>
/// // Program.cs（AddMasaStackComponent / AddI18nForServer / AddI18nForWasm 之后）
/// builder.Services.AddLinguaForMasaBlazor(o => builder.Configuration.GetSection("Lingua").Bind(o));
/// // wwwroot/appsettings.json
/// { "Lingua": { "ServerEndpoint": "http://lingua-host:port", "ProjectCode": "your-app" } }
/// </code>
///
/// 两种工作模式：
/// 1. **独立拉取模式**（Blazor WASM / 未注册 Lingua store 的应用）：注册 IHostedService，
///    首屏渲染前从 Lingua 拉取 manifest + 各语种 {culture}.json，展平后合并进 Masa.Blazor 的 I18n 缓存，
///    Lingua 词条优先于静态 wwwroot/i18n/*.json（后者保持原样作为离线兜底先行加载）；
///    Lingua 不可达 / 未发布 / 超时（10 秒）时静默降级，应用照常以静态词条启动；
///    平台发布新版本后，用户刷新页面即可拿到新词条。
/// 2. **共享 store 订阅模式**（检测到 <see cref="ILinguaMemoryStore"/> / <see cref="ILinguaChangeNotifier"/>
///    已注册，典型为与 <c>AddLinguaMasaI18n</c> 同宿主的 Blazor Server 双链应用）：不再独立发 HTTP，
///    启动时把共享 store 已载入的各语种合并进 I18n 缓存，并订阅语种更新通知——
///    <c>LinguaJsonSyncService</c>（SSE + 轮询）每次同步某语种后自动重合并该语种。
///    UI 词条与 MASA Framework I18n 词条共用同一条同步链，平台发布后新 circuit 立即读到新词条，无需重启进程。
/// </summary>
public static class MasaBlazorLinguaExtensions
{
    public static IServiceCollection AddLinguaForMasaBlazor(
        this IServiceCollection services,
        Action<LinguaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // 共享 store 订阅模式：AddLinguaMasaI18n（或 AddLinguaLocalization）已注册 Lingua 单例时启用。
        // 只消费现成实例、不重复拉取，避免与 LinguaJsonSyncService 形成双份同步。
        if (services.IsSharedLinguaStoreRegistered())
        {
            services.AddHostedService<LinguaMasaBlazorStoreBridgeService>();
            return services;
        }

        // 独立拉取模式（WASM / 纯 Blazor 应用）
        services.Configure(configure);
        services.AddHostedService<LinguaMasaBlazorI18nService>();
        return services;
    }

    /// <summary>共享 Lingua store 是否已注册（即宿主已经跑过 AddLinguaMasaI18n / AddLinguaLocalization）。</summary>
    private static bool IsSharedLinguaStoreRegistered(this IServiceCollection services) =>
        services.Any(static d => d.ServiceType == typeof(ILinguaMemoryStore))
        && services.Any(static d => d.ServiceType == typeof(ILinguaChangeNotifier));
}
