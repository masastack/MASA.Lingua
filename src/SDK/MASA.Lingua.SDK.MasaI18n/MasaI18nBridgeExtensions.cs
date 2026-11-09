using System.Collections.Frozen;
using System.Globalization;
using Masa.BuildingBlocks.Globalization.I18n;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.AspNetCore;
using MASA.Lingua.SDK.MasaI18n;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// MASA Framework I18n 与 MASA.Lingua 的桥接注册。
///
/// 一行注册即可让业务项目现有的全部 MASA I18n 调用（静态 <c>I18n.T()</c>、<c>II18n&lt;T&gt;</c>、
/// FluentValidation 的 <c>MasaLanguageManager</c>、<c>UseMasaExceptionHandler</c> 的错误码翻译等）
/// 改由 Lingua 平台供词并热更新，业务代码零改动：
///
/// <code>
/// builder.Services.AddLinguaMasaI18n(o => builder.Configuration.GetSection("Lingua").Bind(o));
/// </code>
///
/// 注册内容与顺序：
/// 1. <see cref="MasaI18nServiceCollectionExtensions.AddI18n(IServiceCollection, Action{CultureSettings}, Action{I18nOptions})"/>：
///    MASA 框架内置资源（校验/语言管理等）+ II18n 基础设施 + GlobalI18nConfiguration 支持语种；
///    本地 {culture}.json（打包到输出目录的兜底文件）同时注册为 IConfiguration 资源源。
/// 2. <see cref="LinguaAspNetCoreExtensions.AddLinguaLocalization"/>：Lingua 内存 store、同步后台服务
///    （轮询 manifest + SSE + 本地文件监听）、IStringLocalizer 基础设施。
/// 3. 将 MASA <see cref="DefaultResource"/> 的 contributor 替换为「Lingua store 优先、本地 JSON 兜底」的组合。
///    这一步必须在任何 <c>II18n</c> 解析之前完成（MASA 的 <c>I18n&lt;T&gt;</c> 在构造时读取静态资源表）。
///    之后每当同步服务拉到新的语种（manifest 新增语种 / 本地目录新增 {culture}.json），
///    都会自动为该语种补注册 contributor —— 平台扩语种无需重新发版。
/// </summary>
public static class MasaI18nBridgeExtensions
{
    public static IServiceCollection AddLinguaMasaI18n(
        this IServiceCollection services,
        Action<LinguaClientOptions> configure,
        Action<CultureSettings>? cultureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // 不预填 "default"：那会让下面的守卫永远不触发，项目编码配错时一路静默 404（表现为「翻译没生效」）。
        var options = new LinguaClientOptions { ProjectCode = string.Empty };
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ProjectCode))
        {
            throw new InvalidOperationException("Lingua ProjectCode 不能为空（Lingua:ProjectCode）。");
        }

        // 本地兜底目录：相对路径以应用输出目录（AppContext.BaseDirectory）为基准解析，
        // 与 csproj 的 CopyToOutputDirectory 行为一致，不受进程工作目录影响。
        var localDirectory = ResolveDirectory(options.LocalJsonDirectory);
        // 本地文件只用来**在启动阶段**把 MASA 的 contributor 表先铺好（离线可启动、首屏不全是 key）。
        // 「站点支持哪些语种」不以它为准：平台 manifest 的 cultures 才是唯一来源，
        // 由同步服务写入 store.SupportedCultures，并在请求期覆盖 RequestLocalizationOptions（见 UseLinguaI18n）。
        // 这里顺序无语义，默认语种也不从这里推断。
        var cultures = ScanLocalCultures(localDirectory);
        // 本地兜底快照：把注册时扫描到的 {culture}.json 内容冻结为只读字典，作为兜底层最末端。
        // 必要性：MASA 的 LocalI18nResourceContributor 通过 ChangeToken.OnChange 监听文件并重载，
        // 而 Lingua 同步会覆盖同一目录的 {culture}.json —— 重载后「只在本地文件里的 key」会从 MASA 兜底消失
        // （Linux inotify 即时重载，行为确定；Windows 重载滞后才让测试侥幸通过）。
        // 快照层保证「平台没有该 key 时按注册时的本地文案兜底」这一文档语义不随同步/重载时序漂移。
        var localSnapshot = LoadLocalSnapshot(localDirectory, cultures);

        // 1) MASA I18n 基础设施。本地 JSON 目录同时作为 MASA 的 IConfiguration 资源源，
        //    产生的 LocalI18nResourceContributor 会在第 3 步被保留为兜底层。
        services.AddI18n(settings =>
        {
            settings.ResourcesDirectory = string.IsNullOrWhiteSpace(localDirectory)
                ? Path.Combine(I18nResourceResourceConfiguration.BaseDirectory, "i18n")
                : localDirectory;
            settings.SupportedCultures = cultures.Select(c => new CultureModel(c)).ToList();
            cultureSettings?.Invoke(settings);
        });

        // 2) Lingua SDK。store 以现成实例注册：第 3 步的 contributor 需要直接引用它，
        //    AddLinguaLocalization 内部的 TryAdd 会跳过已注册的 store / notifier。
        var notifier = new LinguaChangeNotifier();
        var store = new LinguaFrozenMemoryStore(notifier);
        if (!string.IsNullOrWhiteSpace(localDirectory) && Directory.Exists(localDirectory))
        {
            store.LoadFromLocalDirectory(localDirectory);
        }

        services.TryAddSingleton<ILinguaChangeNotifier>(notifier);
        services.TryAddSingleton<ILinguaMemoryStore>(store);

        services.AddLinguaLocalization(o =>
        {
            configure(o);
            o.LocalJsonDirectory = localDirectory ?? string.Empty;
        });

        // 3) DefaultResource 的 contributor 替换为 Lingua(热更新)优先 + 本地 JSON 兜底的组合。
        //    MASA 的 I18nResource.AddContributor 对重复 culture 抛异常且没有移除 API，
        //    所以整表替换：BaseResourceTypes（含 [InheritResource] 继承的框架资源）原样保留。
        var originalResource = I18nResourceResourceConfiguration.Resources.GetOrNull<DefaultResource>();
        var contributorRegistry = new ContributorRegistry(
            originalResource?.BaseResourceTypes ?? Enumerable.Empty<Type>(),
            originalResource, store, localSnapshot);

        foreach (var culture in cultures)
        {
            contributorRegistry.EnsureCulture(culture);
        }

        // 4) 自愈：同步服务每次拉到新语种（平台 manifest 新增 / 本地文件监听新增）时自动补注册，
        //    避免出现「平台有 fr-FR 但启动时本地没有 fr-FR.json」导致该语种取词返回 Key。
        notifier.OnCultureUpdated += culture => contributorRegistry.EnsureCulture(culture);

        return services;
    }

    /// <summary>
    /// DefaultResource contributor 注册表：
    /// 维护「已注册语种 → Lingua contributor」映射；新增语种时重建整个 I18nResource 并原子覆盖静态资源表
    /// （MASA 的 I18n&lt;T&gt; 每次解析都会重新读取该表，替换对读侧透明、无锁）。
    /// </summary>
    private sealed class ContributorRegistry(
        IEnumerable<Type> baseResourceTypes,
        I18nResource? originalResource,
        ILinguaMemoryStore store,
        IReadOnlyDictionary<string, FrozenDictionary<string, string>> localSnapshot)
    {
        private readonly object _lock = new();
        private readonly List<Type> _baseResourceTypes = [.. baseResourceTypes];
        private readonly Dictionary<string, LinguaI18nResourceContributor> _registered =
            new(StringComparer.OrdinalIgnoreCase);

        public void EnsureCulture(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
            {
                return;
            }

            lock (_lock)
            {
                if (_registered.ContainsKey(culture))
                {
                    return;
                }

                // 本地兜底链：MASA 原生 contributor（框架资源 / 配置热重载）优先，注册时快照（见
                // LoadLocalSnapshot）垫底 —— 后者保证「只在本地文件里的 key」在 Lingua 同步覆盖本地
                // {culture}.json 后仍能按注册时的本地文案解析，行为不随同步/重载时序漂移。
                II18nResourceContributor? fallback = originalResource?.GetResourceContributor(new CultureInfo(culture));
                if (localSnapshot.TryGetValue(culture, out var snapshotEntries))
                {
                    var snapshot = new LocalSnapshotFallbackContributor(culture, snapshotEntries);
                    fallback = fallback is null ? snapshot : new CompositeFallbackContributor(culture, fallback, snapshot);
                }

                _registered[culture] = new LinguaI18nResourceContributor(culture, store, fallback);

                var replacement = new I18nResource(typeof(DefaultResource), _baseResourceTypes);
                foreach (var registered in _registered)
                {
                    replacement.AddContributor(registered.Key, registered.Value);
                }

                I18nResourceResourceConfiguration.Resources[typeof(DefaultResource)] = replacement;
            }
        }
    }

    /// <summary>
    /// 把注册时扫描到的本地 {culture}.json 冻结成「语种 → 扁平 key 字典」快照（只读、永不再变）。
    /// 读不出（缺失 / 损坏）的语种跳过：快照只是兜底层的末端，不挡任何其他层。
    /// </summary>
    private static IReadOnlyDictionary<string, FrozenDictionary<string, string>> LoadLocalSnapshot(
        string? directory,
        IReadOnlyList<string> cultures)
    {
        var snapshot = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || cultures.Count == 0)
        {
            return snapshot;
        }

        foreach (var culture in cultures)
        {
            var path = Path.Combine(directory, $"{culture}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var entries = JsonBundleConverter.FlattenToLookupDictionary(File.ReadAllBytes(path))
                    .ToFrozenDictionary(StringComparer.Ordinal);
                if (entries.Count > 0)
                {
                    snapshot[culture] = entries;
                }
            }
            catch
            {
                // 损坏 / 被占用的本地文件：跳过该语种的快照，不阻塞启动
            }
        }

        return snapshot;
    }

    /// <summary>注册时本地 JSON 快照的兜底 contributor：只按冻结字典解析，与 Lingua store 相同的 key 归一规则。</summary>
    private sealed class LocalSnapshotFallbackContributor(
        string cultureName,
        FrozenDictionary<string, string> entries) : II18nResourceContributor
    {
        public string CultureName { get; } = cultureName;

        public string? GetOrNull(string name) =>
            entries.TryGetValue(name, out var value) || entries.TryGetValue(name.Replace(':', '.'), out value)
                ? value
                : null;
    }

    /// <summary>多个兜底 contributor 的串联：依次尝试，第一个命中即返回。</summary>
    private sealed class CompositeFallbackContributor(
        string cultureName,
        params II18nResourceContributor[] children) : II18nResourceContributor
    {
        public string CultureName { get; } = cultureName;

        public string? GetOrNull(string name)
        {
            foreach (var child in children)
            {
                if (child.GetOrNull(name) is { } value)
                {
                    return value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 解析本地兜底目录：空值返回 null（只用 Lingua 远端）；相对路径基于 AppContext.BaseDirectory。
    /// </summary>
    private static string? ResolveDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        return Path.IsPathRooted(directory)
            ? directory
            : Path.GetFullPath(Path.Combine(I18nResourceResourceConfiguration.BaseDirectory, directory));
    }

    /// <summary>
    /// 扫描本地兜底目录里的 {culture}.json 文件名作为支持语种列表。
    /// 文件名必须是合法 BCP47 语种码（见 <see cref="LinguaCultureFiles"/>）：
    /// supportedCultures.json 等元数据 / 杂项文件天然不是语种资源；返回规范化后的语种名。
    /// </summary>
    private static List<string> ScanLocalCultures(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var cultures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (LinguaCultureFiles.TryResolveCultureFileName(Path.GetFileNameWithoutExtension(file), out var culture))
            {
                cultures.Add(culture);
            }
        }

        return cultures.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
