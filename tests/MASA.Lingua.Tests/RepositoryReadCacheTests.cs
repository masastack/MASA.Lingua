using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 仓储读缓存的契约（见 <c>EfCoreLinguaRepository</c> 顶部的「正确性约束」）：
/// 命中不再查库；返回的是调用方私有的快照；写入后立即失效；并发的读与写交错时不会把旧数据留在缓存里。
/// 时序类用例用栅栏把「读在写之前开始、写之后才结束」固定下来，是确定性的，不靠压力碰运气。
/// </summary>
public sealed class RepositoryReadCacheTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static async Task ReadAllCachesAsync(ILinguaRepository repo)
    {
        await repo.GetAllProjectsAsync();
        await repo.GetProjectOverviewsAsync();
        await repo.GetProjectImpactSummariesAsync();
        await repo.GetSharedNamespacesAsync();
    }

    // ---------------------------------------------------------------------
    // 命中 / 失效
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Repeated_Reads_Hit_The_Cache_Until_A_Write_Invalidates_It()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_hit");
        var probe = db.NewProbed();
        var repo = probe.Repo;

        await ReadAllCachesAsync(repo);
        Assert.NotEmpty(probe.Commands.Commands);

        // 第二轮全部命中缓存：一条 SQL 都不该发
        probe.Commands.Clear();
        await ReadAllCachesAsync(repo);
        Assert.Empty(probe.Commands.Commands);

        // 四类写入各自让缓存失效，且读到的是写入后的数据
        await repo.UpsertEntryAsync("__shared__", "common", "cacheSharedKey", "共享新词条", null, null);
        await repo.UpsertEntryAsync("shop-vue", "order", "cacheProjectKey", "项目新词条", null, null);
        await repo.SavePublishPolicyAsync("shop-vue", new PublishPolicy { MinCoveragePercent = 61 });
        await repo.CreateProjectAsync("cache-new-proj");

        var projects = await repo.GetAllProjectsAsync();
        Assert.Contains(projects, p => p.Code == "cache-new-proj");
        Assert.Contains(
            projects.Single(p => p.Code == "shop-vue").Namespaces.Single(n => n.Code == "order").Entries,
            e => e.Key == "cacheProjectKey");

        var overviews = await repo.GetProjectOverviewsAsync();
        Assert.Equal(61, overviews.Single(p => p.Code == "shop-vue").PublishPolicy.MinCoveragePercent);
        Assert.Contains(overviews, p => p.Code == "cache-new-proj");

        var shared = await repo.GetSharedNamespacesAsync();
        Assert.Contains(shared.Single(n => n.Code == "common").Entries, e => e.Key == "cacheSharedKey");

        var impacts = await repo.GetProjectImpactSummariesAsync();
        Assert.Contains(impacts, p => p.Code == "cache-new-proj");

        // 删除项目同样失效：不会还从缓存里返回已删除的项目
        Assert.True(await repo.DeleteProjectAsync("cache-new-proj"));
        Assert.DoesNotContain(await repo.GetAllProjectsAsync(), p => p.Code == "cache-new-proj");
        Assert.DoesNotContain(await repo.GetProjectOverviewsAsync(), p => p.Code == "cache-new-proj");
        Assert.DoesNotContain(await repo.GetProjectImpactSummariesAsync(), p => p.Code == "cache-new-proj");
    }

    [Fact]
    public async Task Cache_Snapshots_Are_Private_To_Each_Caller()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_snap");
        var repo = db.Repo;

        // 共享命名空间：调用方清空词条、改编码、移除命名空间，都不能影响下一个调用方
        var shared = (IList<TranslationNamespace>)await repo.GetSharedNamespacesAsync();
        var commonEntries = shared.Single(n => n.Code == "common").Entries.Count;
        Assert.True(commonEntries > 0);
        shared.Single(n => n.Code == "common").Entries.Clear();
        shared.Single(n => n.Code == "common").Code = "tampered";
        shared.Remove(shared.Single(n => n.Code == "errors"));

        var sharedAgain = await repo.GetSharedNamespacesAsync();
        Assert.Equal(["common", "errors"], sharedAgain.Select(n => n.Code));
        Assert.Equal(commonEntries, sharedAgain.Single(n => n.Code == "common").Entries.Count);

        // 全量项目：命名空间列表、语种列表、发布策略都是副本
        var projects = await repo.GetAllProjectsAsync();
        var shop = projects.Single(p => p.Code == "shop-vue");
        var namespaceCount = shop.Namespaces.Count;
        var cultureCount = shop.SupportedCultures.Count;
        var coverage = shop.PublishPolicy.MinCoveragePercent;
        shop.Namespaces.Clear();
        shop.SupportedCultures.Clear();
        shop.PublishPolicy.MinCoveragePercent = coverage + 40;

        var shopAgain = (await repo.GetAllProjectsAsync()).Single(p => p.Code == "shop-vue");
        Assert.Equal(namespaceCount, shopAgain.Namespaces.Count);
        Assert.Equal(cultureCount, shopAgain.SupportedCultures.Count);
        Assert.Equal(coverage, shopAgain.PublishPolicy.MinCoveragePercent);

        // 项目概览：发布策略是副本
        var overview = (await repo.GetProjectOverviewsAsync()).Single(p => p.Code == "shop-vue");
        overview.PublishPolicy.MinCoveragePercent = coverage + 40;
        Assert.Equal(coverage, (await repo.GetProjectOverviewsAsync()).Single(p => p.Code == "shop-vue").PublishPolicy.MinCoveragePercent);

        // 非缓存路径：GetProjectByCodeAsync 每次都是新的私有快照，页面可以就地改自己的副本
        var snapshot = await repo.GetProjectByCodeAsync("shop-vue");
        snapshot!.Namespaces.Clear();
        Assert.NotEmpty((await repo.GetProjectByCodeAsync("shop-vue"))!.Namespaces);
    }

    // ---------------------------------------------------------------------
    // 并发时序（确定性）
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A_Read_That_Overlaps_A_Write_Does_Not_Leave_Stale_Data_In_The_Cache()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_race");
        var probe = db.NewProbed();
        var repo = probe.Repo;

        // 读先开始：查询已在库里执行完（数据快照在写入之前），结果还没交给仓储
        var gate = probe.Commands.PauseNextReader(sql => sql.Contains("ORDER BY p.code") && !sql.Contains("write_key"));
        var overlapped = Task.Run(() => repo.GetProjectOverviewsAsync());
        await gate.Reached.WaitAsync(Timeout);

        // 写入在读的中途完成（提交 + 失效）
        await repo.SavePublishPolicyAsync("shop-vue", new PublishPolicy { MinCoveragePercent = 77 });
        gate.Release();

        // 重叠的那次读拿到的是写入前的数据（它的快照早于写入）——这正是要构造的时序
        var stale = await overlapped.WaitAsync(Timeout);
        Assert.Equal(0, stale.Single(p => p.Code == "shop-vue").PublishPolicy.MinCoveragePercent);

        // 关键：旧数据必须带着「旧版本号」入缓存，下一次读发现版本不符、重新查库。
        // 若版本号是在查询结束后才取（带上写之后的新版本），陈旧数据会被当作新鲜的缓存一直读到 TTL 过期。
        var fresh = await repo.GetProjectOverviewsAsync();
        Assert.Equal(77, fresh.Single(p => p.Code == "shop-vue").PublishPolicy.MinCoveragePercent);
    }

    [Fact]
    public async Task Invalidation_Happens_After_Commit_Not_Before()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_commit");
        var probe = db.NewProbed();
        var repo = probe.Repo;

        // 写入已发给库（SaveChanges 已返回）、事务还没提交：卡住提交
        var commitGate = probe.Commits.PauseNextCommit();
        var writing = Task.Run(() => repo.UpsertEntryAsync(
            "shop-vue", "order", "slowCommitKey", "慢提交词条", null, null));
        await commitGate.Reached.WaitAsync(Timeout);

        // 此时另一条连接读到的是写之前的数据（未提交不可见），而且这次读会进缓存
        var during = await repo.GetAllProjectsAsync();
        Assert.DoesNotContain(
            during.Single(p => p.Code == "shop-vue").Namespaces.Single(n => n.Code == "order").Entries,
            e => e.Key == "slowCommitKey");

        commitGate.Release();
        await writing.WaitAsync(Timeout);

        // 提交之后才失效：如果在 SaveChanges 时就失效，上面那次读会把旧数据盖上新版本号，这里就读不到新词条
        var after = await repo.GetAllProjectsAsync();
        Assert.Contains(
            after.Single(p => p.Code == "shop-vue").Namespaces.Single(n => n.Code == "order").Entries,
            e => e.Key == "slowCommitKey");
    }

    // ---------------------------------------------------------------------
    // 发布 / 导出数据源：不走读缓存
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Publish_Source_Always_Reads_Fresh_Data_Even_When_The_Read_Cache_Is_Stale()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_publish");
        var replicaA = db.NewProbed();
        var replicaB = db.NewProbed(); // 同一个库上的另一个实例 = 多副本部署里的另一个副本

        // A 的共享命名空间缓存已建立；B 写入一条共享词条，A 的缓存不会被通知（只靠 TTL 兜底）
        await replicaA.Repo.GetSharedNamespacesAsync();
        await replicaB.Repo.UpsertEntryAsync("__shared__", "common", "writtenByReplicaB", "副本 B 写的", null, null);

        replicaA.Commands.Clear();
        var source = await replicaA.Repo.LoadPublishSourceAsync("shop-vue");
        Assert.NotNull(source);
        Assert.NotEmpty(replicaA.Commands.Commands); // 确实查了库，没有命中缓存
        Assert.Contains(
            source.SharedNamespaces.Single(n => n.Code == "common").Entries,
            e => e.Key == "writtenByReplicaB");
    }

    [Fact]
    public async Task Publish_Source_Contains_Only_Inherited_Shared_Namespaces_With_Wired_Navigations()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_source");
        var repo = db.Repo;

        Assert.Null(await repo.LoadPublishSourceAsync("no-such-project"));

        // 新项目默认只继承 common；shop-vue 继承 common + errors
        await repo.CreateProjectAsync("source-only-common");
        var narrow = await repo.LoadPublishSourceAsync("source-only-common");
        Assert.Equal(["common"], narrow!.SharedNamespaces.Select(n => n.Code));

        var shop = await repo.LoadPublishSourceAsync("shop-vue");
        Assert.Equal(["common", "errors"], shop!.SharedNamespaces.Select(n => n.Code));

        // 未跟踪读取的导航属性仍然连好：校验消息里的完整 key 靠 entry.Namespace 取命名空间编码
        foreach (var ns in shop.Project.Namespaces.Concat(shop.SharedNamespaces))
        {
            Assert.NotEmpty(ns.Entries);
            Assert.All(ns.Entries, e =>
            {
                Assert.Same(ns, e.Namespace);
                Assert.Equal(ns.Code, e.NamespaceCode);
                Assert.NotEmpty(e.Translations);
            });
        }
    }

    [Fact]
    public async Task GetProjectByCode_Wires_Navigations_Without_Change_Tracking()
    {
        using var db = await RepositoryTestDatabase.CreateAsync("lingua_cache_nav");

        var project = await db.Repo.GetProjectByCodeAsync("shop-vue");
        Assert.NotNull(project);
        Assert.NotEmpty(project.Namespaces);
        foreach (var ns in project.Namespaces)
        {
            Assert.All(ns.Entries, e =>
            {
                Assert.Same(ns, e.Namespace);
                Assert.Equal(ns.Code, e.NamespaceCode);
            });
        }

        Assert.Null(await db.Repo.GetProjectByCodeAsync("no-such-project"));
    }
}
