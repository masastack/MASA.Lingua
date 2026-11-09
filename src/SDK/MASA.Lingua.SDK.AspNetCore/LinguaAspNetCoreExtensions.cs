using MASA.Lingua.SDK.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;

namespace MASA.Lingua.SDK.AspNetCore;

public static class LinguaAspNetCoreExtensions
{
    /// <summary>
    /// 注册基于 {culture}.json 的 ASP.NET Core / Blazor Server 薄适配器
    /// </summary>
    public static IServiceCollection AddLinguaLocalization(
        this IServiceCollection services,
        Action<LinguaClientOptions> configure)
    {
        var opts = new LinguaClientOptions { ProjectCode = string.Empty };
        configure(opts);

        // 不再静默兜底成 "default"：项目编码配错会一路 404，最后表现成「翻译没生效」，极难排查。
        if (string.IsNullOrWhiteSpace(opts.ProjectCode))
        {
            throw new InvalidOperationException(
                "LinguaClientOptions.ProjectCode 未配置 —— 必须填业务项目在 Lingua 平台中的项目编码（如 auth-web-staging）。");
        }

        if (opts.ServerEndpoint is { } endpoint &&
            !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"LinguaClientOptions.ServerEndpoint 不是合法绝对地址：{endpoint}");
        }

        services.Configure(configure);
        services.AddHttpClient("LinguaClient");

        services.TryAddSingleton<ILinguaChangeNotifier, LinguaChangeNotifier>();
        services.TryAddSingleton<ILinguaMemoryStore>(sp =>
        {
            var store = new LinguaFrozenMemoryStore(sp.GetRequiredService<ILinguaChangeNotifier>());

            store.LoadFromLocalDirectory(opts.LocalJsonDirectory);
            return store;
        });

        services.TryAddSingleton<IMissingKeyReporter, HttpMissingKeyReporter>();
        services.TryAddSingleton<IStringLocalizerFactory, LinguaStringLocalizerFactory>();
        services.TryAddTransient<IStringLocalizer>(sp =>
            new LinguaStringLocalizer(
                sp.GetRequiredService<ILinguaMemoryStore>(),
                sp.GetService<IMissingKeyReporter>()));
        services.TryAddTransient(typeof(IStringLocalizer<>), typeof(LinguaStringLocalizer<>));

        services.AddSingleton<LinguaJsonSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<LinguaJsonSyncService>());

        return services;
    }
}
