using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.Core.Validation;
using Microsoft.EntityFrameworkCore;

namespace MASA.Lingua.Storage.EfCore;

public sealed class EfCoreLinguaRepository(IDbContextFactory<LinguaDbContext> dbFactory) : ILinguaRepository
{
    // ------------------------------------------------------------------
    // 读缓存：项目概览 / 影响面 / 共享命名空间 / 全量项目 这些「元数据为主」的读路径
    // 在高延迟数据库链路（内网/VPN）下每个往返都要 20-50ms，重复读取（页面切换、多页共用）集中在这里缓存。
    // 仓储是单例：任何写路径在「提交成功之后」递增版本号让缓存立即失效；TTL 兜底多实例部署时其他副本的写入。
    //
    // 正确性约束（别破坏）：
    // 1. 读路径必须在「发起查询之前」捕获版本号并用它给结果打标：若在查询结束后才取版本号，
    //    一个在写之前开始、写之后结束的读请求会把旧数据盖上新版本号，陈旧数据最多留存一个 TTL。
    // 2. 失效必须发生在事务提交之后：事务内的 SaveChanges 对其他连接还不可见，
    //    此时失效会让并发读把旧数据以新版本号缓存起来（同上）。
    // 3. 缓存里的对象图是共享的只读快照：对外返回的一律是克隆（见 Clone* 方法），
    //    调用方（页面就地更新等）可以随意改返回值，不会污染缓存，也不会与其他电路互相踩踏。
    // 4. 多副本部署时，别的副本写入对本副本最多 <see cref="CacheTtl"/> 后才可见（管理后台读到的是最终一致的元数据），
    //    发布与导出不走这些缓存（见 LoadPublishSourceAsync），不受影响。
    // ------------------------------------------------------------------

    private sealed record DataCache<T>(int Version, DateTimeOffset At, IReadOnlyList<T> Data);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private int _dataVersion;
    private DataCache<ProjectOverview>? _overviewCache;
    private DataCache<ProjectImpact>? _impactCache;
    private DataCache<TranslationNamespace>? _sharedNamespaceCache;
    private DataCache<Project>? _allProjectsCache;

    /// <summary>任何写路径（词条/译文/项目配置/发布记录/术语）在提交成功后都要调用：让全部读缓存失效。</summary>
    private void InvalidateDataCache() => Interlocked.Increment(ref _dataVersion);

    /// <summary>读路径发起查询之前调用：返回此刻的数据版本，查询结果用它打标。</summary>
    private int CaptureDataVersion() => Volatile.Read(ref _dataVersion);

    private DataCache<T>? Fresh<T>(DataCache<T>? cache)
    {
        if (cache is null || cache.Version != Volatile.Read(ref _dataVersion) || DateTimeOffset.UtcNow - cache.At > CacheTtl)
        {
            return null;
        }

        return cache;
    }

    /// <summary>共享命名空间的浅克隆：新命名空间壳 + 新 Entries 列表（词条实例只读共享），调用方改列表不会碰缓存。</summary>
    private static List<TranslationNamespace> CloneNamespaces(IEnumerable<TranslationNamespace> source) =>
        source.Select(CloneNamespace).ToList();

    private static TranslationNamespace CloneNamespace(TranslationNamespace n) => new()
    {
        Id = n.Id,
        ProjectId = n.ProjectId,
        Code = n.Code,
        IsShared = n.IsShared,
        Entries = [.. n.Entries]
    };

    private static List<Project> CloneProjects(IEnumerable<Project> source) =>
        source.Select(p => new Project
        {
            Id = p.Id,
            Code = p.Code,
            WriteKey = p.WriteKey,
            DefaultCulture = p.DefaultCulture,
            SupportedCultures = [.. p.SupportedCultures],
            SharedNamespaceCodes = [.. p.SharedNamespaceCodes],
            LastPublishedAt = p.LastPublishedAt,
            LastPublishedVersionHash = p.LastPublishedVersionHash,
            PublishPolicy = p.PublishPolicy.Clone(),
            Namespaces = CloneNamespaces(p.Namespaces),
            GlossaryTerms = [.. p.GlossaryTerms]
        }).ToList();

    private static List<ProjectOverview> CloneOverviews(IEnumerable<ProjectOverview> source) =>
        source.Select(o => o with
        {
            SupportedCultures = [.. o.SupportedCultures],
            PublishPolicy = o.PublishPolicy.Clone()
        }).ToList();

    public async Task<IReadOnlyList<Project>> GetAllProjectsAsync(CancellationToken ct = default)
    {
        var data = await GetAllProjectsReadOnlyAsync(ct);
        return CloneProjects(data);
    }

    /// <summary>
    /// <see cref="IProjectCatalog.GetAllProjectsReadOnlyAsync"/> 的实现：与克隆版共用同一次查询与缓存，
    /// 只是命中缓存时直接交出共享快照（调用方承诺只读）。查询里的实体本就 AsNoTracking，
    /// 仓储自身不会改它们；写入路径提交成功后递增版本号让缓存整体失效，旧快照随之变成孤儿对象。
    /// </summary>
    public async Task<IReadOnlyList<Project>> GetAllProjectsReadOnlyAsync(CancellationToken ct = default)
    {
        if (Fresh(_allProjectsCache) is { } hit)
        {
            return hit.Data; // 免克隆：调用方承诺只读，改了会污染缓存
        }

        var version = CaptureDataVersion();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var data = await db.Projects
            .AsNoTracking()
            .Include(p => p.Namespaces)
                .ThenInclude(n => n.Entries)
                    .ThenInclude(e => e.Translations)
            .Include(p => p.GlossaryTerms)
            .OrderBy(p => p.Code)
            .AsSplitQuery()
            .ToListAsync(ct);
        _allProjectsCache = new DataCache<Project>(version, DateTimeOffset.UtcNow, data);
        return data;
    }

