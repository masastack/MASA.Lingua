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

    private readonly ConcurrentDictionary<(string Project, string Culture), CompiledBundle> _memoryCache = new();

    // manifest 中的语种完成度需要加载全部词条，按版本指纹缓存，同一版本只算一次
    private readonly ConcurrentDictionary<string, (string Version, IReadOnlyDictionary<string, (int Entries, int Completion)> Stats)> _manifestStats = new();

    // 发布与回滚串行执行，避免两次并发发布互相覆盖文件与缓存
    private readonly SemaphoreSlim _releaseLock = new(1, 1);

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
            .Where(s => project.SharedNamespaceCodes.Contains(s.Code))
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
                    var diff = published is null
                        ? BundleDiff.Compare(default, draft.NestedJsonUtf8Bytes.Span, hasPublished: false)
                        : BundleDiff.Compare(published.NestedJsonUtf8Bytes.Span, draft.NestedJsonUtf8Bytes.Span);
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
        var keys = new HashSet<string>(StringComparer.Ordinal);
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

    public Task<PublishSnapshot> PublishProjectAsync(
        string projectCode,
        string? releaseNote = null,
        CancellationToken ct = default) =>
        PublishProjectAsync(projectCode, new PublishRequest(releaseNote), ct);

    /// <exception cref="PublishBlockedException">未通过门禁，且没有强制发布（或强制发布没有填写原因）。</exception>
    public async Task<PublishSnapshot> PublishProjectAsync(
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

            var bundles = SnapshotContentCodec.Unpack(target.Content)
                .Select(kv => compiler.FromPublishedBytes(projectCode, kv.Key, kv.Value))
                .ToList();

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
        var projectStaticDir = Path.Combine(ResolveBundleDirectory(), projectCode);
        Directory.CreateDirectory(projectStaticDir);

        // 1. 原子写入固定名物理文件 App_Data/i18n/{project}/{culture}.json
        foreach (var bundle in bundles)
        {
            await WriteAtomicAsync(Path.Combine(projectStaticDir, $"{bundle.Culture}.json"), bundle.NestedJsonUtf8Bytes, ct);
        }

        var versionHash = BundleCompiler.ComputeVersionHash(bundles.Select(b => (b.Culture, b.ContentHash)));

        // 2. 全部语种写完后，一次性换入内存缓存（先覆盖再清理，读者不会撞上空窗期）
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

        var snapshot = new PublishSnapshot
        {
            ProjectId = project.Id,
            VersionHash = versionHash,
            ReleaseNote = releaseNote,
            PublishedAt = DateTimeOffset.UtcNow,
            EntryCount = bundles.Count == 0 ? 0 : bundles.Max(b => b.LookupEntries.Count),
            PublishedCultures = bundles.Select(b => b.Culture).ToList(),
            Kind = kind,
            RollbackOfSnapshotId = rollbackOf,
            Forced = forced,
            ForceReason = forceReason,
            GateSummary = gateSummary.Length > 1000 ? gateSummary[..1000] : gateSummary,
            Content = SnapshotContentCodec.Pack(bundles.Select(b => (b.Culture, b.NestedJsonUtf8Bytes)))
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

    private static async Task WriteAtomicAsync(string finalPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $"{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp");

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
            catch
            {
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
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

        var restored = 0;
        foreach (var culture in project.SupportedCultures)
        {
            if (await TryRestoreFromDiskAsync(projectCode, culture, ct) is not null)
            {
                restored++;
            }
        }

        if (restored > 0)
        {
            return false;
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
        var projectStaticDir = Path.Combine(ResolveBundleDirectory(), project.Code);
        Directory.CreateDirectory(projectStaticDir);

        foreach (var (culture, bytes) in SnapshotContentCodec.Unpack(snapshot.Content!))
        {
            var bundle = compiler.FromPublishedBytes(project.Code, culture, bytes);
            _memoryCache[(project.Code, culture)] = bundle;

            // 磁盘文件缺失或与线上版本不一致（例如容器重建、上次发布中途失败留下混合版本）时，用快照内容补回
            var path = Path.Combine(projectStaticDir, $"{culture}.json");
            if (!File.Exists(path) || !(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(bytes))
            {
                await WriteAtomicAsync(path, bytes, ct);
            }
        }
    }

    /// <summary>
    /// 读取「已发布」的语言包。只会返回发布过的内容（内存缓存、快照或已发布的静态文件），永远不会把未发布的草稿编译出来对外提供；
    /// 项目不存在、语种不在项目支持列表内、或该语种尚未发布时返回 null（调用方应回 404）。
    /// </summary>
    public async Task<CompiledBundle?> GetOrCompileBundleAsync(
        string projectCode,
        string culture,
        CancellationToken ct = default)
    {
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

        return await TryRestoreFromDiskAsync(projectCode, supported, ct);
    }

    private async Task<CompiledBundle?> TryRestoreFromDiskAsync(string projectCode, string culture, CancellationToken ct)
    {
        var path = Path.Combine(ResolveBundleDirectory(), projectCode, $"{culture}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var bundle = compiler.FromPublishedBytes(projectCode, culture, bytes);
            _memoryCache[(projectCode, culture)] = bundle;
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
        var projectDir = Path.Combine(ResolveBundleDirectory(), projectCode);
        foreach (var culture in cultures)
        {
            _memoryCache.TryRemove((projectCode, culture), out _);

            var path = Path.Combine(projectDir, $"{culture}.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "删除已下线语种的静态文件 {Path} 失败（文件被占用）；读取端仍会按 SupportedCultures 拦截，不影响对外正确性。", path);
            }
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
            return new ManifestCulture(
                b.Culture,
                LinguaCultures.GetName(b.Culture),
                $"/i18n/{projectCode}/{b.Culture}.json",
                b.ETag,
                s.Entries,
                s.Completion);
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
            .Where(s => project.SharedNamespaceCodes.Contains(s.Code))
            .Concat(project.Namespaces)
            .SelectMany(n => n.Entries)
            .ToList();

        var coverage = CoverageCalculator
            .Compute(entries, bundles.Select(b => b.Culture), project.DefaultCulture)
            .ToDictionary(c => c.Culture, StringComparer.OrdinalIgnoreCase);

        var stats = new Dictionary<string, (int Entries, int Completion)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bundles)
        {
            var count = JsonBundleConverter.ParseExistingJson(b.NestedJsonUtf8Bytes.Span, "").Count;
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
