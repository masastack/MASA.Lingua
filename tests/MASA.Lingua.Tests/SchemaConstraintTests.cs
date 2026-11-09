using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 数据库层约束：命名空间编码在作用域内唯一（含全局作用域），
/// 行版本并发令牌能拦住「后写入的会话覆盖先写入的修改」。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaConstraintTests
{
    private static async Task<(IServiceProvider Provider, string DatabaseName)> CreateDatabaseAsync(string prefix)
    {
        var databaseName = PostgresServer.NewDatabaseName(prefix);
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider, databaseName);
    }

    [Fact]
    public async Task Namespace_Code_Should_Be_Unique_Within_Project_Scope()
    {
        var (provider, databaseName) = await CreateDatabaseAsync("lingua_schema_ns");
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var project = await db.Projects.FirstAsync(p => p.Code == "shop-vue");

            // 种子里 shop-vue 已有 order 命名空间：数据库必须拒绝重复，而不是只靠仓储的先查后写
            db.TranslationNamespaces.Add(new TranslationNamespace { ProjectId = project.Id, Code = "order" });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Namespace_Code_Should_Be_Unique_For_Global_Scope_Too()
    {
        var (provider, databaseName) = await CreateDatabaseAsync("lingua_schema_global_ns");
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            // 全局命名空间的 project_id 为 NULL：靠「project_id IS NULL」那条部分唯一索引兜住，不会因为 NULL 互不相等而逃逸
            db.TranslationNamespaces.Add(new TranslationNamespace { Code = "common", IsShared = true });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Update_Should_Fail_On_RowVersion_Mismatch()
    {
        var (provider, databaseName) = await CreateDatabaseAsync("lingua_schema_concurrency");
        try
        {
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();

            await using var first = await factory.CreateDbContextAsync();
            await using var second = await factory.CreateDbContextAsync();

            var firstProject = await first.Projects.FirstAsync(p => p.Code == "shop-vue");
            var secondProject = await second.Projects.FirstAsync(p => p.Code == "shop-vue");

            firstProject.LastPublishedVersionHash = "hash-from-first-session";
            await first.SaveChangesAsync();

            // 第二个会话读到的是旧版本：xmin 已变，写入必须失败而不是静默覆盖
            secondProject.LastPublishedVersionHash = "hash-from-second-session";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }
}
