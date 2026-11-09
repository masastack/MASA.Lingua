using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Server.Options;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 项目语种设置：UpdateProjectCulturesAsync 的规范化与校验，
/// 以及语种被移除后静态 {culture}.json 的清理（读取端按 SupportedCultures 拦截）。
/// </summary>
public sealed class ProjectCultureSettingsTests
{
    // --- 仓储：语种配置校验 --------------------------------------------------

    private static async Task<(IServiceProvider Services, string DbPath)> CreateServicesAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_cultures");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider, databaseName);
    }

    private static void Cleanup(string databaseName) => PostgresServer.Drop(databaseName);

    private static async Task SeedProjectAsync(IServiceProvider services, string code)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Projects.Add(new Project
        {
            Code = code,
            DefaultCulture = "zh-CN",
            SupportedCultures = ["zh-CN", "en-US"],
            SharedNamespaceCodes = []
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdateProjectCultures_Should_Normalize_Dedupe_And_Persist()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "cultures-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // en-us 写法映射回项目已有的 en-US；ko-kr / zh_CN 规范化为 ko-KR / zh-CN
            await repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN",
                ["en-us", "ko-kr", "zh_CN", "en-US"]);

            var summary = await repo.GetProjectSummaryAsync("cultures-app");
            Assert.NotNull(summary);
            Assert.Equal(["en-US", "ko-KR", "zh-CN"], summary!.SupportedCultures);
            Assert.Equal("zh-CN", summary.DefaultCulture);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public async Task UpdateProjectCultures_Should_Reject_Unknown_Or_Invalid_Codes_And_Keep_Old_Config()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "cultures-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // 不在标准语种表、也不在项目已有语种里：拒绝，且不悄悄变成新语种
            await Assert.ThrowsAsync<LinguaValidationException>(() =>
                repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN", ["zh-CN", "xx-YY"]));
            // 格式不合法
            await Assert.ThrowsAsync<LinguaValidationException>(() =>
                repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN", ["zh-CN", "not a code"]));

            var summary = await repo.GetProjectSummaryAsync("cultures-app");
            Assert.NotNull(summary);
            Assert.Equal(["zh-CN", "en-US"], summary!.SupportedCultures); // 失败的保存不落库
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public async Task UpdateProjectCultures_Should_Reject_Empty_List_And_Default_Not_In_List()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "cultures-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // 列表不可清空
            await Assert.ThrowsAsync<LinguaValidationException>(() =>
                repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN", []));
            // 默认语种必须在列表内（不能把默认语种直接移除）
            await Assert.ThrowsAsync<LinguaValidationException>(() =>
                repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN", ["en-US"]));
            // 默认语种不在提交列表里
            await Assert.ThrowsAsync<LinguaValidationException>(() =>
                repo.UpdateProjectCulturesAsync("cultures-app", "ja-JP", ["zh-CN", "en-US"]));

            var summary = await repo.GetProjectSummaryAsync("cultures-app");
            Assert.NotNull(summary);
            Assert.Equal(["zh-CN", "en-US"], summary!.SupportedCultures);
            Assert.Equal("zh-CN", summary.DefaultCulture);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public async Task UpdateProjectCultures_Should_Allow_Changing_Default_Then_Removing_Old_Default()
    {
        var (services, db) = await CreateServicesAsync();
        try
        {
            await SeedProjectAsync(services, "cultures-app");
            var repo = services.GetRequiredService<ILinguaRepository>();

            // 先把默认语种改成 en-US（同时保留 zh-CN）
            await repo.UpdateProjectCulturesAsync("cultures-app", "en-US", ["zh-CN", "en-US", "ja-JP"]);
            var summary = await repo.GetProjectSummaryAsync("cultures-app");
            Assert.NotNull(summary);
            Assert.Equal("en-US", summary!.DefaultCulture);

            // 默认语种已经改掉，现在可以移除旧默认语种 zh-CN
            await repo.UpdateProjectCulturesAsync("cultures-app", "en-US", ["en-US", "ja-JP"]);
            summary = await repo.GetProjectSummaryAsync("cultures-app");
            Assert.NotNull(summary);
            Assert.Equal(["en-US", "ja-JP"], summary!.SupportedCultures);
            Assert.Equal("en-US", summary.DefaultCulture);
        }
        finally
        {
            Cleanup(db);
        }
    }

    // --- 发布器：移除语种后的静态产物清理 ------------------------------------

    private sealed class TestEnv(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MASA.Lingua.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class Harness : IDisposable
    {
        public required ILinguaRepository Repo { get; init; }
        public required string DbPath { get; init; }
        public required string Root { get; init; }

        public StaticBundlePublisher NewPublisher() => new(
            Repo, // IProjectCatalog
            Repo, // IGlossaryCatalog
            Repo, // IPublishArchive
            new BundleCompiler(),
            new LinguaSseBroadcaster(),
            new TestEnv(Root),
            new ConfigurationBuilder().Build(),
            NullLogger<StaticBundlePublisher>.Instance);

        public string BundlePath(string culture) => Path.Combine(Root, "App_Data", "i18n", "cultures-app", $"{culture}.json");

        public string ConnectionString => PostgresServer.NewConnectionString(DbPath);

        public void Dispose()
        {
            PostgresServer.Drop(DbPath);
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<Harness> CreateHarnessAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_culture_pub");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();

        var root = Path.Combine(Path.GetTempPath(), $"lingua_culture_pub_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new Harness { Repo = provider.GetRequiredService<ILinguaRepository>(), DbPath = databaseName, Root = root };
    }

    [Fact]
    public async Task RemovePublishedCultures_Should_Delete_Files_And_Stop_Serving_Removed_Culture()
    {
        var harness = await CreateHarnessAsync();
        using var _ = harness;

        var dbFactory = new ServiceCollection()
            .AddLinguaEfCoreStorage(harness.ConnectionString)
            .BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<LinguaDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Projects.Add(new Project
            {
                Code = "cultures-app",
                DefaultCulture = "zh-CN",
                SupportedCultures = ["zh-CN", "en-US"],
                SharedNamespaceCodes = []
            });
            await db.SaveChangesAsync();
        }

        // 模拟 en-US 已发布过：磁盘上有静态产物
        Directory.CreateDirectory(Path.GetDirectoryName(harness.BundlePath("en-US"))!);
        await File.WriteAllTextAsync(harness.BundlePath("en-US"), """{"order":{"title":"Title"}}""");
        Assert.True(File.Exists(harness.BundlePath("en-US")));

        // 从项目支持列表移除 en-US，然后清理产物
        await harness.Repo.UpdateProjectCulturesAsync("cultures-app", "zh-CN", ["zh-CN"]);
        harness.NewPublisher().RemovePublishedCultures("cultures-app", ["en-US"]);

        Assert.False(File.Exists(harness.BundlePath("en-US"))); // 静态文件已删除

        // 即便文件残留，读取端也按 SupportedCultures 拦截：返回 null（调用方回 404）
        var bundle = await harness.NewPublisher().GetOrCompileBundleAsync("cultures-app", "en-US");
        Assert.Null(bundle);

        // 移除操作是幂等的：再删一次也不抛异常
        harness.NewPublisher().RemovePublishedCultures("cultures-app", ["en-US"]);
    }
}
