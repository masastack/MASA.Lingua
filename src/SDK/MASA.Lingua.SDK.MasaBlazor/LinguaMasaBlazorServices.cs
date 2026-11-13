using System.Globalization;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using MASA.Lingua.SDK.Abstractions;
using Masa.Blazor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// 类名同时是日志类别：必须挂在 MASA.Lingua.SDK.* 前缀下，
// 挂在 Microsoft.* 下会被宿主常见的 "Microsoft": "Warning" 日志规则过滤，启动/热更新结论日志会静默丢失。
namespace MASA.Lingua.SDK.MasaBlazor;

/// <summary>
/// 合并进 Masa.Blazor I18n 缓存前的键值表：Masa.Blazor 按忽略大小写匹配词条，因此用 OrdinalIgnoreCase 字典承载，
/// 与 Lingua「命名空间 / Key 忽略大小写」的口径一致。新版 Lingua 服务端保证产物里没有仅大小写不同的 Key；
/// 若连的是旧版服务端（存量数据里仍有 <c>Confirm.Title</c> 与 <c>confirm.title</c> 两条），进入这张表时后者会覆盖前者 ——
/// 这里把「仅大小写不同的重复键」数出来并告警一次，而不是静默丢掉其中一条。
/// </summary>
internal sealed class MasaBlazorKeyMap
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exactKeys = new(StringComparer.Ordinal);
    private readonly List<string> _collisions = [];

    public void Put(string key, string value)
    {
        // 精确键首次出现、但忽略大小写后已存在 = 仅大小写不同的重复（同一个精确键重复 Put 只是覆盖，不算）
        var firstExactOccurrence = _exactKeys.Add(key);
        if (firstExactOccurrence && _map.ContainsKey(key))
        {
            _collisions.Add(key);
        }

        _map[key] = value;
    }

    public Dictionary<string, string> Build(ILogger logger, string culture)
    {
        if (_collisions.Count > 0)
        {
            logger.LogWarning(
                "语种 {Culture} 有 {Count} 处词条 Key 仅大小写不同（如 {Sample}），Masa.Blazor I18n 忽略大小写匹配，后者会覆盖前者。"
                + "请在 Lingua 后台统一这些 Key 的写法（发布页「提示」里有明细）。",
                culture, _collisions.Count, _collisions[0]);
        }

        return _map;
    }
}

/// <summary>
/// 词条合并进 Masa.Blazor I18n 缓存的公共路径，供独立拉取与共享 store 订阅两种模式复用。
/// <para>
/// 公开的 <see cref="I18n.AddLocale"/> 固定按「内置词条」合并（静态 JSON 反而会覆盖新词条），
/// 因此反射调用 I18nCache.AddLocale(culture, map, isBuiltInLocale: false) 让 Lingua 词条优先；
/// 反射不可用时降级为公开 API（仅对静态文件里没有的 Key 生效）。
/// </para>
/// </summary>
internal static class MasaBlazorI18nCacheMerger
{
    public static void Merge(string culture, Dictionary<string, string> map, ILogger logger, IServiceScopeFactory? scopeFactory)
    {
        var cultureInfo = CultureInfo.CreateSpecificCulture(culture);

        try
        {
            var cacheType = typeof(I18n).Assembly.GetType("Masa.Blazor.Core.I18n.I18nCache", throwOnError: false);
            var addLocale = cacheType?.GetMethod("AddLocale",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            if (addLocale is not null)
            {
                addLocale.Invoke(null, new object?[] { cultureInfo, map, false });
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "反射合并 Masa.Blazor I18n 缓存失败，降级为公开 AddLocale API。");
        }

        try
        {
            if (scopeFactory is null)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var i18n = scope.ServiceProvider.GetService<I18n>();
            i18n?.AddLocale(cultureInfo, map);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "合并 {Culture} 词条进 Masa.Blazor I18n 失败，该语种将使用静态词条。", culture);
        }
    }
}

