using System.Text;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 命名空间编码 / 词条 Key「忽略大小写唯一」的端到端回归（存储层）：
/// 写法不同的同一词条是更新而不是新建、导入与缺词上报同口径、冲突检测忽略大小写、
/// 数据库唯一索引兜底，以及升级迁移遇到存量重复项时的行为。
/// 纯函数与 SDK 取词的大小写不敏感见 <see cref="KeyPathDuplicationTests"/>。
/// </summary>
public sealed class CaseInsensitiveKeyTests
{
    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }
        public required string DatabaseName { get; init; }

        public ILinguaRepository Repo => Provider.GetRequiredService<ILinguaRepository>();

        public IDbContextFactory<LinguaDbContext> Factory => Provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();

        public void Dispose()
        {
            Provider.Dispose();
            PostgresServer.Drop(DatabaseName);
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_casekey");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return new Harness { Provider = provider, DatabaseName = databaseName };
    }

    private static Task<TranslationEntry> UpsertAsync(ILinguaRepository repo, string project, string ns, string key, string zh, string en) =>
        repo.UpsertEntryAsync(project, ns, key, zh, null, null,
            new Dictionary<string, string> { ["zh-CN"] = zh, ["en-US"] = en },
            status: TranslationStatus.Approved, acknowledgeIssues: true);

    private static async Task<List<(string Namespace, string Key)>> ProjectEntriesAsync(ILinguaRepository repo) =>
        (await repo.GetProjectByCodeAsync("shop-vue"))!.Namespaces
            .SelectMany(n => n.Entries.Select(e => (n.Code, e.Key)))
            .ToList();

    // ------------------------------------------------------------------
    // 手工写入 / 批量写入 / 删除
    // ------------------------------------------------------------------

    [Fact]
    public async Task Upsert_Should_Update_The_Existing_Entry_When_Only_The_Case_Differs()
    {
        using var h = await CreateHarnessAsync();

        var first = await UpsertAsync(h.Repo, "shop-vue", "pay", "Cancel", "取消", "Cancel");
        var second = await UpsertAsync(h.Repo, "shop-vue", "PAY", "cancel", "取消2", "Cancel 2");

        // 同一个词条：Id 相同，库里保留首次创建时的写法，不会另建一条
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Cancel", second.Key);
        Assert.Equal("pay", second.NamespaceCode);
        Assert.Equal("取消2", second.SourceText);

        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var ns = Assert.Single(project.Namespaces, n => string.Equals(n.Code, "pay", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("pay", ns.Code);
        Assert.Equal("Cancel", Assert.Single(ns.Entries).Key);
    }

    [Fact]
    public async Task Shared_Pool_Upsert_Should_Reuse_The_Seeded_Namespace_And_Entry_Regardless_Of_Case()
    {
        using var h = await CreateHarnessAsync();
        var before = (await h.Repo.GetSharedNamespacesAsync()).Single(n => n.Code == "common").Entries.Count;

        var updated = await UpsertAsync(h.Repo, "__shared__", "COMMON", "OK", "好的", "Okay"); // 种子里是 common / ok
        var created = await UpsertAsync(h.Repo, "__shared__", "Common", "brandNew", "新", "New");

        Assert.Equal("ok", updated.Key);
        Assert.Equal("common", updated.NamespaceCode);
        Assert.Equal("common", created.NamespaceCode);

        var shared = await h.Repo.GetSharedNamespacesAsync();
        var common = Assert.Single(shared, n => string.Equals(n.Code, "common", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before + 1, common.Entries.Count); // 只多了 brandNew 一条
    }

    [Fact]
    public async Task Batch_Upsert_Should_Fold_Case_Variants_Within_And_Across_Batches()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "shop-vue", "pay", "Cancel", "取消", "Cancel");

        static TranslationUpsertBatchItem Item(string ns, string key, string zh) => new(
            ns, key, zh, null, null,
            new Dictionary<string, string> { ["zh-CN"] = zh },
            TranslationStatus.AiTranslated);

        await h.Repo.UpsertTranslationsBatchAsync("shop-vue",
        [
            Item("PAY", "CANCEL", "取消（批量）"),   // 命中已有词条
            Item("Fresh", "A", "新甲"),                // 本批新建
            Item("fresh", "a", "新甲（再次）"),         // 与上一条同一词条：同批内去重
        ]);

        var entries = await ProjectEntriesAsync(h.Repo);
        Assert.Equal(1, entries.Count(e => string.Equals(e.Namespace, "pay", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(("Fresh", "A"), entries);
        Assert.Single(entries, e => string.Equals(e.Namespace, "fresh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Namespace is "PAY" or "fresh" || e.Key is "CANCEL" or "a");
    }

    [Fact]
    public async Task Delete_Should_Find_The_Entry_Regardless_Of_Case()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "shop-vue", "pay", "Cancel", "取消", "Cancel");

        Assert.True(await h.Repo.DeleteEntryAsync("shop-vue", "PAY", "CANCEL"));
        Assert.DoesNotContain(await ProjectEntriesAsync(h.Repo), e => string.Equals(e.Namespace, "pay", StringComparison.OrdinalIgnoreCase));
        Assert.False(await h.Repo.DeleteEntryAsync("shop-vue", "pay", "Cancel")); // 已经删掉了
    }

    // ------------------------------------------------------------------
    // 缺词上报 / 冲突预检
    // ------------------------------------------------------------------

    [Fact]
    public async Task RecordMissingKeys_Should_Treat_Case_Variants_As_Already_Existing()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "shop-vue", "Confirm", "Title", "确认标题", "Confirm Title");

        var created = await h.Repo.RecordMissingKeysAsync(new MissingKeyBatchReport("shop-vue", "en-US",
        [
            new MissingKeyReportItem("confirm", "TITLE", "已存在，大小写不同"),   // 已存在：不创建
            new MissingKeyReportItem("CONFIRM", "NewOne", "新词条"),            // 落在已有命名空间 Confirm 里，不会另建 CONFIRM
            new MissingKeyReportItem("", "CONFIRM", "根裸 key"),                // 与命名空间 Confirm 路径冲突：不创建
            new MissingKeyReportItem("Fresh", "K", "新甲"),
            new MissingKeyReportItem("fresh", "k", "新甲（大小写不同）"),         // 本批内重复
        ]));

        Assert.Equal(2, created);
        var entries = await ProjectEntriesAsync(h.Repo);
        Assert.Contains(("Confirm", "NewOne"), entries);
        Assert.Contains(("Fresh", "K"), entries);
        Assert.DoesNotContain(entries, e => e.Namespace is "CONFIRM" or "confirm" or "fresh");
        Assert.DoesNotContain(entries, e => e.Namespace.Length == 0 && string.Equals(e.Key, "confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindKeyPathConflicts_Should_Ignore_Case()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "shop-vue", "Confirm", "Title", "确认标题", "Confirm Title");

        async Task<bool> ConflictsAsync(string project, string ns, string key) =>
            (await h.Repo.FindKeyPathConflictsAsync(project, ns, key)).Count > 0;

        Assert.True(await ConflictsAsync("shop-vue", "", "CONFIRM"));          // 根裸 key 与命名空间 Confirm
        Assert.True(await ConflictsAsync("shop-vue", "", "confirm.TITLE"));    // 同一路径的另一种写法（不同词条）
        Assert.True(await ConflictsAsync("shop-vue", "CONFIRM", "title.deep")); // 压在已有值之下
        Assert.True(await ConflictsAsync("shop-vue", "", "COMMON"));           // 继承的共享命名空间 common
        Assert.True(await ConflictsAsync("__shared__", "", "ERRORS"));

        Assert.False(await ConflictsAsync("shop-vue", "confirm", "TITLE"));    // 同一词条（更新）
        Assert.False(await ConflictsAsync("shop-vue", "CONFIRM", "confirm"));  // 命名空间下的同名 key 是不同层级
        Assert.False(await ConflictsAsync("shop-vue", "pay", "brandNew"));
    }

    // ------------------------------------------------------------------
    // 导入
    // ------------------------------------------------------------------

    [Fact]
    public async Task Import_Should_Update_Existing_Entries_Whose_Names_Differ_Only_By_Case()
    {
        using var h = await CreateHarnessAsync();
        await h.Repo.ImportJsonBundleAsync("shop-vue", "zh-CN", Encoding.UTF8.GetBytes("""{ "pay": { "Cancel": "取消" } }"""));

        var result = await h.Repo.ImportJsonBundleAsync("shop-vue", "zh-CN",
            Encoding.UTF8.GetBytes("""{ "PAY": { "CANCEL": "取消 v2" } }"""));

        Assert.Equal(0, result.AddedEntries);
        Assert.Equal(1, result.UpdatedTranslations);
        Assert.Equal(["pay"], result.AffectedNamespaces); // 以库里的写法登记
        Assert.Equal(0, result.CaseDuplicateCount);

        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var ns = Assert.Single(project.Namespaces, n => string.Equals(n.Code, "pay", StringComparison.OrdinalIgnoreCase));
        var entry = Assert.Single(ns.Entries);
        Assert.Equal(("pay", "Cancel", "取消 v2"), (ns.Code, entry.Key, entry.SourceText));
    }

    [Fact]
    public async Task Import_Should_Report_Case_Only_Duplicates_Inside_The_File_And_Merge_Them()
    {
        using var h = await CreateHarnessAsync();

        var result = await h.Repo.ImportJsonBundleAsync("shop-vue", "zh-CN", Encoding.UTF8.GetBytes(
            """{ "Confirm.Title": "甲", "confirm.title": "乙", "ok": "好", "ok": "好" }"""));

        Assert.Equal(1, result.CaseDuplicateCount);
        var message = Assert.Single(result.CaseDuplicates);
        Assert.Contains("\"Confirm.Title\"", message);
        Assert.Contains("\"confirm.title\"", message);
        Assert.Equal(0, result.PathConflictCount); // 合并后只剩一个词条，不构成路径冲突

        var project = (await h.Repo.GetProjectByCodeAsync("shop-vue"))!;
        var ns = Assert.Single(project.Namespaces, n => string.Equals(n.Code, "Confirm", StringComparison.OrdinalIgnoreCase));
        var entry = Assert.Single(ns.Entries);
        Assert.Equal(("Confirm", "Title", "乙"), (ns.Code, entry.Key, entry.SourceText)); // 首次写法 + 后出现的内容
    }

    // ------------------------------------------------------------------
    // 数据库兜底：表达式唯一索引 + 升级迁移
    // ------------------------------------------------------------------

    private static async Task AssertUniqueViolationAsync(Func<Task> save)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(save);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task Database_Should_Reject_Case_Insensitive_Duplicates_As_A_Safety_Net()
    {
        using var h = await CreateHarnessAsync();
        await UpsertAsync(h.Repo, "shop-vue", "Confirm", "Title", "确认标题", "Confirm Title");

        Guid projectId, namespaceId;
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            projectId = (await db.Projects.SingleAsync(p => p.Code == "shop-vue")).Id;
            namespaceId = (await db.TranslationNamespaces.SingleAsync(n => n.ProjectId == projectId && n.Code == "Confirm")).Id;
        }

        // 同一个命名空间里仅大小写不同的 Key
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.TranslationEntries.Add(new TranslationEntry { NamespaceId = namespaceId, Key = "TITLE", SourceText = "x" });
            await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
        }

        // 同一个项目里仅大小写不同的命名空间
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.TranslationNamespaces.Add(new TranslationNamespace { ProjectId = projectId, Code = "CONFIRM" });
            await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
        }

        // 共享命名空间池里仅大小写不同的命名空间
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.TranslationNamespaces.Add(new TranslationNamespace { Code = "COMMON", IsShared = true });
            await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
        }

        // 不同项目可以各自有同名命名空间（唯一范围没有被放大）
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            var otherProject = await db.Projects.FirstAsync(p => p.Id != projectId);
            db.TranslationNamespaces.Add(new TranslationNamespace { ProjectId = otherProject.Id, Code = "CONFIRM" });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Migration_Should_Abort_On_Existing_Case_Duplicates_Without_Touching_Data_Then_Enforce_The_Index()
    {
        const string previousMigration = "20261103013433_AddProjectWriteKey";
        const string caseMigration = "20261113024448_CaseInsensitiveKeyUniqueness";

        var databaseName = PostgresServer.NewDatabaseName("lingua_casemig");
        try
        {
            var services = new ServiceCollection();
            services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();

            // 升级前的库：唯一索引区分大小写，库里可以存在仅大小写不同的重复项
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync(previousMigration);

                var upper = new TranslationNamespace { Code = "Dup", IsShared = true };
                var lower = new TranslationNamespace { Code = "dup", IsShared = true };
                upper.Entries.Add(new TranslationEntry { Namespace = upper, Key = "Confirm", SourceText = "a" });
                upper.Entries.Add(new TranslationEntry { Namespace = upper, Key = "confirm", SourceText = "b" });
                db.TranslationNamespaces.AddRange(upper, lower);
                await db.SaveChangesAsync();
            }

            // 升级：检测到重复项，中止并把它们列出来
            await using (var db = await factory.CreateDbContextAsync())
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.GetService<IMigrator>().MigrateAsync());
                Assert.Contains("仅大小写不同", ex.Message);
                Assert.Contains("共享命名空间", ex.Message);
                Assert.Contains("\"Dup\"", ex.Message);
                Assert.Contains("\"dup\"", ex.Message);
                Assert.Contains("\"Confirm\"", ex.Message);
                Assert.Contains("\"confirm\"", ex.Message);
            }

            // 没有修改任何数据，迁移也没有被记录
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await db.TranslationNamespaces.CountAsync(n => n.Code == "Dup" || n.Code == "dup"));
                Assert.Equal(2, await db.TranslationEntries.CountAsync(e => e.Namespace!.Code == "Dup"));
                Assert.Contains(caseMigration, await db.Database.GetPendingMigrationsAsync());
            }

            // 维护者处理掉重复项后升级成功
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.TranslationNamespaces.RemoveRange(await db.TranslationNamespaces.Where(n => n.Code == "dup").ToListAsync());
                db.TranslationEntries.RemoveRange(await db.TranslationEntries.Where(e => e.Key == "confirm").ToListAsync());
                await db.SaveChangesAsync();
            }

            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());

                // 新索引生效：再写仅大小写不同的重复项会被数据库拒绝
                var dupId = (await db.TranslationNamespaces.SingleAsync(n => n.Code == "Dup")).Id;
                db.TranslationEntries.Add(new TranslationEntry { NamespaceId = dupId, Key = "CONFIRM", SourceText = "c" });
                await AssertUniqueViolationAsync(() => db.SaveChangesAsync());
            }
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }
}
