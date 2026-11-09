using System.Reflection;
using Masa.BuildingBlocks.StackSdks.Config;
using Masa.BuildingBlocks.StackSdks.Pm;
using Masa.BuildingBlocks.StackSdks.Pm.Model;
using Masa.BuildingBlocks.StackSdks.Pm.Service;
using MASA.Lingua.Server.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// PM 目录服务的调用收敛行为：成功缓存、失败短缓存、单飞、手动重试绕过缓存。
/// 这些是「PM 不可用时页面加载不应放大成大量外呼/日志」的契约，不依赖真实 PM。
/// </summary>
public sealed class PmProjectDirectoryTests
{
    [Fact]
    public async Task Success_Should_Be_Cached_And_Grouped_By_Project_In_Order()
    {
        var pm = new FakePm(() =>
        [
            Project("B 项目", ("b-web", "Web")),
            Project("A 项目", ("a-web", "Web"), ("a-api", "API"))
        ]);
        var directory = CreateDirectory(pm);

        var first = await directory.GetProjectsAsync();
        var second = await directory.GetProjectsAsync();

        Assert.Equal(1, pm.Calls);
        Assert.Same(first, second);
        // 项目按名称排序；项目内应用按名称排序
        Assert.Equal(["A 项目", "B 项目"], first.Select(p => p.Name));
        Assert.Equal(["a-api", "a-web"], first[0].Apps.Select(a => a.Identity));
        Assert.Equal(["b-web"], first[1].Apps.Select(a => a.Identity));
    }

    [Fact]
    public async Task Projects_Without_Apps_Should_Be_Omitted()
    {
        // 真实 PM 里有不少没有应用的项目（Masa.Auth / Masa.Pm 等）：Lingua 的配置挂在应用上，选了也无事可做
        var pm = new FakePm(() =>
        [
            Project("Empty"),
            Project("HasApps", ("x-web", "Web"))
        ]);
        var directory = CreateDirectory(pm);

        var projects = await directory.GetProjectsAsync();

        Assert.Equal(["HasApps"], projects.Select(p => p.Name));
    }

    [Fact]
    public async Task Same_App_Identity_Under_Two_Projects_Should_Stay_In_Both()
    {
        // PM 里 Lonsid_MESPro_Web_Host 同时挂在 lonsid-smart 与 Lonsid_CRM 下：不能去重，否则其中一个项目下选不到它
        var pm = new FakePm(() =>
        [
            Project("lonsid-smart", ("Lonsid_MESPro_Web_Host", "Lonsid_MESPro_Web_Host")),
            Project("Lonsid_CRM", ("Lonsid_MESPro_Web_Host", "mes"))
        ]);
        var directory = CreateDirectory(pm);

        var projects = await directory.GetProjectsAsync();

        Assert.All(projects, p => Assert.Contains(p.Apps, a => a.Identity == "Lonsid_MESPro_Web_Host"));
        Assert.Equal(2, projects.Count);
    }

    [Fact]
    public async Task Failure_Should_Be_Cached_Briefly_So_Repeated_Loads_Do_Not_Hit_Pm()
    {
        var pm = new FakePm(() => throw new HttpRequestException("pm down"));
        var directory = CreateDirectory(pm);

        // 预渲染 + 电路初始化：连续两次加载
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => directory.GetProjectsAsync());
        var second = await Assert.ThrowsAsync<HttpRequestException>(() => directory.GetProjectsAsync());

