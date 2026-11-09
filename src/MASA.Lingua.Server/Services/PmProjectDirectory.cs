using System.Runtime.ExceptionServices;
using Masa.BuildingBlocks.StackSdks.Config;
using Masa.BuildingBlocks.StackSdks.Pm;
using Masa.Contrib.StackSdks.Config;
using Microsoft.Extensions.Caching.Memory;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// PM 应用。<paramref name="Identity"/> 即 Lingua 本地多语言配置的项目编码；
/// <paramref name="Name"/> 仅用于界面展示——Lingua 本地库不存项目元数据。
/// </summary>
public sealed record PmApp(string Identity, string Name);

/// <summary>
/// PM 项目及其下属应用，供切换器做「项目 → 应用」两级选择。
/// <para>
/// 注意：PM 里同一个应用 Identity 可能同时挂在不同项目下（例如 <c>Lonsid_MESPro_Web_Host</c>），
/// 因此「当前选的项目」不能只靠应用 Identity 反推，调用方需自行记住用户选的项目。
/// </para>
/// </summary>
public sealed record PmProject(string Identity, string Name, IReadOnlyList<PmApp> Apps);

/// <summary>
/// MASA Stack PM（项目管理）侧的项目应用清单来源。
/// <para>
/// 项目身份与展示信息一律以 PM 为准，Lingua 本地库只按应用 Identity 存「编码 ↔ 多语言配置」的关联；
/// 因此切换器数据源、合法性判断都走这里，本地库不再提供项目清单。
/// </para>
/// </summary>
public interface IPmProjectDirectory
{
    /// <summary>
    /// 当前环境（取自 MasaStackConfig）下的项目及其应用：项目按名称排序，项目内应用按名称排序。
    /// 没有任何应用的项目不返回——Lingua 的多语言配置挂在应用上，这样的项目选了也无事可做。
    /// 调用失败直接抛出——严格依赖 PM，调用方（切换器）负责呈现错误态，不做本地兜底。
    /// </summary>
    /// <param name="forceRefresh">
    /// 跳过成功/失败缓存直接请求 PM。给用户手动「重试」用——否则点重试会被短时失败缓存挡回。
    /// </param>
    Task<IReadOnlyList<PmProject>> GetProjectsAsync(bool forceRefresh = false, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IPmProjectDirectory"/> 默认实现。把「每个页面加载 / 每个电路 / 每个预渲染」放大出来的调用收敛到 PM 侧：
/// <list type="bullet">
/// <item>成功结果缓存 60 秒——预渲染与随后的电路初始化、多标签页共享同一次结果。</item>
/// <item>失败结果缓存 10 秒——PM 不可用时预渲染+电路不会各打一次，也不会刷屏 SDK 的 HttpClient 日志；手动重试走 <c>forceRefresh</c>。</item>
/// <item>单飞——并发的缓存未命中只发一次 PM 请求，其余等待并复用其结果（含失败）。</item>
/// </list>
/// </summary>
public sealed class PmProjectDirectory(
    IServiceScopeFactory scopeFactory,
    IMemoryCache cache,
    ILogger<PmProjectDirectory> logger,
    TimeSpan requestTimeout) : IPmProjectDirectory
{
    private const string CacheKey = "lingua.pm.project-apps";
    private const string FailureCacheKey = "lingua.pm.project-apps.failure";

    /// <summary>
    /// PM SDK 内部固定的「按环境取项目+应用」路径（<c>Masa.Contrib.StackSdks.Pm</c> 的
    /// <c>ProjectService.GetProjectAppsAsync</c>）。SDK 不透出该路径，这里复制一份仅用于日志，
    /// 便于排查这次请求打到了哪个环境、哪个 PM 域名。
    /// </summary>
    private const string ProjectAppsRoute = "open-api/projectwithapps";

    private static readonly TimeSpan SuccessWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<PmProject>> GetProjectsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && GetCachedOrThrow() is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync(ct);
        var context = new FetchContext();
        try
        {
            // 等锁期间前一个请求可能已写入结果（成功或失败）：再查一次，避免并发请求各打一次 PM
            if (!forceRefresh && GetCachedOrThrow() is { } afterWait)
            {
                return afterWait;
            }

            try
            {
                // 整体卸到线程池 + 单一超时上界（覆盖 token + PM 请求全程）：
                // Caller 构造会同步解析 TokenProvider → ITokenGenerater.Generater() 同步阻塞换 token（见 Masa
                // Stack SDK 的 TokenProvider scoped 工厂），这段发生在任何 await 之前——不卸线程的话，
                // Blazor 电路里它直接冻结渲染器 Dispatcher，且 await 侧的超时（WaitAsync）根本没开始计时、管不到它，
                // 页面表现为永远「正在加载项目清单…」。卸到线程池后 Dispatcher 不再被阻塞，上界才能真正兜底。
                var projects = await Task.Run(() => FetchCoreAsync(context), ct)
                    .WaitAsync(requestTimeout, ct);
                cache.Set(CacheKey, projects, SuccessWindow);
                cache.Remove(FailureCacheKey);
                return projects;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // 失败必须落到后端日志：异常向上只被 MainLayout 塞进 UI tooltip（悬停可见），docker logs 没有这条就无线索。
                // OCE 要单独甄别：HttpClient 请求超时抛的 TaskCanceledException 也派生自 OCE —— 调用方未取消（ct 未触发）
                // 时按真实失败处理（记日志 + 进失败缓存），只有调用方自己取消（ct 已触发）才静默放行。
                cache.Remove(CacheKey);
                cache.Set(FailureCacheKey, ex, FailureWindow);
                logger.LogWarning(ex,
                    "PM 项目清单获取失败（环境 {Environment}，地址 {RequestUri}），已进失败缓存 {FailureSeconds}s 等待手动重试。",
                    context.Environment ?? "（未解析）",
                    context.RequestUri ?? "（未解析）",
                    FailureWindow.TotalSeconds);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>单次取数的现场信息：失败日志用它定位是哪条腿（token / PM 请求）出的问题。</summary>
    private sealed class FetchContext
    {
        public string? Environment { get; set; }
        public string? RequestUri { get; set; }
    }

    private async Task<IReadOnlyList<PmProject>> FetchCoreAsync(FetchContext context)
    {
        // IPmClient / IMasaStackConfig 的注册生命周期由 Masa Stack SDK 决定（Caller 链路通常是 Scoped），
        // 本服务注册为单例，因此每次调用都开独立 scope 解析，避免捕获已释放的作用域实例。
        // 先解析配置并填入 context（外层失败日志要用），再解析 IPmClient —— 后者的构造会同步换 token，可能挂。
        using var scope = scopeFactory.CreateScope();
        var masaStackConfig = scope.ServiceProvider.GetRequiredService<IMasaStackConfig>();

        // 项目清单是按环境过滤的：同一个 PM 域名下 Development / Staging / Production 返回不同项目，
        // 出问题（少项目、404、鉴权失败）时先看这两个值就能定位是不是环境或域名配错了。
        context.Environment = masaStackConfig.Environment;
        context.RequestUri = BuildProjectAppsRequestUri(masaStackConfig, context.Environment);
        logger.LogInformation(
            "请求 PM 项目应用清单：环境 {Environment}，请求地址 {RequestUri}",
            context.Environment,
            context.RequestUri);

        var pmClient = scope.ServiceProvider.GetRequiredService<IPmClient>();

        var projects = await pmClient.ProjectService.GetProjectAppsAsync(context.Environment);

        return projects
            .Select(project => new PmProject(
                project.Identity,
                project.Name,
                (project.Apps ?? [])
                    .Select(app => new PmApp(app.Identity, app.Name))
                    .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .Where(project => project.Apps.Count > 0)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>命中成功缓存返回清单；命中失败缓存重新抛出原异常；都没有返回 null。</summary>
    private IReadOnlyList<PmProject>? GetCachedOrThrow()
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<PmProject>? cached) && cached is not null)
        {
            return cached;
        }

        if (cache.TryGetValue(FailureCacheKey, out Exception? failure) && failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return null;
    }

    /// <summary>
    /// 拼出这次请求的目标地址（PM 服务域名 + SDK 内部路由 + 环境名），仅用于日志。
    /// <para>
    /// 域名来自 DCC 公共配置，解析失败时只把原因写进日志，绝不影响请求本身——
    /// 真正发请求的是 SDK，这里不复制它的失败场景。
    /// </para>
    /// </summary>
    private static string BuildProjectAppsRequestUri(IMasaStackConfig masaStackConfig, string environment)
    {
        var route = $"{ProjectAppsRoute}/{environment}";

        string domain;
        try
        {
            domain = masaStackConfig.GetPmServiceDomain();
        }
        catch (Exception ex)
        {
            return $"{route}（PM 域名解析失败：{ex.Message}）";
        }

        return string.IsNullOrWhiteSpace(domain)
            ? $"{route}（PM 域名未解析：DCC 公共配置缺少 pm 项目的 service.domain）"
            : $"{domain.TrimEnd('/')}/{route}";
    }
}
