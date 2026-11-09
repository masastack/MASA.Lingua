using System.Globalization;
using MASA.Lingua.SDK.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.SDK.Maui;

/// <summary>标准 <see cref="IStringLocalizer"/> 适配：让共享库 / Blazor Hybrid 页面里的 <c>L["order.submitBtn"]</c> 也走 Lingua。</summary>
public sealed class LinguaMauiStringLocalizer(LinguaLocalizer localizer) : IStringLocalizer
{
    public LocalizedString this[string name] =>
        localizer.TryGetValue(name, out var value)
            ? new LocalizedString(name, value, resourceNotFound: false)
            : new LocalizedString(name, name, resourceNotFound: true);

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var localized = this[name];
            return new LocalizedString(
                name,
                MASA.Lingua.Core.Formatting.LinguaInterpolation.Format(localized.Value, arguments),
                localized.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        localizer.GetAll().Select(kv => new LocalizedString(kv.Key, kv.Value, resourceNotFound: false));
}

public sealed class LinguaMauiStringLocalizer<T>(LinguaLocalizer localizer) : IStringLocalizer<T>
{
    private readonly LinguaMauiStringLocalizer _inner = new(localizer);

    public LocalizedString this[string name] => _inner[name];
    public LocalizedString this[string name, params object[] arguments] => _inner[name, arguments];
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => _inner.GetAllStrings(includeParentCultures);
}

public static class LinguaMauiExtensions
{
    /// <summary>
    /// 注册 Lingua（MAUI / Blazor Hybrid / 其他 .NET 8 客户端）：<see cref="LinguaLocalizer"/>（单例，可绑定）、
    /// <see cref="IStringLocalizer"/>、<see cref="ILinguaChangeNotifier"/>（语种更新事件，客户端自定义重绘逻辑可订阅）。
    /// 只需要 <see cref="LinguaClientOptions.ProjectCode"/> 与 <see cref="LinguaClientOptions.ServerEndpoint"/>；语种与默认语种来自平台。
    /// 注册后在应用启动时调用 <c>await sp.GetRequiredService&lt;LinguaLocalizer&gt;().InitializeAsync()</c>。
    /// </summary>
    public static IServiceCollection AddLinguaMaui(this IServiceCollection services, Action<LinguaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure<LinguaClientOptions>(configure);

        services.AddHttpClient("LinguaClient");
        services.TryAddSingleton<ILinguaChangeNotifier, LinguaChangeNotifier>();
        services.TryAddSingleton<ILinguaMemoryStore>(sp =>
            new LinguaFrozenMemoryStore(sp.GetRequiredService<ILinguaChangeNotifier>()));
        services.TryAddSingleton(sp => new LinguaLocalizer(
            sp.GetRequiredService<IOptions<LinguaClientOptions>>().Value,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LinguaClient"),
            sp.GetRequiredService<ILinguaMemoryStore>(),
            sp.GetRequiredService<ILinguaChangeNotifier>()));

        services.TryAddTransient<IStringLocalizer>(sp => new LinguaMauiStringLocalizer(sp.GetRequiredService<LinguaLocalizer>()));
        services.TryAddTransient(typeof(IStringLocalizer<>), typeof(LinguaMauiStringLocalizer<>));

        return services;
    }
}
