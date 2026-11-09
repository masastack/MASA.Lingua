using System.Text;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.SDK.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// SDK 多语言诊断：状态快照只依赖 SDK 自身运行态（内存字典 / 同步服务 / 配置），
/// 不含任何项目、语种或词条级硬编码，任何宿主接上去都能用。
/// </summary>
public sealed class LinguaDiagnosticsTests
{
    private static (ServiceProvider Provider, ILinguaMemoryStore Store) Build(bool online = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLinguaLocalization(options =>
        {
            options.ProjectCode = "diagnostics-project";
            options.LocalJsonDirectory = Path.Combine(Path.GetTempPath(), $"lingua_diag_{Guid.NewGuid():N}");
            options.ServerEndpoint = online ? "http://localhost:62641" : null;
        });
        services.AddLinguaDiagnostics();

        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ILinguaMemoryStore>());
    }

    [Fact]
    public void MemoryStore_Should_Report_Loaded_Cultures_And_Default_Culture()
    {
        var (provider, store) = Build();
        using var _ = provider;

        store.UpdateCultureFromJsonBytes("zh-CN", Encoding.UTF8.GetBytes("""{"errors":{"UserNotExist":"当前用户不存在"}}"""));
        store.UpdateCultureFromJsonBytes("en-US", Encoding.UTF8.GetBytes("""{"errors":{"UserNotExist":"The current user does not exist"}}"""));
        Assert.Equal(["en-US", "zh-CN"], store.GetLoadedCultures());

        // 同步服务还没跑过：先用 store 的初始回退语种
        Assert.Equal("zh-CN", store.DefaultCulture);
        store.SetDefaultCulture("en-US");
        Assert.Equal("en-US", store.DefaultCulture);
    }

    [Fact]
    public void Status_Should_Report_Offline_Mode_With_Loaded_Entries()
    {
        var (provider, store) = Build();
        using var _ = provider;

        store.UpdateCultureFromJsonBytes("zh-CN", Encoding.UTF8.GetBytes("""{"errors":{"A":"a","B":"b"}}"""));

        var status = provider.GetRequiredService<LinguaStatusProvider>().CreateStatus();

        Assert.Equal("diagnostics-project", status.ProjectCode);
        Assert.Equal("offline", status.Mode);
        Assert.Null(status.ServerEndpoint);
        Assert.Null(status.Version);
        Assert.False(status.ServerEventsConnected);
        Assert.Equal("zh-CN", status.DefaultCulture);
        var culture = Assert.Single(status.Cultures);
        Assert.Equal("zh-CN", culture.Culture);
        Assert.Equal(2, culture.Entries);
        Assert.True(status.Healthy);
    }

    [Fact]
    public void Status_Should_Be_Unhealthy_When_Nothing_Is_Loaded()
    {
        var (provider, _) = Build(online: true);
        using var __ = provider;

        var status = provider.GetRequiredService<LinguaStatusProvider>().CreateStatus();

        Assert.Equal("online", status.Mode);
        Assert.Empty(status.Cultures);
        Assert.False(status.Healthy);
        // 还没有真正同步过：版本必须为空，调用方据此判断「启动后是否就绪」
        Assert.Null(status.Version);
    }

    [Fact]
    public void AddLinguaDiagnostics_Should_Require_Lingua_Registration_First()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddLinguaDiagnostics());
        Assert.Contains("AddLinguaLocalization", ex.Message);
    }

    [Fact]
    public void AddLinguaDiagnostics_Should_Be_Idempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLinguaLocalization(options => options.ProjectCode = "diagnostics-project");

        services.AddLinguaDiagnostics();
        services.AddLinguaDiagnostics();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(LinguaStatusProvider));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                          && descriptor.ImplementationType == typeof(LinguaStartupCheck));
    }
}