        Assert.Equal(1, pm.Calls);
        Assert.Equal("pm down", first.Message);
        Assert.Equal("pm down", second.Message);
    }

    [Fact]
    public async Task ForceRefresh_Should_Bypass_Failure_Cache_And_Recover()
    {
        var healthy = false;
        var pm = new FakePm(() => healthy
            ? new List<ProjectAppsModel> { Project("P", ("p-web", "Web")) }
            : throw new HttpRequestException("pm down"));
        var directory = CreateDirectory(pm);

        await Assert.ThrowsAsync<HttpRequestException>(() => directory.GetProjectsAsync());

        healthy = true;
        // 普通调用仍被失败缓存挡回——不会自己重试
        await Assert.ThrowsAsync<HttpRequestException>(() => directory.GetProjectsAsync());
        Assert.Equal(1, pm.Calls);

        // 用户点「重试」：绕过失败缓存，真实请求
        var recovered = await directory.GetProjectsAsync(forceRefresh: true);
        Assert.Equal(2, pm.Calls);
        Assert.Equal(["p-web"], recovered.SelectMany(p => p.Apps).Select(a => a.Identity));

        // 成功后失败缓存被清掉，后续普通调用直接命中成功缓存
        var after = await directory.GetProjectsAsync();
        Assert.Equal(2, pm.Calls);
        Assert.Same(recovered, after);
    }

    [Fact]
    public async Task Concurrent_Misses_Should_Share_One_Pm_Request()
    {
        var release = new TaskCompletionSource();
        var pm = new FakePm(() => [Project("P", ("p-web", "Web"))], gate: release.Task);
        var directory = CreateDirectory(pm);

        // 多个电路/标签页同时初始化
        var loads = Enumerable.Range(0, 8).Select(_ => Task.Run(() => directory.GetProjectsAsync())).ToArray();
        await pm.FirstCallStarted;
        release.SetResult();
        var results = await Task.WhenAll(loads);

        Assert.Equal(1, pm.Calls);
        Assert.All(results, r => Assert.Equal(["p-web"], r.SelectMany(p => p.Apps).Select(a => a.Identity)));
    }

    [Fact]
    public async Task Fetch_Should_Log_Environment_And_Request_Uri()
    {
        // 项目清单按环境过滤：日志里必须能看出打到哪个环境、哪个 PM 地址，
        // 否则「少项目 / 404 / 401」只能靠抓包反推。
        var pm = new FakePm(() => [Project("P", ("p-web", "Web"))]);
        var logger = new CapturingLogger<PmProjectDirectory>();
        var directory = CreateDirectory(pm, logger);

        await directory.GetProjectsAsync();

        var message = Assert.Single(logger.InformationMessages);
        Assert.Contains("Development", message);
        Assert.Contains("https://pm.example.com/open-api/projectwithapps/Development", message);
    }

    [Fact]
    public async Task Hung_Pm_Call_Should_Throw_TimeoutException_Within_Bound()
    {
        // 永不开闸的闸门 = 部署网络到 PM 的「黑洞」（SYN 被丢弃、请求永不返回）。
        // 没有超时上界时 MainLayout.OnInitializedAsync 会永远卡在「正在加载项目清单…」。
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pm = new FakePm(() => [Project("P", ("p-web", "Web"))], gate: neverRelease.Task);
        var directory = CreateDirectory(pm, requestTimeout: TimeSpan.FromMilliseconds(200));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => directory.GetProjectsAsync());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"超时上界未生效：等待了 {sw.Elapsed}");
    }

    [Fact]
    public async Task Timeout_Should_Be_Cached_As_Failure_So_Repeats_Do_Not_Hit_Pm()
    {
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pm = new FakePm(() => [Project("P", ("p-web", "Web"))], gate: neverRelease.Task);
        var directory = CreateDirectory(pm, requestTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<TimeoutException>(() => directory.GetProjectsAsync());
        // 第二个电路/刷新被失败缓存挡回：不再打 PM、立即抛同因异常（ExceptionDispatchInfo 原样重抛）
        await Assert.ThrowsAnyAsync<TimeoutException>(() => directory.GetProjectsAsync());
        Assert.Equal(1, pm.Calls);
    }

    [Fact]
    public async Task Timeout_Should_Log_Warning_With_Environment_And_Request_Uri()
    {
        // 失败必须落到后端日志：异常向上只被 MainLayout 塞进 UI tooltip，docker logs 没有这条就无线索。
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pm = new FakePm(() => [Project("P", ("p-web", "Web"))], gate: neverRelease.Task);
        var logger = new CapturingLogger<PmProjectDirectory>();
        var directory = CreateDirectory(pm, logger, TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<TimeoutException>(() => directory.GetProjectsAsync());

        var warning = Assert.Single(logger.WarningMessages);
        Assert.Contains("Development", warning);
        Assert.Contains("https://pm.example.com/open-api/projectwithapps/Development", warning);
    }

    [Fact]
    public async Task OperationCanceled_Without_Caller_Cancel_Should_Be_Treated_As_Failure()
    {
        // HttpClient 请求超时抛 TaskCanceledException（OCE 派生），但调用方并未取消——
        // 必须按真实失败处理：记日志 + 进失败缓存，而不是被「调用方取消」过滤器静默放行（那样 SSO/PM 黑洞无痕）。
        var pm = new FakePm(() => throw new TaskCanceledException("http request timeout"));
        var logger = new CapturingLogger<PmProjectDirectory>();
        var directory = CreateDirectory(pm, logger);

        await Assert.ThrowsAsync<TaskCanceledException>(() => directory.GetProjectsAsync());

        var warning = Assert.Single(logger.WarningMessages);
        Assert.Contains("Development", warning);

        // 进了失败缓存：第二次调用立即抛同因异常，不再打 PM
        await Assert.ThrowsAsync<TaskCanceledException>(() => directory.GetProjectsAsync());
        Assert.Equal(1, pm.Calls);
    }

    private static PmProjectDirectory CreateDirectory(FakePm pm, ILogger<PmProjectDirectory>? logger = null, TimeSpan? requestTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPmClient>(pm.Client);
        services.AddSingleton(DispatchProxy.Create<IMasaStackConfig, ConfigProxy>());
        var provider = services.BuildServiceProvider();

        return new PmProjectDirectory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new MemoryCache(new MemoryCacheOptions()),
            logger ?? NullLogger<PmProjectDirectory>.Instance,
            requestTimeout ?? TimeSpan.FromSeconds(5));
    }

    private static ProjectAppsModel Project(string name, params (string Identity, string Name)[] apps) =>
        new(id: 0, name, name, labelCode: "", teamIds: null)
        {
            Apps = [.. apps.Select(a => new AppModel { Identity = a.Identity, Name = a.Name })]
        };

    /// <summary>可编排的假 PM：计数 + 可选阻塞闸门，用于验证单飞。</summary>
    private sealed class FakePm
    {
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public FakePm(Func<List<ProjectAppsModel>> respond, Task? gate = null)
        {
            var service = DispatchProxy.Create<IProjectService, ProjectServiceProxy>();
            var proxy = (ProjectServiceProxy)(object)service;
            proxy.Handler = async () =>
            {
                Interlocked.Increment(ref _calls);
                _firstCallStarted.TrySetResult();
                if (gate is not null)
                {
                    await gate;
                }
                return respond();
            };
            Client = new FakePmClient { ProjectService = service };
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task FirstCallStarted => _firstCallStarted.Task;

        public IPmClient Client { get; }
    }

    private sealed class FakePmClient : IPmClient
    {
        public IEnvironmentService EnvironmentService { get; init; } = null!;
        public IClusterService ClusterService { get; init; } = null!;
        public IProjectService ProjectService { get; init; } = null!;
        public IAppService AppService { get; init; } = null!;
    }

    /// <summary>只实现 <c>GetProjectAppsAsync</c>；其余成员不应被目录服务调用。</summary>
    public class ProjectServiceProxy : DispatchProxy
    {
        public Func<Task<List<ProjectAppsModel>>>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IProjectService.GetProjectAppsAsync)
                ? Handler!()
                : throw new NotSupportedException(targetMethod?.Name);
    }

    /// <summary>目录服务只读 <c>Environment</c>；日志用的 PM 域名走 <c>MASA_STACK</c> 公共配置解析。</summary>
    public class ConfigProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_" + nameof(IMasaStackConfig.Environment) => "Development",
                nameof(IMasaStackConfig.GetValue) when args is ["MASA_STACK"] =>
                    """[{"id":"pm","service":{"domain":"https://pm.example.com/"}}]""",
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    /// <summary>只收集 Information / Warning 级别、已格式化的消息，用于断言日志内容。</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> InformationMessages { get; } = [];

        public List<string> WarningMessages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                InformationMessages.Add(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Warning)
            {
                WarningMessages.Add(formatter(state, exception));
            }
        }
    }
}