    public async Task<IReadOnlyList<ProjectOverview>> GetProjectOverviewsAsync(CancellationToken ct = default)
    {
        if (Fresh(_overviewCache) is { } overviewHit)
        {
            return CloneOverviews(overviewHit.Data);
        }

        var version = CaptureDataVersion();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var data = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new ProjectOverview(
                p.Code,
                p.DefaultCulture,
                p.SupportedCultures,
                p.PublishPolicy,
                p.LastPublishedAt,
                p.LastPublishedVersionHash))
            .ToListAsync(ct);
        _overviewCache = new DataCache<ProjectOverview>(version, DateTimeOffset.UtcNow, data);
        return CloneOverviews(data);
    }

    public async Task<IReadOnlyList<ProjectImpact>> GetProjectImpactSummariesAsync(CancellationToken ct = default)
    {
        if (Fresh(_impactCache) is { } impactHit)
        {
            return impactHit.Data;
        }

        var version = CaptureDataVersion();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 影响面只关心两件事：每个项目继承了哪些共享命名空间；项目自己的命名空间里有哪些词条与共享词条「同命名空间同 Key」（即覆盖）。
        // 第二件事用库内半连接（EXISTS）只取重叠的行——行数约等于覆盖数，而不是把所有项目的全部词条 Key 拉进内存；
        // 查询量与项目总词条数无关，只与共享词条规模和覆盖数有关。
        var projects = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new { p.Id, p.Code, p.SharedNamespaceCodes })
            .ToListAsync(ct);

        var overlaps = await db.TranslationEntries
            .AsNoTracking()
            .Where(e => !e.Namespace!.IsShared
                        && e.Namespace.ProjectId != null
                        && db.TranslationEntries.Any(s =>
                            s.Namespace!.IsShared && s.Namespace.Code == e.Namespace.Code && s.Key == e.Key))
            .Select(e => new { e.Namespace!.ProjectId, NamespaceCode = e.Namespace.Code, e.Key })
            .ToListAsync(ct);

        var namespacesByProject = overlaps
            .GroupBy(o => o.ProjectId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ImpactNamespace>)g
                    .GroupBy(o => o.NamespaceCode, StringComparer.Ordinal)
                    .Select(ns => new ImpactNamespace(
                        ns.Key,
                        false,
                        ns.Select(o => o.Key).ToHashSet(StringComparer.Ordinal)))
                    .ToList());

        var data = projects
            .Select(p => new ProjectImpact(
                p.Code,
                p.SharedNamespaceCodes,
                namespacesByProject.GetValueOrDefault(p.Id, [])))
            .ToList();
        _impactCache = new DataCache<ProjectImpact>(version, DateTimeOffset.UtcNow, data);
        return data;
    }

    public async Task<ProjectSummary?> GetProjectSummaryAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Projects
            .AsNoTracking()
            .Where(x => x.Code == projectCode)
            .Select(x => new { x.Code, x.DefaultCulture, x.SupportedCultures, x.LastPublishedAt, x.LastPublishedVersionHash })
            .FirstOrDefaultAsync(ct);

        return p is null
            ? null
            : new ProjectSummary(p.Code, p.DefaultCulture, p.SupportedCultures, p.LastPublishedAt, p.LastPublishedVersionHash);
    }

    public async Task<ProjectWriteAccess?> GetProjectWriteAccessAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Projects
            .AsNoTracking()
            .Where(x => x.Code == projectCode)
            .Select(x => new ProjectWriteAccess(x.Code, x.DefaultCulture, x.WriteKey))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> DeleteProjectAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct);
        if (entity is null)
        {
            return false;
        }

        // publish_snapshots 对 projects 是 RESTRICT：先清发布历史，再删项目；
        // 命名空间 / 词条 / 译文 / 术语由外键级联清除。两步放同一事务，避免「历史清了项目还在」。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.PublishSnapshots.Where(s => s.ProjectId == entity.Id).ExecuteDeleteAsync(ct);
        db.Projects.Remove(entity);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        InvalidateDataCache();
        return true;
    }

    public async Task<Project?> GetProjectByCodeAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 热路径（页面切换 / 发布检查）：只加载词条树；术语由调用方按需单独加载，少一个拆分查询往返。
        // 只读用途（上下文随即释放，没有人通过它保存），不跟踪：少一份快照与身份映射，大项目材料化更快、内存更省。
        // 导航属性（Entry.Namespace / Translation.Entry 等）由 Include 的结果修复，不依赖跟踪（有测试守住）。
        return await db.Projects
            .AsNoTracking()
            .Include(p => p.Namespaces)
                .ThenInclude(n => n.Entries)
                    .ThenInclude(e => e.Translations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Code == projectCode, ct);
    }

    public async Task<Project> CreateProjectAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var existing = await db.Projects
            .Include(p => p.Namespaces)
                .ThenInclude(n => n.Entries)
                    .ThenInclude(e => e.Translations)
            .Include(p => p.GlossaryTerms)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Code == projectCode, ct);
        if (existing is not null)
        {
            return existing;
        }

        // 实体默认值即骨架形状：DefaultCulture zh-CN、SupportedCultures zh-CN/en-US/ja-JP、
        // SharedNamespaceCodes ["common"]。项目元数据（名称、描述等）不在本地存储，一律以 PM 为准。
        // 写密钥随创建一并生成：缺词上报端点只接受携带密钥的请求，没有「密钥后补」的匿名窗口期。
        var project = new Project { Code = projectCode, WriteKey = WriteKeys.Generate() };
        db.Projects.Add(project);

        try
        {
            await db.SaveChangesAsync(ct);
            InvalidateDataCache();
        }
        catch (DbUpdateException)
        {
            // Code 唯一索引：两个会话同时点「创建」时后到者撞索引——开新上下文回读已有配置，
            // 不在本上下文重查：失败的 Added 实体还挂在 ChangeTracker 里，会污染回读结果
            await using var freshDb = await dbFactory.CreateDbContextAsync(ct);
            var raced = await freshDb.Projects
                .Include(p => p.Namespaces)
                    .ThenInclude(n => n.Entries)
                        .ThenInclude(e => e.Translations)
                .Include(p => p.GlossaryTerms)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Code == projectCode, ct);

            if (raced is null)
            {
                throw;
            }
            return raced;
        }

        return project;
    }

    public async Task<string?> RegenerateWriteKeyAsync(Guid projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return null;
        }

        project.WriteKey = WriteKeys.Generate();
        await db.SaveChangesAsync(ct);
        InvalidateDataCache();
        return project.WriteKey;
    }

    public async Task<IReadOnlyList<TranslationNamespace>> GetSharedNamespacesAsync(CancellationToken ct = default)
    {
        if (Fresh(_sharedNamespaceCache) is { } sharedHit)
        {
            return CloneNamespaces(sharedHit.Data);
        }

        var version = CaptureDataVersion();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var data = await db.TranslationNamespaces
            .Where(n => n.IsShared)
            .Include(n => n.Entries)
                .ThenInclude(e => e.Translations)
            .OrderBy(n => n.Code)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);
        _sharedNamespaceCache = new DataCache<TranslationNamespace>(version, DateTimeOffset.UtcNow, data);
        return CloneNamespaces(data);
    }

    public async Task<PublishSource?> LoadPublishSourceAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects
            .AsNoTracking()
            .Include(p => p.Namespaces)
                .ThenInclude(n => n.Entries)
                    .ThenInclude(e => e.Translations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Code == projectCode, ct);
        if (project is null)
        {
            return null;
        }

        var sharedCodes = project.SharedNamespaceCodes;
        var shared = sharedCodes.Count == 0
            ? []
            : await db.TranslationNamespaces
                .AsNoTracking()
                .Where(n => n.IsShared && sharedCodes.Contains(n.Code))
                .Include(n => n.Entries)
                    .ThenInclude(e => e.Translations)
                .OrderBy(n => n.Code)
                .AsSplitQuery()
                .ToListAsync(ct);

        return new PublishSource(project, shared);
    }

    public async Task UpdateProjectCulturesAsync(
        string projectCode,
        string defaultCulture,
        IReadOnlyList<string> cultures,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct)
            ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");

        // 语种代码规范化：en-us / zh_CN / ZH-cn 之类写法映射回标准写法；
        // 只接受项目已有语种或可选语种目录（常用清单 + ICU）内的代码，写错的代码直接报错而不是悄悄变成新语种。
        var normalized = new List<string>();
        foreach (var culture in cultures)
        {
            var code = LinguaCultures.Resolve(culture, project.SupportedCultures);
            if (!normalized.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(code);
            }
        }

        if (normalized.Count == 0)
        {
            throw new LinguaValidationException("语种列表不能为空：项目至少要保留一个语种。");
        }

        var resolvedDefault = normalized.FirstOrDefault(c => string.Equals(c, defaultCulture, StringComparison.OrdinalIgnoreCase))
            ?? throw new LinguaValidationException($"默认语种 \"{defaultCulture}\" 不在语种列表内，请先在列表中添加它。");

        project.SupportedCultures = normalized;
        project.DefaultCulture = resolvedDefault;
        await SaveAsync(db, ct);
    }

    public async Task<TranslationEntry> UpsertEntryAsync(
        string projectCode,
        string namespaceCode,
        string key,
        string sourceText,
        string? context,
        int? maxLength,
        IDictionary<string, string>? translations = null,
        bool isAutoReported = false,
        TranslationStatus? status = null,
        bool acknowledgeIssues = false,
        CancellationToken ct = default)
    {
        // 超深 key 会在发布期让嵌套 JSON 构建的递归爆栈（无法捕获的进程级死亡）：在写入入口拦住
        JsonBundleConverter.EnsureKeySegments(namespaceCode, key);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 这个方法内部有多次 SaveChanges（先建命名空间、再写词条与译文）：全部放进同一个事务，
        // 否则中途失败会留下「空壳命名空间」或「词条已建、译文没写」的半成品。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 只加载命名空间本身 + 目标这一条词条（带译文）：以前把整个命名空间的全部词条与译文都 Include 进来，
        // 命名空间有几千条词条时，每保存一格都要把它们全读一遍再丢掉（命名空间 → 词条 → 译文是 N+1 倍放大的数据量）。
        TranslationNamespace? ns;
        var namespaceCreated = false;
        Guid? scopeProjectId = null;
        var sourceCulture = LinguaCultures.DefaultSourceCulture;
        if (string.Equals(projectCode, "__shared__", StringComparison.OrdinalIgnoreCase))
        {
            ns = await db.TranslationNamespaces
                .FirstOrDefaultAsync(n => n.IsShared && n.Code == namespaceCode, ct);

            if (ns is null)
            {
                ns = new TranslationNamespace
                {
                    Code = namespaceCode,
                    IsShared = true
                };
                db.TranslationNamespaces.Add(ns);
                await SaveAsync(db, ct);
                namespaceCreated = true;
            }
        }
        else
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct)
                ?? throw new InvalidOperationException($"Project '{projectCode}' does not exist.");
            sourceCulture = project.DefaultCulture;
            scopeProjectId = project.Id;

            ns = await db.TranslationNamespaces
                .FirstOrDefaultAsync(n => n.ProjectId == project.Id && n.Code == namespaceCode, ct);

            if (ns is null)
            {
                ns = new TranslationNamespace
                {
                    ProjectId = project.Id,
                    Code = namespaceCode,
                    IsShared = false
                };
                db.TranslationNamespaces.Add(ns);
                await SaveAsync(db, ct);
                namespaceCreated = true;
            }
        }

        // 刚建的命名空间一定没有词条，省掉一次查询；否则按 (命名空间, Key) 唯一索引取这一条
        // （命名空间已被跟踪，EF 会把读到的词条的 Namespace 导航自动接上）
        var entry = namespaceCreated
            ? null
            : await db.TranslationEntries
                .Include(e => e.Translations)
                .FirstOrDefaultAsync(e => e.NamespaceId == ns.Id && e.Key == key, ct);
        var sourceChanged = false;
        if (entry is null)
        {
            entry = new TranslationEntry
            {
                NamespaceId = ns.Id,
                Namespace = ns,
                Key = key,
                SourceText = sourceText,
                Context = context,
                MaxLength = maxLength,
                IsAutoReported = isAutoReported
            };
            db.TranslationEntries.Add(entry);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                sourceChanged = !string.Equals(entry.SourceText, sourceText, StringComparison.Ordinal);
                entry.SourceText = sourceText;
            }
            if (context is not null)
            {
                entry.Context = context;
            }
            if (maxLength.HasValue)
            {
                entry.MaxLength = maxLength;
            }
        }

        IReadOnlyList<GlossaryTerm>? glossary = null;
        async Task<IReadOnlyList<GlossaryTerm>> LoadGlossaryAsync()
        {
            glossary ??= await LoadEffectiveGlossaryAsync(db, scopeProjectId, ct);
            return glossary;
        }

        await ApplyTranslationsAsync(
            entry, sourceCulture, sourceChanged, translations, status, acknowledgeIssues,
            LoadGlossaryAsync, val => db.TranslationValues.Add(val));

        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
        InvalidateDataCache();
        return entry;
    }

    /// <summary>
    /// 一次批量写入多条译文：同一个 DbContext、同一个事务。词条或命名空间不存在时按需创建，
    /// 每条译文过与单条 <see cref="UpsertEntryAsync"/> 完全相同的写入校验与状态降级逻辑。
    /// </summary>
    public async Task<IReadOnlyList<TranslationEntry>> UpsertTranslationsBatchAsync(
        string projectCode,
        IReadOnlyList<TranslationUpsertBatchItem> items,
        CancellationToken ct = default)
    {
        // 超深 key 会在发布期让嵌套 JSON 构建的递归爆栈：AI 写回 / 缺词上报共用此入口，逐条拦在事务之外
        foreach (var item in items)
        {
            JsonBundleConverter.EnsureKeySegments(item.NamespaceCode ?? "", item.Key);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        Guid? scopeProjectId = null;
        var sourceCulture = LinguaCultures.DefaultSourceCulture;
        var sharedScope = string.Equals(projectCode, "__shared__", StringComparison.OrdinalIgnoreCase);
        if (!sharedScope)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct)
                ?? throw new InvalidOperationException($"Project '{projectCode}' does not exist.");
            sourceCulture = project.DefaultCulture;
            scopeProjectId = project.Id;
        }

        // 每个命名空间只查一次命名空间本身，再只取这批涉及的词条（按 Key 过滤，带译文）；
        // 不存在则按需创建，稍后统一 SaveChanges。不再把整个命名空间的全部词条都读进来。
        var namespaces = new Dictionary<string, TranslationNamespace>(StringComparer.Ordinal);
        var entriesByNamespace = new Dictionary<string, Dictionary<string, TranslationEntry>>(StringComparer.Ordinal);
        foreach (var group in items.GroupBy(i => i.NamespaceCode ?? "", StringComparer.Ordinal))
        {
            var code = group.Key;
            var ns = sharedScope
                ? await db.TranslationNamespaces
                    .FirstOrDefaultAsync(n => n.IsShared && n.Code == code, ct)
                : await db.TranslationNamespaces
                    .FirstOrDefaultAsync(n => n.ProjectId == scopeProjectId && n.Code == code, ct);

            var existing = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
            if (ns is null)
            {
                ns = new TranslationNamespace
                {
                    ProjectId = sharedScope ? null : scopeProjectId,
                    Code = code,
                    IsShared = sharedScope
                };
                db.TranslationNamespaces.Add(ns);
            }
            else
            {
                var namespaceId = ns.Id;
                var wantedKeys = group.Select(i => i.Key).Distinct(StringComparer.Ordinal).ToList();
                var loaded = await db.TranslationEntries
                    .Include(e => e.Translations)
                    .Where(e => e.NamespaceId == namespaceId && wantedKeys.Contains(e.Key))
                    .ToListAsync(ct);
                foreach (var e in loaded)
                {
                    existing[e.Key] = e;
                }
            }

            namespaces[code] = ns;
            entriesByNamespace[code] = existing;
        }

        IReadOnlyList<GlossaryTerm>? glossary = null;
        async Task<IReadOnlyList<GlossaryTerm>> LoadGlossaryAsync()
        {
            glossary ??= await LoadEffectiveGlossaryAsync(db, scopeProjectId, ct);
            return glossary;
        }

        var saved = new List<TranslationEntry>(items.Count);
        foreach (var item in items)
        {
            var nsCode = item.NamespaceCode ?? "";
            var ns = namespaces[nsCode];
            var known = entriesByNamespace[nsCode];
            var sourceChanged = false;
            if (!known.TryGetValue(item.Key, out var entry))
            {
                entry = new TranslationEntry
                {
                    NamespaceId = ns.Id,
                    Namespace = ns,
                    Key = item.Key,
                    SourceText = item.SourceText,
                    Context = item.Context,
                    MaxLength = item.MaxLength
                };
                db.TranslationEntries.Add(entry);
                ns.Entries.Add(entry);
                known[item.Key] = entry; // 同一批里重复出现的 Key 落在同一条词条上
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(item.SourceText))
                {
                    sourceChanged = !string.Equals(entry.SourceText, item.SourceText, StringComparison.Ordinal);
                    entry.SourceText = item.SourceText;
                }
                if (item.Context is not null)
                {
                    entry.Context = item.Context;
                }
                if (item.MaxLength.HasValue)
                {
                    entry.MaxLength = item.MaxLength;
                }
            }

            await ApplyTranslationsAsync(
                entry, sourceCulture, sourceChanged, item.Translations, item.Status, item.AcknowledgeIssues,
                LoadGlossaryAsync, val => db.TranslationValues.Add(val));
            saved.Add(entry);
        }

        await SaveAsync(db, ct);
        await transaction.CommitAsync(ct);
        InvalidateDataCache();
        return saved;
    }

    /// <summary>
    /// 把一批译文写入词条——单条 <see cref="UpsertEntryAsync"/> 与批量 <see cref="UpsertTranslationsBatchAsync"/> 共用的核心逻辑。
    /// 统一的写入校验（手工编辑 / AI / API 共用）：要以「已审核 / AI 已译」入库的译文，
    /// 违反占位符 / 长度 / HTML 规则或术语库（含禁用译法）时只能进「待复核」并写明原因；
    /// <paramref name="acknowledgeIssues"/> = true 表示人工已确认（例如术语误报），按请求的状态入库。
    /// 只修改跟踪实体，不提交——由调用方在同一事务里统一 SaveChanges。
    /// </summary>
    private async Task ApplyTranslationsAsync(
        TranslationEntry entry,
        string sourceCulture,
        bool sourceChanged,
        IEnumerable<KeyValuePair<string, string>>? translations,
        TranslationStatus? status,
        bool acknowledgeIssues,
        Func<Task<IReadOnlyList<GlossaryTerm>>> loadGlossary,
        Action<TranslationValue> addValue)
    {
        var currentHash = SourceTextHasher.Compute(entry.SourceText);
        var providedCultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (translations is not null)
        {
            // 空源文案 = 「源未知」。以前用 SourceText == Key 当哨兵，真实源文案恰好等于 key 时
            // 会被误判成未知而跳过全部校验，脏译文就这样上了线。
            var sourceKnown = !string.IsNullOrWhiteSpace(entry.SourceText);

            foreach (var (culture, text) in translations)
            {
                providedCultures.Add(culture);
                var existingVal = entry.Translations.FirstOrDefault(
                    t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));

                var intended = status ?? TranslationStatus.Approved;
                string? reviewComment = null;
                if (!acknowledgeIssues &&
                    intended is TranslationStatus.Approved or TranslationStatus.AiTranslated &&
                    sourceKnown &&
                    !string.IsNullOrWhiteSpace(text) &&
                    !string.Equals(culture, sourceCulture, StringComparison.OrdinalIgnoreCase))
                {
                    var glossary = await loadGlossary();
                    var blocking = Validator.ValidateEntry(entry, culture, text, glossary).Where(i => i.IsBlocking).ToList();
                    if (blocking.Count > 0)
                    {
                        intended = TranslationStatus.NeedsReview;
                        var summary = string.Join("；", blocking.Select(i => i.Message));
                        reviewComment = summary.Length > 500 ? summary[..500] : summary;
                    }
                }

                if (existingVal is null)
                {
                    var val = new TranslationValue
                    {
                        EntryId = entry.Id,
                        Culture = culture,
                        Text = text,
                        Status = intended,
                        ReviewComment = reviewComment,
                        SourceTextHash = currentHash
                    };
                    entry.Translations.Add(val);
                    addValue(val);
                }
                else
                {
                    existingVal.Text = text;
                    if (status.HasValue || intended == TranslationStatus.NeedsReview && reviewComment is not null)
                    {
                        existingVal.Status = intended;
                    }
                    existingVal.SourceTextHash = currentHash;
                    existingVal.ReviewComment = reviewComment;
                }
            }
        }

        if (sourceChanged)
        {
            MarkStaleTranslations(entry, sourceCulture, currentHash, providedCultures);
        }
    }

    public async Task<bool> DeleteEntryAsync(
        string projectCode,
        string namespaceCode,
        string key,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        TranslationNamespace? ns;
        if (string.Equals(projectCode, "__shared__", StringComparison.OrdinalIgnoreCase))
        {
            ns = await db.TranslationNamespaces
                .FirstOrDefaultAsync(n => n.IsShared && n.Code == namespaceCode, ct);
        }
        else
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct);
            if (project is null)
            {
                return false;
            }
            ns = await db.TranslationNamespaces
                .FirstOrDefaultAsync(n => n.ProjectId == project.Id && n.Code == namespaceCode, ct);
        }

        if (ns is null)
        {
            return false;
        }

        var entry = await db.TranslationEntries
            .FirstOrDefaultAsync(e => e.NamespaceId == ns.Id && e.Key == key, ct);
        if (entry is null)
        {
            return false;
        }

        // 译文对词条配置了级联删除（TranslationValueConfiguration），删词条即清掉全部语种译文
        db.TranslationEntries.Remove(entry);
        await SaveAsync(db, ct);

        // 词条删空后移除空壳命名空间，避免命名空间下拉框里积攒空项；下次写入会按需重建
        if (!await db.TranslationEntries.AnyAsync(e => e.NamespaceId == ns.Id, ct))
        {
            db.TranslationNamespaces.Remove(ns);
            await SaveAsync(db, ct);
        }

        return true;
    }

    /// <summary>
    /// 源文案变更后（对应 gettext msgmerge 的 fuzzy）：其他语种里「已审核 / AI 已译」且不是基于新源文案的译文，
    /// 一律置为「待复核」，避免过时译文静悄悄地继续上线，也不会被 AI 批量补全当成已完成而跳过。
    /// </summary>
    private static int MarkStaleTranslations(
        TranslationEntry entry,
        string sourceCulture,
        string currentHash,
        ISet<string>? skipCultures = null)
    {
        var marked = 0;
        foreach (var tr in entry.Translations)
        {
            if (string.Equals(tr.Culture, sourceCulture, StringComparison.OrdinalIgnoreCase) ||
                (skipCultures?.Contains(tr.Culture) ?? false) ||
                tr.SourceTextHash == currentHash ||
                tr.Status is not (TranslationStatus.Approved or TranslationStatus.AiTranslated))
            {
                continue;
            }

            tr.Status = TranslationStatus.NeedsReview;
            tr.ReviewComment = StaleSourceComment;
            marked++;
        }

        return marked;
    }

    /// <summary>源文案变更导致译文被置为待复核时写入的备注。</summary>
    public const string StaleSourceComment = "源文案已变更，请重新确认译文";

    public async Task<JsonImportResult> ImportJsonBundleAsync(
        string projectCode,
        string culture,
        ReadOnlyMemory<byte> utf8Json,
        string defaultNamespace = "",
        CancellationToken ct = default)
    {
        var parsedItems = JsonBundleConverter.ParseExistingJson(utf8Json.Span, defaultNamespace);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct)
            ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");

        // 语种必须是项目已有语种或可选语种目录内的代码；en-us / zh_CN 会被规范为已有的 en-US / zh-CN，而不是悄悄新增语种
        culture = LinguaCultures.Resolve(culture, project.SupportedCultures);
        if (!project.SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
        {
            project.SupportedCultures.Add(culture);
        }

        var glossaryRows = await db.GlossaryTerms
            .Where(g => g.ProjectId == null || g.ProjectId == project.Id)
            .ToListAsync(ct);
        var glossary = EffectiveGlossary(glossaryRows);

        bool isDefaultCulture = string.Equals(project.DefaultCulture, culture, StringComparison.OrdinalIgnoreCase);
        int addedEntries = 0;
        int updatedTranslations = 0;
        int flaggedIssues = 0;
        int staleMarked = 0;
        var affectedNamespaces = new HashSet<string>(StringComparer.Ordinal);

        // 只加载本批涉及的命名空间与词条（按编码 / Key 过滤），建字典索引后逐条 O(1) 命中。
        // 以前把整个项目的词条树（全部命名空间 × 词条 × 译文）带跟踪读进内存，再对每条导入做两次线性扫描
        // —— 导入几千条 key 时查询量与内存随「项目规模 × 导入规模」相乘放大，是大 JSON 导入时
        // 内存尖峰与 CPU 打满的元凶。查询模式与 UpsertTranslationsBatchAsync 一致（已验证的按需加载）。
        var nsCodes = parsedItems.Select(i => i.Namespace).Distinct(StringComparer.Ordinal).ToList();
        var namespaces = (await db.TranslationNamespaces
                .Where(n => n.ProjectId == project.Id && nsCodes.Contains(n.Code))
                .ToListAsync(ct))
            .ToDictionary(n => n.Code, StringComparer.Ordinal);

        var nsById = namespaces.Values.ToDictionary(n => n.Id);
        var entriesByNs = new Dictionary<string, Dictionary<string, TranslationEntry>>(StringComparer.Ordinal);
        if (namespaces.Count > 0)
        {
            var namespaceIds = namespaces.Values.Select(n => n.Id).ToList();
            var keys = parsedItems.Select(i => i.Key).Distinct(StringComparer.Ordinal).ToList();
            var loaded = await db.TranslationEntries
                .Include(e => e.Translations)
                .Where(e => namespaceIds.Contains(e.NamespaceId) && keys.Contains(e.Key))
                .ToListAsync(ct);
            foreach (var e in loaded)
            {
                // 命名空间本体已被跟踪，EF 关系修正会把 e.Namespace 接上；这里只按编码归组
                var code = nsById[e.NamespaceId].Code;
                if (!entriesByNs.TryGetValue(code, out var dict))
                {
                    dict = entriesByNs[code] = new(StringComparer.Ordinal);
                }

                dict[e.Key] = e;
            }
        }

        foreach (var (nsCode, key, val) in parsedItems)
        {
            affectedNamespaces.Add(nsCode);
            if (!namespaces.TryGetValue(nsCode, out var ns))
            {
                ns = new TranslationNamespace
                {
                    ProjectId = project.Id,
                    Code = nsCode,
                    IsShared = false
                };
                namespaces[nsCode] = ns;
                db.TranslationNamespaces.Add(ns);
            }

            if (!entriesByNs.TryGetValue(nsCode, out var known))
            {
                known = entriesByNs[nsCode] = new(StringComparer.Ordinal);
            }

            if (!known.TryGetValue(key, out var entry))
            {
                entry = new TranslationEntry
                {
                    NamespaceId = ns.Id,
                    Namespace = ns,
                    Key = key,
                    // 只导入非默认语种时源文案未知：留空，而不是把 key 当成源文案
                    SourceText = isDefaultCulture ? val : string.Empty
                };
                ns.Entries.Add(entry);
                db.TranslationEntries.Add(entry);
                known[key] = entry; // 同一批里重复出现的 Key 落在同一条词条上（与老实现一致）
                addedEntries++;
            }
            else if (isDefaultCulture && !string.IsNullOrWhiteSpace(val))
            {
                var changed = !string.Equals(entry.SourceText, val, StringComparison.Ordinal);
                entry.SourceText = val;
                if (changed)
                {
                    staleMarked += MarkStaleTranslations(
                        entry, project.DefaultCulture, SourceTextHasher.Compute(val),
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { culture });
                }
            }

            var currentHash = SourceTextHasher.Compute(entry.SourceText);

            // 导入按「已审核」入库：存量文案默认可信（单人模式，发布即审核），规则/术语校验只做统计提示、不降级状态
            // ——占位符 / 长度 / HTML / 术语违规由发布门禁把关（默认策略下阻断发布，强制发布需填原因）。
            // 只有源文案真实可知（默认语种本身，或词条已有真实源文案）时才能校验。
            var sourceKnown = !string.IsNullOrWhiteSpace(entry.SourceText);
            if (!isDefaultCulture && sourceKnown &&
                Validator.ValidateEntry(entry, culture, val, glossary).Any(i => i.IsBlocking))
            {
                flaggedIssues++;
            }

            var tr = entry.Translations.FirstOrDefault(t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));
            if (tr is null)
            {
                tr = new TranslationValue
                {
                    EntryId = entry.Id,
                    Culture = culture,
                    Text = val,
                    Status = TranslationStatus.Approved,
                    SourceTextHash = currentHash
                };
                entry.Translations.Add(tr);
                db.TranslationValues.Add(tr);
                updatedTranslations++;
            }
            else
            {
                // 文本未变也要更新状态 / 备注 / hash：重新导入同一份 JSON 即可把存量「待复核」批量恢复为已审核，
                // 并清掉「源文案已变更」标记（hash 与当前源文案对齐后 IsStaleFor 不再命中）。
                // 「更新译文」计数仍只统计文本真实变化的行，让结果提示保持诚实。
                var textChanged = !string.Equals(tr.Text, val, StringComparison.Ordinal);
                tr.Text = val;
                tr.Status = TranslationStatus.Approved;
                tr.ReviewComment = null;
                tr.SourceTextHash = currentHash;
                if (textChanged)
                {
                    updatedTranslations++;
                }
            }
        }

        await SaveAsync(db, ct);

        return new JsonImportResult(
            projectCode,
            culture,
            parsedItems.Count,
            addedEntries,
            updatedTranslations,
            affectedNamespaces.ToList(),
            flaggedIssues,
            staleMarked);
    }

    private static readonly DeterministicTranslationValidator Validator = new();

    private static async Task<List<GlossaryTerm>> LoadEffectiveGlossaryAsync(LinguaDbContext db, Guid? projectId, CancellationToken ct)
    {
        var rows = await db.GlossaryTerms
            .AsNoTracking()
            .Where(g => g.ProjectId == null || g.ProjectId == projectId)
            .ToListAsync(ct);
        return EffectiveGlossary(rows);
    }

    public async Task<IReadOnlyList<GlossaryTerm>?> GetEffectiveGlossaryByProjectCodeAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectId = await db.Projects
            .AsNoTracking()
            .Where(p => p.Code == projectCode)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);
        return projectId is null ? null : await LoadEffectiveGlossaryAsync(db, projectId, ct);
    }

    /// <summary>全局术语 + 项目术语合并，同名（忽略大小写）时项目术语完整覆盖全局术语。</summary>
    private static List<GlossaryTerm> EffectiveGlossary(IEnumerable<GlossaryTerm> rows)
    {
        var list = rows.ToList();
        var overridden = list
            .Where(g => g.ProjectId is not null)
            .Select(g => g.SourceTerm.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return list
            .Where(g => g.ProjectId is not null || !overridden.Contains(g.SourceTerm.Trim()))
            .OrderBy(g => g.SourceTerm, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> RecordMissingKeysAsync(MissingKeyBatchReport report, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 缺词上报是高频小批量写：只取项目头 + 本批涉及的命名空间 + 本批涉及的 Key，
        // 不再把整个项目的全部命名空间/词条/译文读进内存。
        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Code == report.ProjectCode)
            .Select(p => new { p.Id, p.DefaultCulture })
            .FirstOrDefaultAsync(ct);

        if (project is null)
        {
            return 0;
        }

        var items = report.Items
            .Select(i => (Code: string.IsNullOrWhiteSpace(i.Namespace) ? "" : i.Namespace, Item: i)) // 空 = 项目根
            .ToList();
        if (items.Count == 0)
        {
            return 0;
        }

        // 超深 key 会在发布期让嵌套 JSON 构建的递归爆栈：缺词上报来自外部 SDK，必须在入口拦住
        foreach (var (code, item) in items)
        {
            JsonBundleConverter.EnsureKeySegments(code, item.Key);
        }

        var projectId = project.Id;
        var codes = items.Select(i => i.Code).Distinct(StringComparer.Ordinal).ToList();
        var keys = items.Select(i => i.Item.Key).Distinct(StringComparer.Ordinal).ToList();

        var namespaces = (await db.TranslationNamespaces
                .Where(n => n.ProjectId == projectId && codes.Contains(n.Code))
                .ToListAsync(ct))
            .ToDictionary(n => n.Code, StringComparer.Ordinal);

        // 已存在的 (命名空间, Key)；本批内重复出现的 Key 也靠它去重
        var known = new HashSet<(string Code, string Key)>();
        if (namespaces.Count > 0)
        {
            var namespaceIds = namespaces.Values.Select(n => n.Id).ToList();
            var existing = await db.TranslationEntries
                .AsNoTracking()
                .Where(e => namespaceIds.Contains(e.NamespaceId) && keys.Contains(e.Key))
                .Select(e => new { e.Namespace!.Code, e.Key })
                .ToListAsync(ct);
            foreach (var e in existing)
            {
                known.Add((e.Code, e.Key));
            }
        }

        int createdCount = 0;
        foreach (var (nsCode, item) in items)
        {
            if (!namespaces.TryGetValue(nsCode, out var ns))
            {
                ns = new TranslationNamespace
                {
                    ProjectId = projectId,
                    Code = nsCode,
                    IsShared = false
                };
                namespaces[nsCode] = ns;
                db.TranslationNamespaces.Add(ns);
            }

            if (!known.Add((nsCode, item.Key)))
            {
                continue;
            }

            var sourceText = string.IsNullOrWhiteSpace(item.DefaultText) ? string.Empty : item.DefaultText!;
            var entry = new TranslationEntry
            {
                NamespaceId = ns.Id,
                Namespace = ns,
                Key = item.Key,
                SourceText = sourceText,
                Context = item.Context ?? "由客户端开发环境自动上报的缺失词条",
                IsAutoReported = true
            };

            entry.Translations.Add(new TranslationValue
            {
                EntryId = entry.Id,
                Culture = project.DefaultCulture,
                Text = sourceText,
                Status = TranslationStatus.Draft,
                SourceTextHash = SourceTextHasher.Compute(sourceText)
            });

            ns.Entries.Add(entry);
            db.TranslationEntries.Add(entry);
            createdCount++;
        }

        if (createdCount > 0)
        {
            await SaveAsync(db, ct);
        }

        return createdCount;
    }

    public async Task<GlossaryTerm> SaveGlossaryTermAsync(GlossaryTerm term, CancellationToken ct = default)
    {
        var sourceTerm = term.SourceTerm?.Trim();
        if (string.IsNullOrEmpty(sourceTerm))
        {
            throw new GlossaryValidationException("源术语不能为空。");
        }

        // 规范化目标译法 / 可接受变体 / 禁用译法：去空白、去空值、去重；DNT 术语不保留任何目标侧约束
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var forbidden = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!term.DoNotTranslate)
        {
            foreach (var (culture, text) in term.TargetTranslations)
            {
                if (!string.IsNullOrWhiteSpace(culture) && !string.IsNullOrWhiteSpace(text))
                {
                    targets[culture.Trim()] = text.Trim();
                }
            }

            variants = CleanWordLists(term.AcceptedVariants);
            forbidden = CleanWordLists(term.ForbiddenTranslations);

            if (targets.Count == 0 && forbidden.Count == 0)
            {
                throw new GlossaryValidationException(
                    $"术语 \"{sourceTerm}\" 未勾选「禁止翻译」，请至少填写一个目标语种的标准译法或禁用译法，否则该术语不会产生任何约束。");
            }

            // 可接受变体只在有标准译法的语种里有意义（它是「标准译法的替代写法」）
            foreach (var culture in variants.Keys.Where(c => !targets.ContainsKey(c)).ToList())
            {
                variants.Remove(culture);
            }

            // 禁用译法不能与标准译法 / 变体冲突，否则任何译文都会违规
            foreach (var (culture, banned) in forbidden)
            {
                targets.TryGetValue(culture, out var standard);
                variants.TryGetValue(culture, out var accepted);
                var clash = banned.FirstOrDefault(w =>
                    string.Equals(w, standard, StringComparison.OrdinalIgnoreCase) ||
                    (accepted?.Any(a => string.Equals(a, w, StringComparison.OrdinalIgnoreCase)) ?? false));
                if (clash is not null)
                {
                    throw new GlossaryValidationException(
                        $"[{culture}] 的禁用译法 \"{clash}\" 与标准译法 / 可接受变体相同，请先从其中一边移除。");
                }
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // 同一作用域（全局或同一项目）内 SourceTerm 不允许重复（忽略大小写）
        var sameScope = await db.GlossaryTerms
            .Where(g => g.ProjectId == term.ProjectId && g.Id != term.Id)
            .ToListAsync(ct);
        if (sameScope.Any(g => string.Equals(g.SourceTerm.Trim(), sourceTerm, StringComparison.OrdinalIgnoreCase)))
        {
            throw new GlossaryValidationException(
                $"当前作用域下已存在术语 \"{sourceTerm}\"，请直接编辑已有术语，避免出现相互冲突的译法。");
        }

        term.SourceTerm = sourceTerm;
        term.TargetTranslations = targets;
        term.AcceptedVariants = variants;
        term.ForbiddenTranslations = forbidden;

        var existing = await db.GlossaryTerms.FirstOrDefaultAsync(g => g.Id == term.Id, ct);
        if (existing is null)
        {
            db.GlossaryTerms.Add(term);
        }
        else
        {
            existing.ProjectId = term.ProjectId;
            existing.SourceTerm = term.SourceTerm;
            existing.Description = term.Description;
            existing.DoNotTranslate = term.DoNotTranslate;
            existing.CaseSensitive = term.CaseSensitive;
            existing.TargetTranslations = new Dictionary<string, string>(targets, StringComparer.OrdinalIgnoreCase);
            existing.AcceptedVariants = CopyWordLists(variants);
            existing.ForbiddenTranslations = CopyWordLists(forbidden);
        }

        await SaveAsync(db, ct);
        return existing ?? term;
    }

    private static Dictionary<string, List<string>> CleanWordLists(Dictionary<string, List<string>>? source)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (culture, words) in source ?? [])
        {
            var cleaned = (words ?? [])
                .Select(w => w?.Trim() ?? string.Empty)
                .Where(w => w.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(culture) && cleaned.Count > 0)
            {
                result[culture.Trim()] = cleaned;
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>> CopyWordLists(Dictionary<string, List<string>> source) =>
        source.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);

    public async Task DeleteGlossaryTermAsync(Guid termId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.GlossaryTerms.FirstOrDefaultAsync(g => g.Id == termId, ct);
        if (existing is not null)
        {
            db.GlossaryTerms.Remove(existing);
            await SaveAsync(db, ct);
        }
    }

    public async Task<IReadOnlyList<GlossaryTerm>> GetGlossaryTermsAsync(Guid? projectId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GlossaryTerms
            .Where(g => g.ProjectId == null || g.ProjectId == projectId)
            .ToListAsync(ct);

        // 项目术语完整覆盖同名（忽略大小写）的全局术语
        return EffectiveGlossary(rows);
    }

    public async Task<IReadOnlyList<GlossaryTerm>> GetGlossaryTermsByScopeAsync(Guid? projectId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.GlossaryTerms
            .Where(g => g.ProjectId == projectId)
            .ToListAsync(ct);

        return rows
            .OrderBy(g => g.SourceTerm, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<TranslationLayer>> BuildTranslationLayersAsync(
        string projectCode,
        string targetCulture,
        PublishContentMode? contentMode = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects
            .Include(p => p.Namespaces)
                .ThenInclude(n => n.Entries)
                    .ThenInclude(e => e.Translations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Code == projectCode, ct)
            ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");

        var sharedNamespaces = await db.TranslationNamespaces
            .Where(n => n.IsShared && project.SharedNamespaceCodes.Contains(n.Code))
            .Include(n => n.Entries)
                .ThenInclude(e => e.Translations)
            .AsSplitQuery()
            .ToListAsync(ct);

        return TranslationLayerBuilder.Build(project, sharedNamespaces, targetCulture, contentMode);
    }

    /// <summary>保留产物内容（可回滚）的最近快照数；更早的快照只保留审计记录。</summary>
    public const int SnapshotContentRetention = 30;

    public async Task<PublishPolicy> SavePublishPolicyAsync(string projectCode, PublishPolicy policy, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Code == projectCode, ct)
            ?? throw new InvalidOperationException($"Project '{projectCode}' not found.");

        project.PublishPolicy = policy.Clone().Normalized();
        await SaveAsync(db, ct);
        return project.PublishPolicy;
    }

    public async Task RecordPublishSnapshotAsync(PublishSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // 快照入库与内容清理必须一起成功：清理失败会让 Content 无界增长，入库失败则丢版本记录
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        snapshot.ContentSize = snapshot.Content?.Length ?? 0;
        db.PublishSnapshots.Add(snapshot);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == snapshot.ProjectId, ct);
        if (project is not null)
        {
            project.LastPublishedAt = snapshot.PublishedAt;
            project.LastPublishedVersionHash = snapshot.VersionHash;
        }

        await SaveAsync(db, ct);

        // 只保留最近 N 次快照的产物内容，更早的清空内容（审计记录仍在）
        var stale = await db.PublishSnapshots
            .Where(s => s.ProjectId == snapshot.ProjectId && s.ContentSize > 0)
            .OrderByDescending(s => s.PublishedAt)
            .Skip(SnapshotContentRetention)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            foreach (var old in stale)
            {
                old.Content = null;
                old.ContentSize = 0;
            }

            await SaveAsync(db, ct);
        }

        await transaction.CommitAsync(ct);
        InvalidateDataCache();
    }

    public async Task<PublishSnapshotPage> GetPublishSnapshotPageAsync(
        string projectCode, int skip = 0, int take = 20, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query = db.PublishSnapshots.Where(s => s.Project!.Code == projectCode);

        // 总数与列表分开查：分页器要算页数，不能靠「本页条数 < take」推断是否最后一页
        var total = await query.CountAsync(ct);

        // 不加载 Content 大字段；时间相同时按 Id 兜底排序，保证翻页不漏行、不重复
        var items = await query
            .OrderByDescending(s => s.PublishedAt)
            .ThenByDescending(s => s.Id)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 200))
            .Select(s => new PublishSnapshot
            {
                Id = s.Id,
                ProjectId = s.ProjectId,
                VersionHash = s.VersionHash,
                ReleaseNote = s.ReleaseNote,
                PublishedAt = s.PublishedAt,
                EntryCount = s.EntryCount,
                PublishedCultures = s.PublishedCultures,
                Kind = s.Kind,
                RollbackOfSnapshotId = s.RollbackOfSnapshotId,
                Forced = s.Forced,
                ForceReason = s.ForceReason,
                GateSummary = s.GateSummary,
                ContentSize = s.ContentSize
            })
            .ToListAsync(ct);

        return new PublishSnapshotPage(items, total);
    }

    public async Task<IReadOnlyList<PublishSnapshot>> GetPublishSnapshotsAsync(string projectCode, int take = 30, CancellationToken ct = default) =>
        (await GetPublishSnapshotPageAsync(projectCode, skip: 0, take: take, ct)).Items;

    public async Task<PublishSnapshot?> GetPublishSnapshotAsync(string projectCode, Guid snapshotId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PublishSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Project!.Code == projectCode && s.Id == snapshotId, ct);
    }

    public async Task<PublishSnapshot?> GetLatestPublishSnapshotWithContentAsync(string projectCode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PublishSnapshots
            .AsNoTracking()
            .Where(s => s.Project!.Code == projectCode && s.ContentSize > 0)
            .OrderByDescending(s => s.PublishedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 统一提交入口：把数据库并发令牌冲突翻译成可直接展示给用户的异常。
    /// 不做自动重试——管理后台写频率低，自动重试只会掩盖真实冲突，且与这里显式开启的事务语义冲突。
    /// 成功提交即让全部读缓存失效（写路径的唯一汇总点）。
    /// <para>
    /// 失效必须发生在「事务提交之后」：存在显式事务时，SaveChanges 的结果对其他连接还不可见，
    /// 此时失效会让并发读把旧数据以新版本号缓存起来。所以显式事务内这里不失效，
    /// 由开启事务的方法在 <c>CommitAsync</c> 之后调用 <see cref="InvalidateDataCache"/>。
    /// </para>
    /// </summary>
    private async Task SaveAsync(LinguaDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            if (db.Database.CurrentTransaction is null)
            {
                InvalidateDataCache();
            }
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new LinguaConcurrencyException("数据已被其他会话修改，请刷新页面后重试。", ex);
        }
    }
}
