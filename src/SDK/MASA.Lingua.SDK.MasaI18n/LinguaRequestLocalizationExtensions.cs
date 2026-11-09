using System.Globalization;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.MasaI18n;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;

// 与 MASA 的 UseI18n 同命名空间：宿主里 app.UseLinguaI18n() 不需要任何额外 using。
// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class LinguaRequestLocalizationExtensions
{
    /// <summary>
    /// 替代宿主里的 <c>app.UseI18n()</c>：语种协商仍然交给 MASA，但「支持哪些语种」与「默认语种」
    /// 改为按平台项目的配置（manifest 下发）在请求时覆盖，宿主不需要在代码或本地文件里声明任何语种。
    /// </summary>
    /// <remarks>
    /// 必须在 <c>AddLinguaMasaI18n</c> 之后调用（需要已注册的 <see cref="ILinguaMemoryStore"/>）。
    /// </remarks>
    public static IApplicationBuilder UseLinguaI18n(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var store = app.ApplicationServices.GetRequiredService<ILinguaMemoryStore>();
        var holder = new OptionsHolder();

        // 关键顺序：本中间件先于 RequestLocalizationMiddleware 执行，所以在它读 SupportedCultures
        // 之前就能把平台下发的语种列表写进去。options 实例由下面的 UseI18n 在启动期创建并通过 holder 交回来，
        // 首个请求到达时一定已经就绪。
        app.Use(async (context, next) =>
        {
            if (holder.Options is { } options)
            {
                SyncFromPlatform(options, store, holder);
            }

            await next(context);
        });

        return app.UseI18n(configure: options =>
        {
            // 追加到末尾，不要 Insert(0, …)：中间件命中第一个返回非空的 provider 就 break，
            // 插到前面会让 ?culture= / 语言 Cookie / Accept-Language 全部失效。
            options.RequestCultureProviders.Add(new LinguaDefaultCultureProvider(store));
            holder.Options = options;
        });
    }

    /// <summary>
    /// 把平台下发的语种列表与默认语种同步到 options。集合没变时不动（避免每请求重建 CultureInfo 列表）。
    /// </summary>
    /// <remarks>
    /// 用 lock 保护：这个方法在请求管道里执行，而 <see cref="RequestLocalizationOptions"/> 被所有并发请求共享。
    /// 不加锁的话，两个请求同时发现「集合变了」会各自构建列表并赋值，读到的一方可能在比较与赋值之间
    /// 被另一个线程改掉 holder，导致重复重建（无害但浪费）甚至短暂的不一致快照。
    /// 命中稳态（集合已在 <see cref="OptionsHolder.Applied"/> 里）时只做一次 SequenceEqual，开销可忽略。
    /// </remarks>
    private static void SyncFromPlatform(
        RequestLocalizationOptions options,
        ILinguaMemoryStore store,
        OptionsHolder holder)
    {
        var platformCultures = store.SupportedCultures;
        var defaultCulture = store.DefaultCulture;

        lock (holder.SyncGate)
        {
            if (platformCultures is { Count: > 0 }
                && !platformCultures.SequenceEqual(holder.Applied, StringComparer.OrdinalIgnoreCase))
            {
                var cultures = platformCultures.Select(c => new CultureInfo(c)).ToList();
                options.SupportedCultures = cultures;
                options.SupportedUICultures = cultures;
                holder.Applied = [.. platformCultures];
            }

            if (!string.IsNullOrWhiteSpace(defaultCulture)
                && !string.Equals(
                    options.DefaultRequestCulture.UICulture.Name, defaultCulture, StringComparison.OrdinalIgnoreCase))
            {
                options.DefaultRequestCulture = new RequestCulture(defaultCulture);
            }
        }
    }

    /// <summary>启动期由 <c>UseI18n</c> 回填、请求期读取；仅在启动阶段写一次。</summary>
    private sealed class OptionsHolder
    {
        public RequestLocalizationOptions? Options { get; set; }

        public string[] Applied { get; set; } = [];

        /// <summary>保护 <see cref="Options"/> 的语种/默认语种写入，避免并发请求同时重建列表。</summary>
        public object SyncGate { get; } = new();
    }
}
