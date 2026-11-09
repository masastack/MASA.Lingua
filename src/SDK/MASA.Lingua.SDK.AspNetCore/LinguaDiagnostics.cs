using MASA.Lingua.SDK.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.SDK.AspNetCore;

public sealed record LinguaCultureStatus(string Culture, int Entries);

/// <summary>
/// 多语言运行状态快照：不查平台、不依赖任何业务代码，回答「词条有没有加载进来、是不是最新版本、SSE 通不通」。
/// <para>
/// 全部字段都来自 SDK 自身的运行态（内存字典 / 同步服务 / 配置），因此对任何宿主（ASP.NET Core、
/// Blazor Server、自带 store 的实现）都成立，不含任何项目、语种或词条级的硬编码。
/// </para>
/// </summary>
public sealed record LinguaStatus(
    string ProjectCode,
    string? ServerEndpoint,
    string Mode,
    string? Version,
    bool ServerEventsConnected,
    string? DefaultCulture,
    IReadOnlyList<LinguaCultureStatus> Cultures,
    bool Healthy,
    DateTimeOffset CheckedAt);

/// <summary>
/// 组装 <see cref="LinguaStatus"/>：语种来自 <see cref="ILinguaMemoryStore.GetLoadedCultures"/>，
/// 词条数来自内存字典本身，版本与 SSE 状态来自 <see cref="LinguaJsonSyncService"/>。
/// </summary>
public sealed class LinguaStatusProvider(
    ILinguaMemoryStore memoryStore,
    LinguaJsonSyncService syncService,
    IOptions<LinguaClientOptions> optionsAccessor)
{
    public LinguaStatus CreateStatus()
    {
        var options = optionsAccessor.Value;

        var cultures = memoryStore.GetLoadedCultures()
            .Select(culture => new LinguaCultureStatus(culture, memoryStore.GetAll(culture).Count()))
            // 有词条的语种排前面，其次按名称稳定排序：运维一眼能看出「哪些语种真的加载到了内容」
            .OrderByDescending(c => c.Entries)
            .ThenBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LinguaStatus(
            ProjectCode: options.ProjectCode,
            ServerEndpoint: options.ServerEndpoint,
            Mode: string.IsNullOrWhiteSpace(options.ServerEndpoint) ? "offline" : "online",
            Version: syncService.CurrentVersion,
            ServerEventsConnected: syncService.ServerEventsConnected,
            DefaultCulture: memoryStore.DefaultCulture,
            Cultures: cultures,
            Healthy: cultures.Any(c => c.Entries > 0),
            CheckedAt: DateTimeOffset.Now);
    }

    /// <summary>日志用的紧凑语种摘要，例如 <c>zh-CN:101, en-US:101</c>。</summary>
    public static string Describe(LinguaStatus status) =>
        status.Cultures.Count == 0
            ? "(no cultures loaded)"
            : string.Join(", ", status.Cultures.Select(c => $"{c.Culture}:{c.Entries}"));
}

/// <summary>
/// 启动自检：在首次同步完成（或确认离线模式）后打一条明确的结论日志，
/// 让「启动后多语言到底可不可用」在日志里一眼可见，而不是等用户看到界面全是 key。
/// </summary>
public sealed class LinguaStartupCheck(
    LinguaStatusProvider statusProvider,
    ILogger<LinguaStartupCheck> logger) : BackgroundService
{
    /// <summary>等待首次同步的上限：超时只告警，不影响宿主启动（继续用本地兜底内容）。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var deadline = DateTimeOffset.UtcNow + Timeout;
            while (!stoppingToken.IsCancellationRequested)
            {
                var status = statusProvider.CreateStatus();

                if (status.Mode == "offline")
                {
                    logger.LogInformation(
                        "Lingua 离线模式（未配置 ServerEndpoint）：项目 {ProjectCode}，已加载语种 {Cultures}。",
                        status.ProjectCode, LinguaStatusProvider.Describe(status));
                    return;
                }

                if (status.Version is not null)
                {
                    logger.LogInformation(
                        "Lingua 词条已就绪：项目 {ProjectCode}，版本 {Version}，SSE {Sse}，默认语种 {DefaultCulture}，语种 {Cultures}。",
                        status.ProjectCode,
                        status.Version,
                        status.ServerEventsConnected ? "已连接" : "未连接（靠空闲对账兜底）",
                        status.DefaultCulture,
                        LinguaStatusProvider.Describe(status));
                    return;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    logger.LogWarning(
                        "Lingua 启动 {Seconds} 秒内未完成同步：项目 {ProjectCode}，服务地址 {Endpoint}。"
                        + "请检查服务可达性、项目编码是否正确、项目是否已发布（manifest 是否 200）。当前已加载语种 {Cultures}。",
                        Timeout.TotalSeconds,
                        status.ProjectCode,
                        status.ServerEndpoint,
                        LinguaStatusProvider.Describe(status));
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主正常停机
        }
        catch (Exception ex)
        {
            // 自检失败绝不能影响宿主启动
            logger.LogError(ex, "Lingua 启动自检异常（不影响宿主启动，可调用 LinguaStatusProvider.CreateStatus 查看状态）。");
        }
    }
}

public static class LinguaDiagnosticsExtensions
{
    /// <summary>
    /// 注册多语言诊断：<see cref="LinguaStatusProvider"/> + 启动结论日志（<see cref="LinguaStartupCheck"/>）。
    /// <para>
    /// 必须在 <c>AddLinguaLocalization</c>（或基于它的 <c>AddLinguaMasaI18n</c>）之后调用：
    /// 诊断读的是它们注册的内存字典与同步服务。重复调用是幂等的。
    /// </para>
    /// </summary>
    public static IServiceCollection AddLinguaDiagnostics(this IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ILinguaMemoryStore)))
        {
            throw new InvalidOperationException(
                "AddLinguaDiagnostics 必须在 AddLinguaLocalization / AddLinguaMasaI18n 之后调用："
                + "诊断读取的是它们注册的 ILinguaMemoryStore 与 LinguaJsonSyncService。");
        }

        services.TryAddSingleton<LinguaStatusProvider>();
        services.AddHostedService<LinguaStartupCheck>();
        return services;
    }

    /// <summary>
    /// 映射状态端点并返回端点约定构建器：鉴权策略由宿主决定，例如
    /// <c>app.MapLinguaStatus().AllowAnonymous()</c> 或 <c>app.MapLinguaStatus().RequireAuthorization()</c>。
    /// </summary>
    public static IEndpointConventionBuilder MapLinguaStatus(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/lingua/status")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapGet(
            pattern,
            (LinguaStatusProvider provider) => Results.Json(provider.CreateStatus()));
    }
}
