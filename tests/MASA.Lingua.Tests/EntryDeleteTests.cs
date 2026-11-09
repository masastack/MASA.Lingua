using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 词条矩阵的「删除词条」：删词条要连带清掉全部语种的译文，命名空间删空后移除空壳；
/// Key 不存在时返回 false，让页面能提示「词条不存在或已被删除」而不是假装成功。
/// </summary>
public sealed class EntryDeleteTests
{
    private static async Task<ILinguaRepository> CreateRepoAsync(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return provider.GetRequiredService<ILinguaRepository>();
    }

    private static async Task SeedProjectAsync(ILinguaRepository repo, string code)
    {
        await repo.CreateProjectAsync(code);
    }

    [Fact]
    public async Task Delete_Should_Remove_Entry_Translations_And_Empty_Namespace()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_entry_delete");
        try
        {
            var repo = await CreateRepoAsync(databaseName);
            await SeedProjectAsync(repo, "delete-app");

            await repo.UpsertEntryAsync(
                "delete-app", "ns", "k", "确定", "弹窗主按钮", 10,
                new Dictionary<string, string> { ["zh-CN"] = "确定", ["en-US"] = "OK" });

            var deleted = await repo.DeleteEntryAsync("delete-app", "ns", "k");
            Assert.True(deleted);

            // 词条连同译文一起消失，空壳命名空间也被移除，不会在下拉框里留空项
            var project = await repo.GetProjectByCodeAsync("delete-app");
            Assert.NotNull(project);
            Assert.DoesNotContain(project!.Namespaces, n => n.Code == "ns");
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Delete_Should_Keep_Namespace_When_Other_Entries_Remain()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_entry_delete_keep");
        try
        {
            var repo = await CreateRepoAsync(databaseName);
            await SeedProjectAsync(repo, "delete-keep-app");

            await repo.UpsertEntryAsync("delete-keep-app", "ns", "a", "甲", null, null);
            await repo.UpsertEntryAsync("delete-keep-app", "ns", "b", "乙", null, null);

            var deleted = await repo.DeleteEntryAsync("delete-keep-app", "ns", "a");
            Assert.True(deleted);

            var project = await repo.GetProjectByCodeAsync("delete-keep-app");
            Assert.NotNull(project);
            var ns = project!.Namespaces.Single(n => n.Code == "ns");
            Assert.Equal(["b"], ns.Entries.Select(e => e.Key).ToList());
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Delete_Should_Return_False_When_Key_Or_Namespace_Missing()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_entry_delete_missing");
        try
        {
            var repo = await CreateRepoAsync(databaseName);
            await SeedProjectAsync(repo, "delete-missing-app");

            Assert.False(await repo.DeleteEntryAsync("delete-missing-app", "ns", "ghost"));
            Assert.False(await repo.DeleteEntryAsync("no-such-project", "ns", "k"));
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Delete_Should_Remove_Shared_Entry_Via_Shared_Scope()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_entry_delete_shared");
        try
        {
            var repo = await CreateRepoAsync(databaseName);

            // 初始化会预置演示共享命名空间（common 等），这里用独立命名空间验证「删空即移除」
            await repo.UpsertEntryAsync(
                "__shared__", "tmp-delete-ns", "ok", "确定", null, null,
                new Dictionary<string, string> { ["en-US"] = "OK" });

            var deleted = await repo.DeleteEntryAsync("__shared__", "tmp-delete-ns", "ok");
            Assert.True(deleted);

            var shared = await repo.GetSharedNamespacesAsync();
            Assert.DoesNotContain(shared, n => n.Code == "tmp-delete-ns");
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }
}
