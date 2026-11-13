using System.Collections.Concurrent;
using System.IO.Compression;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.Server.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MASA.Lingua.Server.Services;

/// <summary>一次发布请求。未通过门禁时必须 Force 且填写 ForceReason 才能发布（原因会写入快照留痕）。</summary>
public sealed record PublishRequest(string? ReleaseNote = null, bool Force = false, string? ForceReason = null);

/// <summary>草稿与线上已发布版本的差异（按「会上线」的口径比较）。</summary>
public sealed record DraftDiff(
    bool NeverPublished,
    int Added,
    int Changed,
    int Removed,
    int ChangedKeyCount,
    IReadOnlyDictionary<string, int> ChangesByCulture)
{
    public int Total => Added + Changed + Removed;
    public bool HasChanges => Total > 0;
}

/// <summary>发布页一次取齐的概览：发布前检查报告 + 草稿差异（同一次数据加载算出）。</summary>
public sealed record ReleaseOverview(PublishCheckReport Report, DraftDiff Diff);

/// <summary>
/// 发布器与 L3 预编译缓存服务：
/// 1. 发布：先过项目发布门禁（完成度 / Error 级问题 / 术语违规），再按发布策略过滤译文状态，
///    将项目的公共命名空间 + 私有命名空间 + 回退链合并，生成固定文件名的物理文件 App_Data/i18n/{project}/{culture}.json；
/// 2. 每次发布 / 回滚都会把各语种产物压缩存入快照；回滚 = 把历史快照重新发布为线上版本（不动草稿词条）；
/// 3. 采用临时文件 (.tmp) + File.Move 原子替换，防止并发读取到残缺 JSON；
/// 4. 发布与回滚完成后触发 SSE 广播，通知业务端重新拉取；
/// 5. 内存维护预编译字节缓存与 ETag，确保零序列化与极速 304 Not Modified 响应；对外只提供「已发布」内容，不会编译草稿。
/// </summary>
/// <remarks>
/// 构造要求三个主体接口（而不是仓储门面）：发布器是唯一同时「读项目数据/术语」与「写发布归档」的组件，
/// 三个参数把它的真实输入面写死在签名上，改一个主体的契约不会波及另外两个。
/// 词条数据通过 <see cref="IProjectCatalog.LoadPublishSourceAsync"/> 一次取齐，各语种图层在内存里构建，
/// 发布器不再需要词条主体接口。
/// </remarks>
public sealed class StaticBundlePublisher(
    IProjectCatalog projects,
    IGlossaryCatalog glossary,
    IPublishArchive archive,
    BundleCompiler compiler,
    LinguaSseBroadcaster sseBroadcaster,
    IWebHostEnvironment env,
    IConfiguration configuration,
    ILogger<StaticBundlePublisher> logger)
{
    /// <summary>发布产物目录的配置键（默认 {ContentRoot}/App_Data/i18n）。容器只读根部署时用它指到可写卷。</summary>
    public const string BundleDirectoryKey = "Lingua:BundleDirectory";

    // 线上版本的内存副本：(项目, 语种) → 预编译产物（嵌套 / 扁平 JSON 字节 + ETag）。
    // 每次业务端同步都会命中分发端点：有了它，热路径只是一次字典查找 + 一次 ETag 比较（命中 304 时连字节都不发），
    // 既不读盘、不解析 JSON、不重算哈希，也不访问数据库。它只是「已发布内容」的副本，不是数据源：
    // 权威来源是数据库里的发布快照，丢了（重启 / 驱逐）随时能从快照或磁盘文件补回。
    // 任何让「线上版本」失效的操作（发布 / 回滚 / 下线语种 / 删除项目）都必须同步更新这里，否则会继续对外吐旧内容。
    private readonly ConcurrentDictionary<(string Project, string Culture), CompiledBundle> _memoryCache = new();

    // manifest 中的语种完成度需要加载全部词条，按版本指纹缓存，同一版本只算一次
    private readonly ConcurrentDictionary<string, (string Version, IReadOnlyDictionary<string, (int Entries, int Completion)> Stats)> _manifestStats = new();

    // 发布与回滚串行执行，避免两次并发发布互相覆盖文件与缓存
    private readonly SemaphoreSlim _releaseLock = new(1, 1);

    // 清理世代：每次项目清理（PurgeProjectAsync）开始与结束各自增一次。
    // 冷路径（从磁盘把线上版本读回缓存）不在发布锁内：它在「读文件」之前记下世代，写入缓存之后复查，
    // 世代变了说明期间有项目被清理，就把刚写入的缓存项撤掉——否则读到一半的旧文件会在清理之后又把缓存填回去。
    private long _purgeEpoch;

    // ------------------------------------------------------------------
    // 发布前检查与草稿差异
    // ------------------------------------------------------------------

    /// <summary>发布前检查：按项目发布策略评估「即将上线」的内容。</summary>
    public async Task<PublishCheckReport> CheckProjectAsync(string projectCode, CancellationToken ct = default)
    {
        var project = await LoadProjectAsync(projectCode, ct);
        return await CheckAsync(project, ct);
    }

    /// <summary>
    /// 发布页一次取齐「发布前检查 + 草稿差异」：项目聚合、共享命名空间、术语各加载一次，
    /// 检查跑一次，逐语种用已加载数据构建图层并编译对比（等价于 CheckProjectAsync + GetDraftDiffAsync，但不再重复加载）。
    /// </summary>
    public async Task<ReleaseOverview> GetReleaseOverviewAsync(string projectCode, CancellationToken ct = default)
    {
        var project = await LoadProjectAsync(projectCode, ct);
        var shared = await projects.GetSharedNamespacesAsync(ct);
        var terms = await glossary.GetGlossaryTermsAsync(project.Id, ct);

        // 检查与差异互不依赖、都只读同一份对象图：检查放线程池与差异并行，整体耗时取两者较大者
        var checkTask = CheckOffThreadAsync(project, InheritedNamespaces(project, shared), terms, ct);
        var diff = await ComputeDraftDiffAsync(project, shared, ct);
        return new ReleaseOverview(await checkTask, diff);
    }

    private async Task<PublishCheckReport> CheckAsync(Project project, CancellationToken ct)
    {
        var shared = await projects.GetSharedNamespacesAsync(ct);
        var terms = await glossary.GetGlossaryTermsAsync(project.Id, ct);
        return await CheckOffThreadAsync(project, InheritedNamespaces(project, shared), terms, ct);
    }

    /// <summary>发布检查是对全部词条 × 语种的逐条校验（纯 CPU）：放线程池执行，不占住调用方（Blazor 电路）的同步上下文。</summary>
    private static Task<PublishCheckReport> CheckOffThreadAsync(
        Project project,
        IReadOnlyList<TranslationNamespace> namespaces,
        IReadOnlyList<GlossaryTerm> terms,
        CancellationToken ct) =>
        Task.Run(() => PublishChecker.Check(project, namespaces, terms), ct);

    /// <summary>项目「有效」的命名空间集合：引用的共享命名空间 + 项目私有命名空间。</summary>
    private static List<TranslationNamespace> InheritedNamespaces(Project project, IReadOnlyList<TranslationNamespace> shared) =>
        shared
            .Where(s => project.SharedNamespaceCodes.Contains(s.Code, LinguaKeys.Comparer))
            .Concat(project.Namespaces)
            .ToList();

    /// <summary>草稿比线上新多少改动：用「如果现在发布会上线的内容」与当前线上版本逐 key 对比。</summary>
    public async Task<DraftDiff> GetDraftDiffAsync(string projectCode, CancellationToken ct = default)
    {
        var project = await LoadProjectAsync(projectCode, ct);
        var shared = await projects.GetSharedNamespacesAsync(ct);
        return await ComputeDraftDiffAsync(project, shared, ct);
    }

    /// <summary>
    /// 草稿差异的唯一实现（<see cref="GetDraftDiffAsync"/> 与 <see cref="GetReleaseOverviewAsync"/> 共用）：
    /// 项目与共享命名空间由调用方加载一次，逐语种在内存里构建图层并编译，不再逐语种读库。
    /// </summary>
    private async Task<DraftDiff> ComputeDraftDiffAsync(
        Project project,
        IReadOnlyList<TranslationNamespace> shared,
        CancellationToken ct)
    {
        var projectCode = project.Code;
        var mode = project.PublishPolicy.Content;

        // 逐语种的「构建图层 + 编译 + 与线上对比」是纯 CPU 活，且互不依赖：
        // 放到线程池里并行跑（有限并发，避免多语种同时持有多份草稿 bundle），
        // 既不占住调用方（Blazor 电路）的同步上下文，也让多语种项目的耗时接近最慢那个语种。
        // 构建过程只读项目 / 共享命名空间对象图，调用方在等待期间不得修改它们。
        using var gate = new SemaphoreSlim(Math.Max(1, Math.Min(4, Environment.ProcessorCount)));
        var perCulture = await Task.WhenAll(project.SupportedCultures.Select(async culture =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await Task.Run(async () =>
                {
                    var layers = TranslationLayerBuilder.Build(project, shared, culture, mode);
                    var draft = compiler.Compile(projectCode, culture, layers);
                    var published = await GetOrCompileBundleAsync(projectCode, culture, ct).ConfigureAwait(false);
                    // 按扁平表示对比：嵌套 JSON 表达不了键路径冲突里被挤掉的一侧，只改这一侧时嵌套字节不变，
                    // 差异会漏报「草稿比线上新」；而 .NET 客户端消费的恰恰是扁平文件（发布指纹也同时覆盖两种字节）。
                    var diff = published is null
                        ? BundleDiff.Compare(default, draft.FlatJsonUtf8Bytes.Span, hasPublished: false)
                        : BundleDiff.Compare(published.FlatJsonUtf8Bytes.Span, draft.FlatJsonUtf8Bytes.Span);
                    return (Culture: culture, HasPublished: published is not null, Diff: diff);
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList()).ConfigureAwait(false);

        // 汇总按语种声明顺序（Task.WhenAll 保序），结果与串行实现逐项一致
        int added = 0, changed = 0, removed = 0;
        var keys = new HashSet<string>(LinguaKeys.Comparer);
        var byCulture = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var neverPublished = true;

        foreach (var (culture, hasPublished, diff) in perCulture)
        {
            if (hasPublished)
            {
                neverPublished = false;
            }

            added += diff.Added;
            changed += diff.Changed;
            removed += diff.Removed;
            keys.UnionWith(diff.ChangedKeys);

            if (diff.Total > 0)
            {
                byCulture[culture] = diff.Total;
            }
        }

        return new DraftDiff(neverPublished, added, changed, removed, keys.Count, byCulture);
    }

    // ------------------------------------------------------------------
    // 发布 / 回滚
    // ------------------------------------------------------------------

    public Task<PublishSnapshot?> PublishProjectAsync(
        string projectCode,
        string? releaseNote = null,
        CancellationToken ct = default) =>
        PublishProjectAsync(projectCode, new PublishRequest(releaseNote), ct);

    /// <summary>
    /// 发布草稿内容并返回新版本快照。
    /// 草稿编译产物与线上版本内容完全一致（版本指纹相同）时<b>不生成新版本</b>：返回 null 表示「没有新内容要发布」，
    /// 由调用方给出对应提示——重复发布会把发布历史灌满指纹相同的记录，也让「发布成功」的反馈失真。
    /// </summary>
    /// <exception cref="PublishBlockedException">未通过门禁，且没有强制发布（或强制发布没有填写原因）。</exception>
    public async Task<PublishSnapshot?> PublishProjectAsync(
        string projectCode,
        PublishRequest request,
        CancellationToken ct = default)
    {
        await _releaseLock.WaitAsync(ct);
        try
        {
            // 发布基于数据库当下的状态（不走读缓存）：项目树 + 共享命名空间 + 术语各读一次，
            // 门禁检查与各语种编译共用这同一份数据，不会出现「检查通过的是一份、上线的是另一份」。
            var source = await projects.LoadPublishSourceAsync(projectCode, ct)
                ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");
            var project = source.Project;
            var terms = await glossary.GetGlossaryTermsAsync(project.Id, ct);
            var report = await CheckOffThreadAsync(project, InheritedNamespaces(project, source.SharedNamespaces), terms, ct);

            var forced = false;
            if (!report.CanPublish)
            {
                if (!request.Force)
                {
                    throw new PublishBlockedException(report);
                }

                if (string.IsNullOrWhiteSpace(request.ForceReason))
                {
                    throw new PublishBlockedException(report, "强制发布必须填写原因，原因会记录在发布历史中。");
                }

                forced = true;
            }

            var bundles = new List<CompiledBundle>();
            foreach (var culture in project.SupportedCultures)
            {
                var layers = TranslationLayerBuilder.Build(
                    project, source.SharedNamespaces, culture, project.PublishPolicy.Content);
                bundles.Add(compiler.Compile(projectCode, culture, layers));
            }

            var versionHash = BundleCompiler.ComputeVersionHash(bundles.Select(b => (b.Culture, b.ContentHash)));

            // 草稿与线上内容完全一致（指纹逐字符相同）：不生成新版本，只把磁盘与内存按本次产物校准一遍
            // （文件丢失 / 缓存未热时顺手补回），不落快照、不广播、不写发布历史。
            // 强制发布同样跳过——没有内容差异可强发，强制原因记录了也只指向一个不存在的版本。
            if (string.Equals(project.LastPublishedVersionHash, versionHash, StringComparison.Ordinal))
            {
                await MaterializeLiveArtifactsAsync(project, bundles, ct);
                logger.LogInformation(
                    "项目 {ProjectCode} 草稿与线上内容一致（版本指纹 {VersionHash}），本次发布未生成新版本。",
                    projectCode, versionHash);
                return null;
            }

            return await ApplyReleaseAsync(
                project,
                bundles,
                string.IsNullOrWhiteSpace(request.ReleaseNote) ? "发布静态 {culture}.json 语言包" : request.ReleaseNote!,
                SnapshotKind.Publish,
                rollbackOf: null,
                forced,
                forced ? request.ForceReason!.Trim() : null,
                report.Summary,
                ct);
        }
        finally
        {
            _releaseLock.Release();
        }
    }

    /// <summary>
    /// 回滚：把历史快照里保存的产物重新发布为线上版本，照常写文件、触发 SSE 广播。
    /// 只回滚线上产物，不改动草稿词条（草稿与线上的差异会在页面上提示）。
    /// </summary>
    public async Task<PublishSnapshot> RollbackProjectAsync(
        string projectCode,
        Guid snapshotId,
        string? note = null,
        CancellationToken ct = default)
    {
        await _releaseLock.WaitAsync(ct);
        try
        {
            var project = await LoadProjectAsync(projectCode, ct);
            var target = await archive.GetPublishSnapshotAsync(projectCode, snapshotId, ct)
                ?? throw new InvalidOperationException("找不到要回滚到的发布记录。");

            if (target.Content is not { Length: > 0 })
            {
                throw new InvalidOperationException("该发布记录没有保存产物内容（早期版本或超出保留数量），无法回滚。");
            }

            var bundles = await RestoreBundlesAsync(project, target.Content, target.VersionHash, ct);

            // 回滚说明会落库并显示在发布历史里：时间戳固定按东八区格式化并标注时区。
            // ToLocalTime() 依赖运行环境的 TZ，容器里没有 tzdata 时等于原样输出 UTC，
            // 于是历史记录里的「回滚到 X 时」会比实际时间早 8 小时。
            var label = $"{target.PublishedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} (UTC+8) 的版本 {target.VersionHash}";
            return await ApplyReleaseAsync(
                project,
                bundles,
                string.IsNullOrWhiteSpace(note) ? $"回滚到 {label}" : note!,
                SnapshotKind.Rollback,
                rollbackOf: target.Id,
                forced: false,
                forceReason: null,
                gateSummary: $"回滚到 {label}",
                ct);
        }
        finally
        {
            _releaseLock.Release();
        }
    }

    private async Task<PublishSnapshot> ApplyReleaseAsync(
        Project project,
        IReadOnlyList<CompiledBundle> bundles,
        string releaseNote,
        SnapshotKind kind,
        Guid? rollbackOf,
        bool forced,
        string? forceReason,
        string gateSummary,
        CancellationToken ct)
    {
        var projectCode = project.Code;

        // 1. 物化产物：原子写固定名物理文件 + 整体换入内存缓存（发布 / 回滚 / 无变化发布共用）
        await MaterializeLiveArtifactsAsync(project, bundles, ct);

        var versionHash = BundleCompiler.ComputeVersionHash(bundles.Select(b => (b.Culture, b.ContentHash)));

        var snapshot = new PublishSnapshot
        {
            ProjectId = project.Id,
            VersionHash = versionHash,
            ReleaseNote = releaseNote,
            PublishedAt = DateTimeOffset.UtcNow,
            // 词条数 = 扁平发布文件的条数（客户端实际能取到的词条）；LookupEntries 为每条词条存了点 / 冒号 / 混合三种别名键，不能拿来计数
            EntryCount = bundles.Count == 0 ? 0 : bundles.Max(b => b.EntryCount),
            PublishedCultures = bundles.Select(b => b.Culture).ToList(),
            Kind = kind,
            RollbackOfSnapshotId = rollbackOf,
            Forced = forced,
            ForceReason = forceReason,
            GateSummary = gateSummary.Length > 1000 ? gateSummary[..1000] : gateSummary,
            // 嵌套 + 扁平两种产物都进快照：回滚 / 重启恢复靠它还原线上版本，缺了扁平就还原不出键路径冲突里被嵌套挤掉的一侧
            Content = SnapshotContentCodec.Pack(bundles.Select(b => (b.Culture, b.NestedJsonUtf8Bytes, b.FlatJsonUtf8Bytes)))
        };

        await archive.RecordPublishSnapshotAsync(snapshot, ct);

        var publishedEvent = new BundlePublishedEvent(
            projectCode,
            bundles.Select(b => new PublishedCulture(b.Culture, b.ETag)).ToList(),
            project.DefaultCulture,
            versionHash,
            snapshot.PublishedAt);

        // 3. SSE 广播通知在线的 ASP.NET / Blazor Server 业务端立即重新同步。
        //    事件带各语种 ETag，客户端据此只拉变化的语种、跳过一次 manifest 往返；
        //    事件没送到时由 SDK 的空闲对账兜底（它一定会在一个周期内发现新版本）。
        sseBroadcaster.Broadcast(publishedEvent);

        logger.LogInformation(
            "项目 {ProjectCode} 已{Kind}，版本指纹: {VersionHash}",
            projectCode, kind == SnapshotKind.Rollback ? "回滚" : "发布", versionHash);

        // 返回给调用方的快照不带大字段
        snapshot.Content = null;
        return snapshot;
    }

    /// <summary>
    /// 把编译产物物化为线上版本：原子写固定名物理文件 App_Data/i18n/{project}/{culture}.json，
    /// 全部语种写完后一次性换入内存缓存（先覆盖再清理下线语种，读者不会撞上空窗期）。
    /// 发布 / 回滚 / 「无变化发布」共用——无变化时也执行一遍，磁盘文件丢失或缓存未热可以顺手补回。
    /// </summary>
    private async Task MaterializeLiveArtifactsAsync(
        Project project,
        IReadOnlyList<CompiledBundle> bundles,
        CancellationToken ct)
    {
        var projectCode = project.Code;
        var projectStaticDir = ProjectStaticDirectory(ResolveBundleDirectory(), projectCode);
        Directory.CreateDirectory(projectStaticDir);

        foreach (var bundle in bundles)
        {
            await WriteAtomicAsync(Path.Combine(projectStaticDir, NestedFileName(bundle.Culture)), bundle.NestedJsonUtf8Bytes, ct);
            await WriteAtomicAsync(Path.Combine(projectStaticDir, FlatFileName(bundle.Culture)), bundle.FlatJsonUtf8Bytes, ct);
        }

        foreach (var bundle in bundles)
        {
            _memoryCache[(projectCode, bundle.Culture)] = bundle;
        }

        var live = bundles.Select(b => b.Culture).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _memoryCache.Keys.Where(k =>
                     string.Equals(k.Project, projectCode, StringComparison.Ordinal) && !live.Contains(k.Culture)).ToList())
        {
            _memoryCache.TryRemove(key, out _);
        }

        DeleteStaleCultureFiles(projectStaticDir, live);
    }

    /// <summary>
    /// 清掉项目目录里不属于本次线上版本的 {culture}.json（下线语种、旧版本遗留、项目被删后残留的孤儿文件）。
    /// 磁盘文件只用于重启恢复与一致性校准，留着「看起来还在线」的旧产物只会在日后某个语种被重新加入时被误读。
    /// 清理失败（文件被占用）只记警告：读取端按 SupportedCultures 与发布状态拦截，不影响对外正确性。
    /// </summary>
    private void DeleteStaleCultureFiles(string projectStaticDir, ISet<string> liveCultures)
    {
        foreach (var path in Directory.EnumerateFiles(projectStaticDir, "*.json"))
        {
            // {culture}.json（嵌套）与 {culture}.flat.json（扁平）同属一个语种：文件名去掉扩展名与 .flat 后缀才是语种代码
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(FlatFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^FlatFileSuffix.Length];
            }

            if (liveCultures.Contains(name))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "删除不属于线上版本的残留语言包文件 {Path} 失败，已忽略。", path);
            }
        }
    }

    /// <summary>扁平产物文件的后缀（<c>{culture}.flat.json</c>）：与嵌套产物 <c>{culture}.json</c> 并存于同一项目目录。</summary>
    private const string FlatFileSuffix = ".flat";

    private static string NestedFileName(string culture) => $"{culture}.json";

    private static string FlatFileName(string culture) => $"{culture}{FlatFileSuffix}.json";

    private static async Task WriteAtomicAsync(string finalPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $"{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp");

        // 写入本身也必须在 try 内：磁盘写满 / 写到一半被取消时，已经落下的半截临时文件同样要清掉，
        // 否则每次失败的发布都会往产物目录里多留一个 .tmp，把本来就满的磁盘越填越满。
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes.ToArray(), ct);

            // Windows 上目标文件正被读取 / 被另一个进程同时替换时 Move 会瞬时失败：短暂重试，最终失败再清理临时文件
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, finalPath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), ct);
                }
            }
        }
        finally
        {
            // Move 成功时临时文件已不存在；任何失败路径都在这里兜底删除
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // ------------------------------------------------------------------
    // 读取已发布内容（启动恢复 / 对外分发）
    // ------------------------------------------------------------------

    /// <summary>
    /// 启动时调用：恢复线上版本到内存缓存与静态文件（不产生新版本、不广播）。
    /// 恢复来源按优先级：最近一次带内容的发布快照（数据库是线上版本的权威来源，磁盘文件丢失也能恢复）→ 已发布的静态文件（旧版本升级）。
    /// 只有项目从未发布过时，才做一次引导发布。返回是否执行了引导发布。
    /// </summary>
    public async Task<bool> EnsurePublishedAsync(string projectCode, CancellationToken ct = default)
    {
        var project = await projects.GetProjectByCodeAsync(projectCode, ct);
        if (project is null)
        {
            return false;
        }

        var snapshot = await archive.GetLatestPublishSnapshotWithContentAsync(projectCode, ct);
        if (snapshot?.Content is { Length: > 0 })
        {
            try
            {
                await RestoreFromSnapshotAsync(project, snapshot, ct);
                return false;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "从发布快照 {SnapshotId} 恢复线上版本失败，尝试从静态文件恢复。", snapshot.Id);
            }
        }

        // 静态文件只在「数据库里确认发布过」时才可信（旧版本升级：有发布记录但还没有带内容的快照）。
        // 从未发布过的项目，磁盘上的 {culture}.json 一定是孤儿（同名项目被删后重建、上次发布中途失败的残留），
        // 把它当线上版本恢复，会让新项目在没发布过的情况下对外吐旧内容、还跳过引导发布。
        if (project.LastPublishedAt is not null)
        {
            var restored = new List<CompiledBundle>();
            var legacyDisk = false;
            foreach (var culture in project.SupportedCultures)
            {
                if (await TryRestoreFromDiskAsync(projectCode, culture, ct) is { } bundle)
                {
                    restored.Add(bundle);
                    // 没有 {culture}.flat.json 说明是旧版本写下的目录（只有嵌套文件）
                    legacyDisk |= !File.Exists(Path.Combine(
                        ProjectStaticDirectory(ResolveBundleDirectory(), projectCode), FlatFileName(culture)));
                }
            }

            if (restored.Count > 0)
            {
                if (legacyDisk && !string.IsNullOrEmpty(project.LastPublishedVersionHash))
                {
                    var reconciled = await ReconcileLegacyBundlesAsync(
                        project, restored, project.LastPublishedVersionHash, ct);
                    if (!ReferenceEquals(reconciled, restored))
                    {
                        await MaterializeLiveArtifactsAsync(project, reconciled, ct);
                    }
                }

                return false;
            }
        }

        // 项目首次启动：引导发布一次（草稿即初始内容，不走门禁）
        await PublishProjectAsync(
            projectCode,
            new PublishRequest("系统首次启动，引导发布初始语言包", Force: true, ForceReason: "系统首次启动引导发布"),
            ct);
        return true;
    }

    private async Task RestoreFromSnapshotAsync(Project project, PublishSnapshot snapshot, CancellationToken ct)
    {
        var projectStaticDir = ProjectStaticDirectory(ResolveBundleDirectory(), project.Code);
        Directory.CreateDirectory(projectStaticDir);

        var bundles = await RestoreBundlesAsync(project, snapshot.Content!, snapshot.VersionHash, ct);
        foreach (var bundle in bundles)
        {
            _memoryCache[(project.Code, bundle.Culture)] = bundle;

            // 磁盘文件缺失或与线上版本不一致（例如容器重建、上次发布中途失败留下混合版本）时，用快照内容补回
            await EnsureFileContentAsync(
                Path.Combine(projectStaticDir, NestedFileName(bundle.Culture)), bundle.NestedJsonUtf8Bytes, ct);
            await EnsureFileContentAsync(
                Path.Combine(projectStaticDir, FlatFileName(bundle.Culture)), bundle.FlatJsonUtf8Bytes, ct);
        }
    }

    private static async Task EnsureFileContentAsync(string path, ReadOnlyMemory<byte> expected, CancellationToken ct)
    {
        if (!File.Exists(path) || !(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(expected.Span))
        {
            await WriteAtomicAsync(path, expected, ct);
        }
    }

    /// <summary>
    /// 从发布快照还原各语种的线上产物（重启恢复与回滚共用）。
    /// 新格式快照同时保存了嵌套与扁平字节，原样还原，内容与 ETag 与发布时逐字节一致。
    /// 旧格式快照只有嵌套字节：扁平产物只能由嵌套内容重建，无键路径冲突的项目与发布时一致；
    /// 有冲突的项目，被嵌套挤掉的一侧找不回来，这时用「当前草稿重新编译、且指纹恰好等于该版本」做校准，
    /// 对不上就保留重建结果并记警告（下一次发布会按草稿写出完整产物）。
    /// </summary>
    private async Task<IReadOnlyList<CompiledBundle>> RestoreBundlesAsync(
        Project project,
        byte[] snapshotContent,
        string expectedVersionHash,
        CancellationToken ct)
    {
        var unpacked = SnapshotContentCodec.UnpackBundles(snapshotContent);
        var bundles = unpacked
            .Select(kv => kv.Value.Flat is { } flat
                ? compiler.FromPublishedBytes(project.Code, kv.Key, kv.Value.Nested, flat)
                : compiler.FromPublishedBytes(project.Code, kv.Key, kv.Value.Nested))
            .ToList();

        return unpacked.Values.Any(v => v.Flat is null)
            ? await ReconcileLegacyBundlesAsync(project, bundles, expectedVersionHash, ct)
            : bundles;
    }

    /// <summary>
    /// 旧格式产物（只有嵌套字节）重建出的扁平产物与发布时的版本指纹不一致时的校准：
    /// 用当前草稿重新编译，仅当草稿编译出的指纹恰好等于期望的版本指纹（说明草稿自那次发布以来没有变过）才采用；
    /// 否则原样返回重建结果（同一个列表实例，调用方据此判断是否发生了替换）。
    /// </summary>
    private async Task<IReadOnlyList<CompiledBundle>> ReconcileLegacyBundlesAsync(
        Project project,
        IReadOnlyList<CompiledBundle> restored,
        string expectedVersionHash,
        CancellationToken ct)
    {
        var restoredHash = BundleCompiler.ComputeVersionHash(restored.Select(b => (b.Culture, b.ContentHash)));
        if (string.Equals(restoredHash, expectedVersionHash, StringComparison.Ordinal))
        {
            return restored;
        }

        try
        {
            var source = await projects.LoadPublishSourceAsync(project.Code, ct);
            if (source is not null)
            {
                var compiled = restored
                    .Select(b => compiler.Compile(
                        project.Code,
                        b.Culture,
                        TranslationLayerBuilder.Build(
                            source.Project, source.SharedNamespaces, b.Culture, source.Project.PublishPolicy.Content)))
                    .ToList();
                var compiledHash = BundleCompiler.ComputeVersionHash(compiled.Select(b => (b.Culture, b.ContentHash)));
                if (string.Equals(compiledHash, expectedVersionHash, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "项目 {ProjectCode} 的旧格式发布产物只有嵌套 JSON，已按未变动的草稿重新编译还原线上版本 {VersionHash}。",
                        project.Code, expectedVersionHash);
                    return compiled;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "项目 {ProjectCode} 按草稿校准旧格式发布产物失败，改用嵌套 JSON 重建的结果。", project.Code);
        }

        logger.LogWarning(
            "项目 {ProjectCode} 的旧格式发布产物只有嵌套 JSON，重建出的扁平产物指纹 {Restored} 与线上版本 {Expected} 不一致" +
            "（项目存在键路径冲突，或草稿在该版本之后有改动）。扁平产物里可能缺少被嵌套 JSON 挤掉的词条，请重新发布一次以写出完整产物。",
            project.Code, restoredHash, expectedVersionHash);
        return restored;
    }

    /// <summary>
    /// 读取「已发布」的语言包。只会返回发布过的内容（内存缓存、快照或已发布的静态文件），永远不会把未发布的草稿编译出来对外提供；
    /// 项目不存在、编码不合法、语种不在项目支持列表内、项目从未发布过、或该语种尚未发布时返回 null（调用方应回 404）。
    /// </summary>
    public async Task<CompiledBundle?> GetOrCompileBundleAsync(
        string projectCode,
        string culture,
        CancellationToken ct = default)
    {
        // 编码不合法只可能是校验上线前入库的遗留项目：它不可能（也不允许）有发布产物。
        // 在这里直接按「未发布」处理 —— 路由参数来自公开 URL，放任它走到 ProjectStaticDirectory 会抛异常变成 500。
        // （IsValid 不 Trim：带首尾空白的路由参数同样按未发布处理，缓存键与磁盘路径始终是校验过的那个字符串。）
        if (!LinguaProjectCodes.IsValid(projectCode))
        {
            return null;
        }

        if (_memoryCache.TryGetValue((projectCode, culture), out var cached))
        {
            return cached;
        }

        // 缓存键只允许项目已声明的语种，任意 URL 参数都不能让缓存无限增长（只查轻量摘要，不加载词条）
        var project = await projects.GetProjectSummaryAsync(projectCode, ct);
        var supported = project?.SupportedCultures.FirstOrDefault(
            c => string.Equals(c, culture, StringComparison.OrdinalIgnoreCase));
        if (project is null || supported is null)
        {
            return null;
        }

        if (_memoryCache.TryGetValue((projectCode, supported), out cached))
        {
            return cached;
        }

        // 从未发布过的项目没有「线上版本」：磁盘上的文件只可能是孤儿（同名项目删除后重建、发布中途失败的残留），
        // 不能当作已发布内容对外提供，也不能让草稿差异拿它当对比基线。
        if (project.LastPublishedAt is null)
        {
            return null;
        }

        return await TryRestoreFromDiskAsync(projectCode, supported, ct);
    }

    private async Task<CompiledBundle?> TryRestoreFromDiskAsync(string projectCode, string culture, CancellationToken ct)
    {
        var projectDir = ProjectStaticDirectory(ResolveBundleDirectory(), projectCode);
        var path = Path.Combine(projectDir, NestedFileName(culture));
        if (!File.Exists(path))
        {
            return null;
        }

        // 先记世代再读文件（见 _purgeEpoch）：读文件期间若有项目清理，下面写入缓存后会发现世代变了并撤掉缓存项
        var epoch = Volatile.Read(ref _purgeEpoch);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);

            // 优先用 {culture}.flat.json 原样还原扁平产物；它缺失（旧版本写下的目录）或损坏时，退回由嵌套内容重建
            var flatPath = Path.Combine(projectDir, FlatFileName(culture));
            CompiledBundle bundle;
            if (File.Exists(flatPath))
            {
                try
                {
                    bundle = compiler.FromPublishedBytes(
                        projectCode, culture, bytes, await File.ReadAllBytesAsync(flatPath, ct));
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogWarning(ex, "已发布的扁平文件 {Path} 不是合法 JSON，改由嵌套文件重建扁平内容。", flatPath);
                    bundle = compiler.FromPublishedBytes(projectCode, culture, bytes);
                }
            }
            else
            {
                bundle = compiler.FromPublishedBytes(projectCode, culture, bytes);
            }

            var key = (projectCode, culture);
            _memoryCache[key] = bundle;
            if (Volatile.Read(ref _purgeEpoch) != epoch)
            {
                _memoryCache.TryRemove(key, out _);
            }

            return bundle;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "从已发布文件 {Path} 恢复语言包失败。", path);
            return null;
        }
    }

    /// <summary>
    /// 发布产物的物化目录。刻意不放在 wwwroot 下：
    /// 这些文件只通过分发端点对外（/i18n 已从静态文件中间件排除），放在站点资源根会让「反代加一条 alias」
    /// 就能绕过只读密钥直接下载，也会逼着容器把站点目录挂成可写。
    /// 它的职责只有重启恢复与磁盘一致性校准，线上版本的权威来源始终是数据库里的发布快照。
    /// </summary>
    private string ResolveBundleDirectory()
    {
        var configured = configuration[BundleDirectoryKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "i18n")
            : configured;
    }

    /// <summary>
    /// 校验后返回发布目录下该项目的子目录。编码直接进磁盘路径，创建入口（HTTP 端点 / 仓储）已统一校验；
    /// 这里再兜底一次，防的是**校验上线前**已入库的非法编码项目把文件读写到 i18n 目录之外（路径穿越）——
    /// 这类项目应删除后用合规编码重建，读写直接失败比落到目录外安全。
    /// </summary>
    private static string ProjectStaticDirectory(string bundleDirectory, string projectCode)
    {
        if (!LinguaProjectCodes.IsValid(projectCode))
        {
            throw new InvalidOperationException(
                $"项目编码「{projectCode}」含不安全字符，拒绝访问发布目录（请删除该非法项目后用合规编码重建）。");
        }

        return Path.Combine(bundleDirectory, projectCode);
    }

    // ------------------------------------------------------------------
    // 语种下线清理
    // ------------------------------------------------------------------

    /// <summary>
    /// 语种从项目的支持列表移除后，删除其已发布的静态 {culture}.json 并逐出内存缓存。
    /// manifest 与 bundle 读取本身就按 SupportedCultures 拦截（旧文件即便残留也不会对外服务），
    /// 这里只是把不再需要的文件与缓存清掉，避免磁盘上留下「看起来还在线」的旧产物。
    /// </summary>
    public void RemovePublishedCultures(string projectCode, IEnumerable<string> cultures)
    {
        // 编码不合法的遗留项目从未写过发布目录：只清内存即可，不能因为目录校验失败让「改语种」整个失败
        var projectDir = LinguaProjectCodes.IsValid(projectCode)
            ? ProjectStaticDirectory(ResolveBundleDirectory(), projectCode)
            : null;
        foreach (var culture in cultures)
        {
            _memoryCache.TryRemove((projectCode, culture), out _);

            if (projectDir is null)
            {
                continue;
            }

            foreach (var path in new[]
                     {
                         Path.Combine(projectDir, NestedFileName(culture)),
                         Path.Combine(projectDir, FlatFileName(culture))
                     })
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "删除已下线语种的静态文件 {Path} 失败（文件被占用）；读取端仍会按 SupportedCultures 拦截，不影响对外正确性。", path);
                }
            }
        }
    }

    /// <summary>
    /// 删除项目的唯一入口：先删库（含发布历史），成功后再清掉发布器里该项目的全部线上产物。
    /// <para>
    /// 「删库必须伴随清缓存 / 清产物」这条不变量收敛在这里：<c>IProjectCatalog.DeleteProjectAsync</c> 只管数据库，
    /// 直接调用它的任何入口（HTTP 端点、将来的管理后台删除）都会留下没有主人的线上版本，所以删除项目一律走本方法。
    /// </para>
    /// <para>
    /// 项目不存在返回 false（此时没有任何东西需要清理）。库里的删除一旦提交，清理就必须做完，因此不跟随请求取消。
    /// </para>
    /// </summary>
    public async Task<bool> DeleteProjectAsync(string projectCode, CancellationToken ct = default)
    {
        if (!await projects.DeleteProjectAsync(projectCode, ct))
        {
            return false;
        }

        // 客户端断开不该留下一个没有主人的线上版本
        await PurgeProjectAsync(projectCode, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// 项目被删除之后，清掉它在发布器里的全部线上产物：内存缓存、manifest 统计缓存、SSE 补发记录与磁盘目录 App_Data/i18n/{code}。
    /// <para>
    /// 必须在数据库删除成功之后调用（外部入口见 <see cref="DeleteProjectAsync"/>）。数据库里的发布快照随项目一起没了，这些副本就成了
    /// 「没有主人的线上版本」：内存缓存命中时分发端点不会再查库，项目删了照样返回 200；同名项目重建后，没发布过就能读到上一个项目的文案。
    /// </para>
    /// <para>
    /// 与发布 / 回滚共用同一把锁：正在进行的发布先收尾，再由本方法清掉它写下的文件与缓存。
    /// 冷路径读盘不在锁内，靠 <see cref="_purgeEpoch"/> 在清理前后各自增一次来撤掉「读到一半的旧文件填回的缓存项」。
    /// 进程内状态，单实例部署成立（见部署文档「副本数」）。
    /// </para>
    /// </summary>
    private async Task PurgeProjectAsync(string projectCode, CancellationToken ct = default)
    {
        await _releaseLock.WaitAsync(ct);
        try
        {
            Interlocked.Increment(ref _purgeEpoch);

            // 先删文件再清缓存：冷路径的并发读者会从磁盘把文件读回缓存，文件没了就不会把刚清掉的缓存又填回去
            if (LinguaProjectCodes.IsValid(projectCode))
            {
                var projectDir = ProjectStaticDirectory(ResolveBundleDirectory(), projectCode);
                try
                {
                    if (Directory.Exists(projectDir))
                    {
                        Directory.Delete(projectDir, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 删不掉（文件被占用）不影响对外正确性：项目没有发布记录，读取端与启动恢复都不会采信这些文件，
                    // 之后的首次发布会整体覆盖并清掉残留
                    logger.LogWarning(ex, "删除已删项目 {ProjectCode} 的发布目录 {Path} 失败，已忽略。", projectCode, projectDir);
                }
            }
            // 编码不合法的遗留项目从未写过发布目录：只清内存

            foreach (var key in _memoryCache.Keys
                         .Where(k => string.Equals(k.Project, projectCode, StringComparison.Ordinal)).ToList())
            {
                _memoryCache.TryRemove(key, out _);
            }

            _manifestStats.TryRemove(projectCode, out _);
            sseBroadcaster.Forget(projectCode);

            // 结束时再自增一次：开始自增之后才开始读盘的冷路径读者，也能在写入缓存后发现世代变了
            Interlocked.Increment(ref _purgeEpoch);
        }
        finally
        {
            _releaseLock.Release();
        }
    }

    private async Task<Project> LoadProjectAsync(string projectCode, CancellationToken ct) =>
        await projects.GetProjectByCodeAsync(projectCode, ct)
        ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");

    /// <summary>
    /// 导出项目所有语种的 {culture}.json 为 ZIP 压缩包字节流。
    /// 导出是管理员的显式操作，导出的是「当前编辑状态」（含未发布草稿），不影响线上已发布版本。
    /// </summary>
    public async Task<byte[]> ExportProjectZipAsync(
        string projectCode,
        bool flatFormat = false,
        CancellationToken ct = default)
    {
        // 导出同样基于数据库当下状态：读一次，逐语种在内存里构建图层
        var source = await projects.LoadPublishSourceAsync(projectCode, ct)
            ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");
        var project = source.Project;

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var culture in project.SupportedCultures)
            {
                var layers = TranslationLayerBuilder.Build(project, source.SharedNamespaces, culture);
                var bundle = compiler.Compile(projectCode, culture, layers);

                var entry = archive.CreateEntry($"{culture}.json", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                var bytes = flatFormat ? bundle.FlatJsonUtf8Bytes : bundle.NestedJsonUtf8Bytes;
                await entryStream.WriteAsync(bytes, ct);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// 项目发布清单（基于「已发布」内容）：版本指纹、各语种地址 / ETag / 条数 / 完成度。
    /// 项目不存在或还没有任何语种发布时返回 null。热路径只读内存缓存与轻量摘要，不加载词条。
    /// </summary>
    public async Task<ProjectManifest?> GetManifestAsync(string projectCode, CancellationToken ct = default)
    {
        var summary = await projects.GetProjectSummaryAsync(projectCode, ct);
        if (summary is null)
        {
            return null;
        }

        var bundles = new List<CompiledBundle>();
        foreach (var culture in summary.SupportedCultures)
        {
            if (await GetOrCompileBundleAsync(projectCode, culture, ct) is { } bundle)
            {
                bundles.Add(bundle);
            }
        }

        if (bundles.Count == 0)
        {
            return null;
        }

        var version = BundleCompiler.ComputeVersionHash(bundles.Select(b => (b.Culture, b.ContentHash)));
        var stats = await GetManifestStatsAsync(projectCode, version, bundles, ct);

        var cultures = bundles.Select(b =>
        {
            stats.TryGetValue(b.Culture, out var s);
            var url = $"/i18n/{projectCode}/{b.Culture}.json";
            return new ManifestCulture(
                b.Culture,
                LinguaCultures.GetName(b.Culture),
                url,
                b.ETag,
                s.Entries,
                s.Completion,
                $"{url}?format=flat");
        }).ToList();

        return new ProjectManifest(
            projectCode,
            summary.DefaultCulture,
            version,
            summary.LastPublishedAt,
            cultures,
            $"/api/v1/projects/{projectCode}/manifest",
            $"/api/v1/stream?project={Uri.EscapeDataString(projectCode)}");
    }

    private async Task<IReadOnlyDictionary<string, (int Entries, int Completion)>> GetManifestStatsAsync(
        string projectCode,
        string version,
        IReadOnlyList<CompiledBundle> bundles,
        CancellationToken ct)
    {
        if (_manifestStats.TryGetValue(projectCode, out var cached) && cached.Version == version)
        {
            return cached.Stats;
        }

        var project = await LoadProjectAsync(projectCode, ct);
        var shared = await projects.GetSharedNamespacesAsync(ct);
        var entries = shared
            .Where(s => project.SharedNamespaceCodes.Contains(s.Code, LinguaKeys.Comparer))
            .Concat(project.Namespaces)
            .SelectMany(n => n.Entries)
            .ToList();

        var coverage = CoverageCalculator
            .Compute(entries, bundles.Select(b => b.Culture), project.DefaultCulture)
            .ToDictionary(c => c.Culture, StringComparer.OrdinalIgnoreCase);

        var stats = new Dictionary<string, (int Entries, int Completion)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bundles)
        {
            // 以扁平产物的条数为准（客户端实际能取到的词条）；嵌套 JSON 里被键路径冲突挤掉的词条不会被漏数
            var count = b.EntryCount;
            var isSource = string.Equals(b.Culture, project.DefaultCulture, StringComparison.OrdinalIgnoreCase);
            var completion = isSource || !coverage.TryGetValue(b.Culture, out var c)
                ? 100
                : c.LivePercent(project.PublishPolicy.Content);
            stats[b.Culture] = (count, completion);
        }

        _manifestStats[projectCode] = (version, stats);
        return stats;
    }
}
