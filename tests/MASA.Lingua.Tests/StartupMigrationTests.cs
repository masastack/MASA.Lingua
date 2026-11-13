using MASA.Lingua.Core.Domain;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 站点每次启动都执行 EF Core 迁移（写法参照 MASA.Auth 的 <c>MigrateDbContextAsync</c>）：
/// 有待执行的迁移才执行并写日志，已是最新什么都不做，失败则记 Critical 并原样抛出（启动终止，不在旧表结构上运行）。
/// </summary>
public sealed class StartupMigrationTests
{
    private const string MigrationLogCategory = "MASA.Lingua.Storage.Migration";

    private sealed class ListLoggerProvider(List<(LogLevel Level, string Message)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, sink);

        public void Dispose()
        {
        }

        private sealed class ListLogger(string category, List<(LogLevel Level, string Message)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // EF Core 自己也会往同一个日志工厂写大量日志：只收迁移入口的
                if (category == MigrationLogCategory)
                {
                    lock (sink)
                    {
                        sink.Add((logLevel, formatter(state, exception)));
                    }
                }
            }
        }
    }

    private static ServiceProvider BuildProvider(string databaseName, List<(LogLevel Level, string Message)> logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)));
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Startup_Should_Apply_Pending_Migrations_And_Be_A_NoOp_When_Up_To_Date()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_startmig");
        var logs = new List<(LogLevel Level, string Message)>();
        try
        {
            await using var provider = BuildProvider(databaseName, logs);

            // 第一次启动：空库，所有迁移都待执行，执行了哪些迁移写进日志
            await provider.MigrateLinguaDatabaseAsync();
            Assert.Contains(logs, l => l.Level == LogLevel.Information
                                       && l.Message.Contains("待执行")
                                       && l.Message.Contains("CaseInsensitiveKeyUniqueness"));
            Assert.Contains(logs, l => l.Message.Contains("执行完成"));

            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            // 之后每次启动：已是最新，只记一条信息，不再执行迁移
            logs.Clear();
            await provider.MigrateLinguaDatabaseAsync();
            var upToDate = Assert.Single(logs);
            Assert.Equal(LogLevel.Information, upToDate.Level);
            Assert.Contains("已是最新", upToDate.Message);

            // 启动入口（迁移 + 种子）可以重复执行：种子只对空库生效
            await provider.InitializeLinguaDatabaseAsync();
            await provider.InitializeLinguaDatabaseAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.True(await db.Projects.AnyAsync());
            }
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Startup_Should_Apply_Only_The_Migrations_That_Are_Still_Pending()
    {
        const string previousMigration = "20261103013433_AddProjectWriteKey";
        var databaseName = PostgresServer.NewDatabaseName("lingua_startmig");
        var logs = new List<(LogLevel Level, string Message)>();
        try
        {
            await using var provider = BuildProvider(databaseName, logs);
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();

            // 模拟「旧版本部署过的库」：只执行到上一个迁移
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync(previousMigration);
            }

            await provider.MigrateLinguaDatabaseAsync();

            var started = Assert.Single(logs, l => l.Message.Contains("待执行"));
            Assert.Contains("1 个待执行", started.Message);
            Assert.Contains("CaseInsensitiveKeyUniqueness", started.Message);
            Assert.DoesNotContain("Initial", started.Message); // 已经执行过的迁移不会再列出来
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task Startup_Should_Fail_Fast_With_A_Critical_Log_When_A_Migration_Fails()
    {
        const string previousMigration = "20261103013433_AddProjectWriteKey";
        var databaseName = PostgresServer.NewDatabaseName("lingua_startmig");
        var logs = new List<(LogLevel Level, string Message)>();
        try
        {
            await using var provider = BuildProvider(databaseName, logs);
            var factory = provider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();

            // 旧库里存在仅大小写不同的共享命名空间：CaseInsensitiveKeyUniqueness 会拒绝升级
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync(previousMigration);
                db.TranslationNamespaces.AddRange(
                    new TranslationNamespace { Code = "Dup", IsShared = true },
                    new TranslationNamespace { Code = "dup", IsShared = true });
                await db.SaveChangesAsync();
            }

            var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.MigrateLinguaDatabaseAsync());
            Assert.Contains("仅大小写不同", error.Message);
            Assert.Contains(logs, l => l.Level == LogLevel.Critical && l.Message.Contains("站点不会继续启动"));

            // 启动入口同样失败，种子不会在旧表结构上执行
            await Assert.ThrowsAnyAsync<Exception>(() => provider.InitializeLinguaDatabaseAsync());
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.False(await db.Projects.AnyAsync());
                Assert.Equal(2, await db.TranslationNamespaces.CountAsync());
            }
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }
}
