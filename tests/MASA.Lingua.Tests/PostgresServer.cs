using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 全局共享的测试 PostgreSQL：默认用 Testcontainers 起一个 postgres:18-alpine 容器（整个测试进程只起一次），
/// 每个测试各自建库（由 EF 的 Migrate 完成），结束 <c>DROP</c>。
/// CI 无 Docker 时，用环境变量 <c>LINGUA_TEST_PG</c> 指向外部实例（账号需有建库权限）。
/// </summary>
public static class PostgresServer
{
    private static readonly Lazy<(string Template, string Admin)> Endpoints = new(
        CreateEndpoints,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static PostgreSqlContainer? _container;

    /// <summary>触发容器启动（供 ICollectionFixture 使用；直接调用 NewConnectionString 也会懒启动）。</summary>
    public static void EnsureStarted() => _ = Endpoints.Value;

    /// <summary>生成一个合法的 PostgreSQL 库名（小写 + 下划线，避免引号转义问题）。</summary>
    public static string NewDatabaseName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public static string NewConnectionString(string databaseName) => WithDatabase(Endpoints.Value.Template, databaseName);

    /// <summary>删除测试库（先断开残留连接，否则 DROP 会被占用阻塞）。</summary>
    public static void Drop(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        try
        {
            using var conn = new NpgsqlConnection(Endpoints.Value.Admin);
            conn.Open();

            using (var kill = conn.CreateCommand())
            {
                kill.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()";
                kill.Parameters.AddWithValue("name", databaseName);
                kill.ExecuteNonQuery();
            }

            using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
            drop.ExecuteNonQuery();
        }
        catch (NpgsqlException)
        {
            // 清理失败不影响测试结论（容器最终由 Testcontainers 回收）。
        }
    }

    private static (string Template, string Admin) CreateEndpoints()
    {
        var external = Environment.GetEnvironmentVariable("LINGUA_TEST_PG");
        string template;

        if (!string.IsNullOrWhiteSpace(external))
        {
            template = external;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
            _container.StartAsync().GetAwaiter().GetResult();
            template = _container.GetConnectionString();
        }

        return (template, WithDatabase(template, "postgres"));
    }

    private static string WithDatabase(string connectionString, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = database,
            // 并发建库/删库不需要连接池复用，避免 DROP 时被池连接占住。
            Pooling = false,
        };
        return builder.ConnectionString;
    }
}

/// <summary>让相关测试类共享同一个容器初始化过程（真正的容器是静态单例）。</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public sealed class PostgresFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        PostgresServer.EnsureStarted();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
