using System.Data.Common;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace MASA.Lingua.Tests;

/// <summary>
/// 可以被测试「卡住」的一个时间点：被测代码走到这里时通知测试（<see cref="Reached"/>）并等待放行（<see cref="Release"/>）。
/// 用来把「读与提交交错」这类并发时序做成确定性用例，而不是靠碰运气的压力测试。
/// </summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>被测代码已经停在栅栏处。</summary>
    public Task Reached => _reached.Task;

    public void Release() => _released.TrySetResult();

    /// <summary>被测代码调用：报到并等放行。</summary>
    internal async ValueTask PassAsync()
    {
        _reached.TrySetResult();
        await _released.Task;
    }
}

/// <summary>
/// 记录经过的所有 SQL，并可在「下一条匹配的查询已在库里执行完、结果还没被读走」时卡住。
/// 读事务在语句开始时就确定了可见的数据快照，卡在这里再让写入提交，就得到「读在写之前开始、写之后才结束」的时序。
/// </summary>
internal sealed class CommandRecorder : DbCommandInterceptor
{
    private readonly object _lock = new();
    private readonly List<string> _commands = [];
    private (Gate Gate, Func<string, bool> Match)? _pending;

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_lock)
            {
                return [.. _commands];
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _commands.Clear();
        }
    }

    /// <summary>卡住下一条文本满足 <paramref name="match"/> 的查询（只卡一次）。</summary>
    public Gate PauseNextReader(Func<string, bool> match)
    {
        var gate = new Gate();
        lock (_lock)
        {
            _pending = (gate, match);
        }

        return gate;
    }

    private void Record(DbCommand command)
    {
        lock (_lock)
        {
            _commands.Add(command.CommandText);
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Gate? gate = null;
        lock (_lock)
        {
            if (_pending is { } pending && pending.Match(command.CommandText))
            {
                gate = pending.Gate;
                _pending = null;
            }
        }

        if (gate is not null)
        {
            await gate.PassAsync();
        }

        return result;
    }
}

/// <summary>可在「事务即将提交」处卡住：此时写入已发给库（SaveChanges 已返回）但对其他连接还不可见。</summary>
internal sealed class CommitGate : DbTransactionInterceptor
{
    private readonly object _lock = new();
    private Gate? _pending;

    /// <summary>卡住下一次事务提交（只卡一次）。</summary>
    public Gate PauseNextCommit()
    {
        var gate = new Gate();
        lock (_lock)
        {
            _pending = gate;
        }

        return gate;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        Gate? gate;
        lock (_lock)
        {
            gate = _pending;
            _pending = null;
        }

        if (gate is not null)
        {
            await gate.PassAsync();
        }

        return result;
    }
}

/// <summary>
/// 一个独立的测试库（走正式迁移 + 种子）和挂了探针的仓储：
/// <see cref="Repo"/> 是生产配置的仓储；<see cref="NewProbed"/> 在同一个库上另建一份带 SQL 记录 / 时序栅栏的仓储实例
/// （每个实例有自己的读缓存，等价于多副本部署里的另一个副本）。
/// </summary>
internal sealed class RepositoryTestDatabase : IDisposable
{
    private readonly ServiceProvider _provider;

    private RepositoryTestDatabase(string databaseName, ServiceProvider provider)
    {
        DatabaseName = databaseName;
        _provider = provider;
        Repo = provider.GetRequiredService<ILinguaRepository>();
    }

    public string DatabaseName { get; }

    public string ConnectionString => PostgresServer.NewConnectionString(DatabaseName);

    public ILinguaRepository Repo { get; }

    public static async Task<RepositoryTestDatabase> CreateAsync(string prefix)
    {
        var databaseName = PostgresServer.NewDatabaseName(prefix);
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return new RepositoryTestDatabase(databaseName, provider);
    }

    public ProbedRepository NewProbed() => new(ConnectionString);

    public void Dispose()
    {
        _provider.Dispose();
        PostgresServer.Drop(DatabaseName);
    }
}

/// <summary>同一个库上的另一份仓储实例，带 SQL 记录与时序栅栏。</summary>
internal sealed class ProbedRepository
{
    private sealed class Factory(string connectionString, params IInterceptor[] interceptors)
        : IDbContextFactory<LinguaDbContext>
    {
        public LinguaDbContext CreateDbContext()
        {
            var builder = new DbContextOptionsBuilder<LinguaDbContext>();
            LinguaStorageExtensions.ConfigureDbContextOptions(builder, connectionString);
            builder.AddInterceptors(interceptors);
            return new LinguaDbContext(builder.Options);
        }
    }

    public ProbedRepository(string connectionString)
    {
        Repo = new EfCoreLinguaRepository(new Factory(connectionString, Commands, Commits));
    }

    public CommandRecorder Commands { get; } = new();

    public CommitGate Commits { get; } = new();

    public EfCoreLinguaRepository Repo { get; }
}
