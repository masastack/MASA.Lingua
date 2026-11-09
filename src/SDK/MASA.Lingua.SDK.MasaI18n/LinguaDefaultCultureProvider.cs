using MASA.Lingua.SDK.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace MASA.Lingua.SDK.MasaI18n;

/// <summary>
/// 用平台配置的默认语种做请求本地化兜底。
/// <para>
/// 为什么需要它：MASA 的 <c>UseI18n()</c> 在<b>启动时</b>一次性构造 <see cref="RequestLocalizationOptions"/>，
/// 没传 <c>defaultCulture</c> 就取 <c>SupportedCultures.FirstOrDefault()</c>。而 Lingua 的语种列表来自本地
/// <c>{culture}.json</c> 的文件枚举顺序，于是「默认语种」被隐含地绑定到文件系统顺序上；更要紧的是那一刻
/// manifest 还没拉回来，平台配置的 <c>project.DefaultCulture</c> 根本不可用。
/// </para>
/// <para>
/// 这里改为在<b>请求时</b>读 <see cref="ILinguaMemoryStore.DefaultCulture"/>——它由同步服务按 manifest 的
/// <c>defaultCulture</c> 写入，因此平台改默认语种并发布后，下一次请求即生效，无需重启、无需本地配置。
/// </para>
/// <para>
/// 语种列表的覆盖由 <see cref="Microsoft.AspNetCore.Builder.LinguaRequestLocalizationExtensions"/> 在管道里完成
/// （必须在 <c>RequestLocalizationMiddleware</c> 读列表之前，provider 阶段已经太晚）。
/// </para>
/// </summary>
public sealed class LinguaDefaultCultureProvider(ILinguaMemoryStore store) : RequestCultureProvider
{
    /// <summary>
    /// 只在前面所有 provider（<c>?culture=</c> / 语言 Cookie / <c>Accept-Language</c>）都没给出可用语种时才返回结果。
    /// <para>
    /// 必须挂在 provider 链<b>末尾</b>：<c>RequestLocalizationMiddleware</c> 命中第一个返回非空的 provider 就
    /// <c>break</c>，挂到前面会让请求显式指定的语言全部失效。放末尾时语义正好是「显式指定优先，都没给才用平台默认」。
    /// </para>
    /// </summary>
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        // DefaultCulture 为空表示「还未知」（非 Lingua store 实现）——返回 null 交给 ASP.NET 自身的
        // DefaultRequestCulture 兜底，不要在这里猜一个值。
        var culture = store.DefaultCulture;
        return Task.FromResult<ProviderCultureResult?>(
            string.IsNullOrWhiteSpace(culture) ? null : new ProviderCultureResult(culture));
    }
}
