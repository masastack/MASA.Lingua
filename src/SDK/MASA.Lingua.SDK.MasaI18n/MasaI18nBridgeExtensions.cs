using System.Collections.Frozen;
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
    /// <summary>
    /// 同步落盘子目录（位于本地兜底目录下）。平台下发的 {culture}.json 只写这里，
    /// 随程序发布的兜底文件永不被覆盖：MASA 以 reloadOnChange 监听兜底文件，覆盖它会让「只在本地的 key」
    /// 在进程内随重载消失、重启后彻底丢失。
    /// </summary>
    public const string SyncCacheDirectoryName = ".lingua-cache";

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
        var syncCacheDirectory = localDirectory is null ? null : Path.Combine(localDirectory, SyncCacheDirectoryName);
        // 本地兜底层：把注册时扫描到的 {culture}.json 冻结为只读字典。
        // 不走 MASA 的 AddJson / LocalI18nResourceContributor —— 其路径解析（PathUtils 会剥掉前导 '/'
        // 再与 BaseDirectory 拼接）在 Linux 上对绝对目录静默失效，且热重载语义会把结果交给文件事件时序。
        // 冻结层让兜底在所有平台、任何时序下行为一致：平台没有的 key 恒按注册时的本地文案解析。
        var localSnapshot = LoadLocalSnapshot(localDirectory, cultures);

        // 1) MASA I18n 基础设施。读侧不依赖它为 DefaultResource 产生的 contributor（第 3 步整表替换，
        //    兜底用上面的注册时冻结快照），所以 MASA 的 AddJson 在 Linux 上解析不了绝对路径也无影响。
        services.AddI18n(settings =>
        {
            // 必须传绝对路径：本地目录里没有任何 {culture}.json 时，MASA 的 CultureUtils 直接用这个路径读
            // supportedCultures.json（不拼 BaseDirectory），相对路径会按进程工作目录解析。
            settings.ResourcesDirectory = localDirectory
                ?? Path.Combine(I18nResourceResourceConfiguration.BaseDirectory, "i18n");
            settings.SupportedCultures = cultures.Select(c => new CultureModel(c)).ToList();
            cultureSettings?.Invoke(settings);
        });

        // 2) Lingua SDK。store 以现成实例注册：第 3 步的 contributor 需要直接引用它，
        //    AddLinguaLocalization 内部的 TryAdd 会跳过已注册的 store / notifier。
        var notifier = new LinguaChangeNotifier();
        // 先铺发布的兜底文件，再用上次同步的缓存覆盖同名语种（离线重启也是平台最新内容）。
        var store = new LinguaFrozenMemoryStore(notifier);
        if (localDirectory is not null)
        {
            store.LoadFromLocalDirectory(localDirectory);
        }

        if (syncCacheDirectory is not null)
        {
            store.LoadFromLocalDirectory(syncCacheDirectory);
        }

        services.TryAddSingleton<ILinguaChangeNotifier>(notifier);
        services.TryAddSingleton<ILinguaMemoryStore>(store);

        services.AddLinguaLocalization(o =>
        {
            configure(o);
            o.LocalJsonDirectory = syncCacheDirectory ?? string.Empty;
        });

        // 3) DefaultResource 的 contributor 替换为 Lingua(热更新)优先 + 本地 JSON 兜底的组合。
        //    MASA 的 I18nResource.AddContributor 对重复 culture 抛异常且没有移除 API，
        //    所以整表替换：BaseResourceTypes（含 [InheritResource] 继承的框架资源）原样保留。
        var originalResource = I18nResourceResourceConfiguration.Resources.GetOrNull<DefaultResource>();
        var contributorRegistry = new ContributorRegistry(
            originalResource?.BaseResourceTypes ?? Enumerable.Empty<Type>(),
            store, localSnapshot);

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

                // 本地兜底 = 注册时冻结的本地 {culture}.json（见 LoadLocalSnapshot）：
                // 跨平台确定，不依赖 MASA 的文件路径解析与热重载时序。
                II18nResourceContributor? fallback = localSnapshot.TryGetValue(culture, out var snapshotEntries)
                    ? new LocalSnapshotFallbackContributor(culture, snapshotEntries)
                    : null;

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
    /// 读不出（缺失 / 损坏）的语种跳过：快照只是兜底层，不挡任何其他层。
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
