using System.Globalization;
using MASA.Lingua.SDK.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
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
    /// <summary><c>LinguaClient</c> 命名 HttpClient 的默认单次请求超时（应用可对同名客户端再调用 <c>ConfigureHttpClient</c> 覆盖）。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private static void ValidateOptions(LinguaClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectCode))
        {
            throw new InvalidOperationException(
                "LinguaClientOptions.ProjectCode 未配置 —— 必须填业务项目在 Lingua 平台中的项目编码（如 my-maui-app）。");
        }

        // ServerEndpoint 留空是合法的「纯离线 / 仅 Seed」模式，只校验填了的值。
        // 必须是带 scheme 的 http(s) 绝对地址：漏写 scheme（lingua.example.com）或写成 localhost:5000
        //（会被解析成 scheme=localhost 的绝对 URI）都会在每次同步时失败。
        if (!string.IsNullOrWhiteSpace(options.ServerEndpoint) &&
            !(Uri.TryCreate(options.ServerEndpoint, UriKind.Absolute, out var endpoint) &&
              endpoint.Scheme is "http" or "https"))
        {
            throw new InvalidOperationException(
                $"LinguaClientOptions.ServerEndpoint 必须是 http(s) 绝对地址（如 https://lingua.example.com），当前值：{options.ServerEndpoint}");
        }
    }

    /// <summary>
    /// 注册 Lingua（MAUI / Blazor Hybrid / 其他 .NET 8 客户端）：<see cref="LinguaLocalizer"/>（单例，可绑定）、
    /// <see cref="IStringLocalizer"/>、<see cref="ILinguaChangeNotifier"/>（语种更新事件，客户端自定义重绘逻辑可订阅）。
    /// 只需要 <see cref="LinguaClientOptions.ProjectCode"/> 与 <see cref="LinguaClientOptions.ServerEndpoint"/>；语种与默认语种来自平台。
    /// 注册后在应用启动时调用 <c>await sp.GetRequiredService&lt;LinguaLocalizer&gt;().InitializeAsync()</c>。
    /// </summary>
    public static IServiceCollection AddLinguaMaui(this IServiceCollection services, Action<LinguaClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // 注册期就校验配置（与 AspNetCore 端一致：对一份新建的 options 先跑一次 configure）：
        // TrySyncAsync / InitializeAsync 对同步失败一律不抛，项目编码或地址写错会表现成「翻译一直没更新」且毫无声响，
        // 所以必须在这里让它在开发期第一次启动就失败。配置随应用包发布，这个失败会在发版前暴露，而不是线上。
        var probe = new LinguaClientOptions { ProjectCode = string.Empty };
        configure(probe);
        ValidateOptions(probe);

        services.Configure<LinguaClientOptions>(configure);

        // 请求超时：HttpClient 默认 100 秒，黑洞网络（连得上但不回包）下会长时间占着同步闸门，
        // 「回到前台」的后续同步也只能排在后面。移动端 30 秒足够下载语言包。
        // 应用可以在之后对同名客户端再调用 ConfigureHttpClient 覆盖。
        services.AddHttpClient("LinguaClient")
            .ConfigureHttpClient(client => client.Timeout = DefaultRequestTimeout);
        services.TryAddSingleton<ILinguaChangeNotifier, LinguaChangeNotifier>();
        services.TryAddSingleton<ILinguaMemoryStore>(sp =>
            new LinguaFrozenMemoryStore(sp.GetRequiredService<ILinguaChangeNotifier>()));
        services.TryAddSingleton(sp => new LinguaLocalizer(
            sp.GetRequiredService<IOptions<LinguaClientOptions>>().Value,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("LinguaClient"),
            sp.GetRequiredService<ILinguaMemoryStore>(),
            sp.GetRequiredService<ILinguaChangeNotifier>(),
            sp.GetService<ILogger<LinguaLocalizer>>())); // 应用没注册日志时为 null，同步失败只记 LastSyncError

        services.TryAddTransient<IStringLocalizer>(sp => new LinguaMauiStringLocalizer(sp.GetRequiredService<LinguaLocalizer>()));
        services.TryAddTransient(typeof(IStringLocalizer<>), typeof(LinguaMauiStringLocalizer<>));

        return services;
    }
}