/// <summary>独立拉取模式：启动时从 Lingua 拉取词条并合并进 Masa.Blazor I18n 缓存的 hosted service。</summary>
public sealed class LinguaMasaBlazorI18nService(
    IServiceScopeFactory scopeFactory,
    IOptions<LinguaClientOptions> optionsAccessor,
    ILogger<LinguaMasaBlazorI18nService> logger) : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record LinguaManifest(string Project, string Version, List<LinguaManifestCulture> Cultures);
    private sealed record LinguaManifestCulture(string Culture, string ETag, string Url, string? FlatUrl = null);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(options.ServerEndpoint) || string.IsNullOrWhiteSpace(options.ProjectCode))
        {
            return;
        }

        var endpoint = options.ServerEndpoint.TrimEnd('/');
        try
        {
            // 整个拉取限时 10 秒：词条同步绝不能拖慢应用启动；超时/失败都走静态兜底
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            var manifest = await http.GetFromJsonAsync<LinguaManifest>(
                LinguaProtocol.Combine(endpoint, LinguaProtocol.ManifestPath(options.ProjectCode)),
                JsonOptions, timeout.Token);
            if (manifest?.Cultures is null or { Count: 0 })
            {
                logger.LogDebug("Lingua 项目 {Project} 暂无发布产物，使用静态词条兜底。", options.ProjectCode);
                return;
            }

            foreach (var culture in manifest.Cultures)
            {
                // 优先扁平格式：Flatten 对嵌套 / 扁平两种形状产出同一份扁平键值表，而扁平文件没有键路径冲突的表达缺陷
                var url = LinguaProtocol.Combine(endpoint, string.IsNullOrWhiteSpace(culture.FlatUrl) ? culture.Url : culture.FlatUrl);
                var bundle = await http.GetFromJsonAsync<JsonElement>(url, JsonOptions, timeout.Token);
                var map = Flatten(bundle, logger, culture.Culture);
                if (map.Count == 0)
                {
                    continue;
                }

                MasaBlazorI18nCacheMerger.Merge(culture.Culture, map, logger, scopeFactory);
                logger.LogInformation("已从 Lingua 合并项目 {Project} 的 {Culture} 词条 {Count} 条（版本 {Version}）。",
                    options.ProjectCode, culture.Culture, map.Count, manifest.Version);
            }
        }
        catch (Exception ex)
        {
            // 静态 wwwroot/i18n/*.json 已先行加载（AddMasaStackComponent / AddI18nForWasm），Lingua 不可达时应用照常工作
            logger.LogWarning(ex, "从 Lingua 拉取词条失败，使用静态词条兜底。");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 把 Lingua 的发布 bundle 展平为 Masa.Blazor 的扁平键值：
    /// 根级裸 key（项目根命名空间）直接映射为全局 key（与 T(key) 查找一致）；
    /// 命名空间对象转 "{Namespace}.{key}"（与 T(scope, key) 的 "{scope}.{key}" 拼接查找一致）。
    /// </summary>
    private static Dictionary<string, string> Flatten(JsonElement root, ILogger logger, string culture)
    {
        var map = new MasaBlazorKeyMap();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return map.Build(logger, culture);
        }

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var entry in EnumerateLeaves(property.Value, prefix: string.Empty))
                    {
                        map.Put($"{property.Name}.{entry.Key}", entry.Value);
                    }
                    break;

                case JsonValueKind.String:
                    // 项目根：发布产物里的根级裸 key
                    map.Put(property.Name, property.Value.GetString() ?? string.Empty);
                    break;

                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    map.Put(property.Name, property.Value.ToString());
                    break;
            }
        }

        return map.Build(logger, culture);
    }

    private static IEnumerable<KeyValuePair<string, string>> EnumerateLeaves(JsonElement element, string prefix)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var nested in EnumerateLeaves(property.Value, path))
                    {
                        yield return nested;
                    }
                    break;
                case JsonValueKind.String:
                    yield return new(path, property.Value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    yield return new(path, property.Value.ToString());
                    break;
            }
        }
    }
}

/// <summary>
/// 共享 store 订阅模式：宿主已注册 <see cref="ILinguaMemoryStore"/> / <see cref="ILinguaChangeNotifier"/>
/// （<c>AddLinguaMasaI18n</c> 或 <c>AddLinguaLocalization</c>）时启用。
/// 启动时把 store 已载入的各语种合并进 Masa.Blazor I18n 缓存；订阅语种更新通知，
/// <c>LinguaJsonSyncService</c> 每次同步某语种（SSE 发布 / 空闲对账 / 本地文件变更）后自动重合并该语种——
/// UI 词条与 MASA Framework I18n 词条共用同一条 SSE/轮询同步链，平台发布后新 circuit 立即读到新词条，
/// 无需重启进程。已打开的 circuit 仍使用合并前的快照，刷新页面即拿到新词条。
/// </summary>
public sealed class LinguaMasaBlazorStoreBridgeService(
    ILinguaMemoryStore store,
    ILinguaChangeNotifier notifier,
    IServiceScopeFactory scopeFactory,
    ILogger<LinguaMasaBlazorStoreBridgeService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 初始合并：store 在 DI 注册阶段已同步加载本地 {culture}.json（离线兜底），
        // LinguaJsonSyncService 先于本服务启动，首个对账若已完成则平台词条也已就位；
        // 之后到达的更新一律通过 OnCultureUpdated 补合并，两个时序来源都闭环。
        var merged = 0;
        foreach (var culture in store.GetLoadedCultures())
        {
            if (MergeCulture(culture))
            {
                merged++;
            }
        }

        notifier.OnCultureUpdated += MergeCultureIfChanged;
        logger.LogInformation(
            "Lingua UI 词条已以共享 store 订阅模式接入 Masa.Blazor：启动合并 {Merged} 个语种，后续随 LinguaJsonSyncService 的发布事件自动热更新。",
            merged);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.OnCultureUpdated -= MergeCultureIfChanged;
        return Task.CompletedTask;
    }

    private void MergeCultureIfChanged(string culture) => MergeCulture(culture);

    /// <summary>把 store 中该语种的词条合并进 Masa.Blazor I18n 缓存；返回是否实际合并了词条。</summary>
    private bool MergeCulture(string culture)
    {
        try
        {
            // GetAll 返回「ns.key」点分与根级裸 key 两种形式（含 ':' 的冗余变体已被过滤），
            // 与 Masa.Blazor 的 T(key) / T(scope, key) 查找格式一致，无需再展平转换。
            var keyMap = new MasaBlazorKeyMap();
            foreach (var kv in store.GetAll(culture))
            {
                keyMap.Put(kv.Key, kv.Value);
            }

            var map = keyMap.Build(logger, culture);
            if (map.Count == 0)
            {
                return false;
            }

            MasaBlazorI18nCacheMerger.Merge(culture, map, logger, scopeFactory);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "从共享 Lingua store 合并 {Culture} 词条进 Masa.Blazor I18n 失败，该语种将使用静态词条。", culture);
            return false;
        }
    }
}
