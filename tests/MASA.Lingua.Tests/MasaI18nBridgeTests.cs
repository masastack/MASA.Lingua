using System.Globalization;
using System.Text;
using Masa.BuildingBlocks.Globalization.I18n;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.SDK.AspNetCore;
using MASA.Lingua.Server.Services;
using MASA.Lingua.Storage.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// MASA.Lingua.SDK.MasaI18n 桥接包的端到端测试：
/// 真实 Lingua 服务端（内存宿主）→ AddLinguaMasaI18n 注册的客户端 → MASA II18n 取词 →
/// 平台改词并发布 → 重新同步 → 新文案立即生效；本地 JSON 只在 Lingua 没有该 Key 时兜底。
/// </summary>
public sealed class MasaI18nBridgeTests
{
    [Fact]
    public async Task AddLinguaMasaI18n_Should_Resolve_Through_MasaI18n_And_HotUpdate()
    {
        using var factory = new IsolatedLinguaFactory();
        var endpoint = factory.Server.BaseAddress.ToString().TrimEnd('/');

        // 1. 服务端：建项目 + 导入词条 + 发布（发布包含 zh-CN 与 en-US 两个语种）
        using (var scope = factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            // 独立项目编码：auth-web-staging 已是内置种子项目，这里要的是可控的最小词条集
            db.Projects.Add(new Project
            {
                Code = "masa-i18n-bridge",
                DefaultCulture = "zh-CN",
                SupportedCultures = ["zh-CN", "en-US"],
            });
            await db.SaveChangesAsync();

            var repo = scope.ServiceProvider.GetRequiredService<ILinguaRepository>();
            var publisher = scope.ServiceProvider.GetRequiredService<StaticBundlePublisher>();

            // 注意：LocalOnlyKey 故意不导入 Lingua，专门用来验证本地 JSON 兜底路径
            const string zhImport = """{"RoleNotExist":"当前角色不存在","UserFrozen":"当前用户已被冻结"}""";
            await repo.ImportJsonBundleAsync("masa-i18n-bridge", "zh-CN", Encoding.UTF8.GetBytes(zhImport), "");

            const string enImport = """{"RoleNotExist":"The role does not exist"}""";
            await repo.ImportJsonBundleAsync("masa-i18n-bridge", "en-US", Encoding.UTF8.GetBytes(enImport), "");

            await publisher.PublishProjectAsync("masa-i18n-bridge", "bridge test publish");
        }

        // 2. 客户端：桥接注册（本地兜底目录里的 RoleNotExist 是旧文案，同步后应被平台版本覆盖）
        var localDir = Path.Combine(Path.GetTempPath(), $"lingua_bridge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(localDir, "zh-CN.json"),
                """{"RoleNotExist":"本地兜底-旧","LocalOnlyKey":"仅本地兜底"}""");
            // 本地文件同时定义支持语种集合（与生产一致：csproj 拷贝全部语种 JSON）
            await File.WriteAllTextAsync(
                Path.Combine(localDir, "en-US.json"),
                """{"RoleNotExist":"Local fallback stale"}""");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLinguaMasaI18n(o =>
            {
                o.ProjectCode = "masa-i18n-bridge";
                o.ServerEndpoint = endpoint;
                o.LocalJsonDirectory = localDir;
                o.PollInterval = TimeSpan.Zero;   // 测试中手动触发同步
                o.EnableServerEvents = false;
            });

            await using var provider = services.BuildServiceProvider();

            var previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            try
            {
                var i18n = provider.GetRequiredService<II18n<DefaultResource>>();

                // 3. 同步前：Lingua 还没同步，取到的是本地兜底文件里的旧文案
                Assert.Equal("本地兜底-旧", i18n.T("RoleNotExist"));

                // 4. 手动触发一次同步（等价于轮询 / SSE 命中后的行为）。
                //    WebApplicationFactory 是内存 TestServer（无真实 TCP 端口），注入其 HttpClient 走内存传输。
                var sync = provider.GetRequiredService<LinguaJsonSyncService>();
                Assert.True(await sync.SyncAllAsync(factory.CreateClient()));

                // 5. 同步后：MASA I18n 取到 Lingua 发布的文案 —— 桥接生效
                Assert.Equal("当前角色不存在", i18n.T("RoleNotExist"));

                // 6. 仅存在于本地文件的 Key：Lingua 没有，走本地 JSON 兜底
                Assert.Equal("仅本地兜底", i18n.T("LocalOnlyKey"));

                // 7. 热更新：平台改词并重新发布 → 再同步 → 下一笔取词立即生效（无缓存、无需重启）
                using (var scope = factory.Services.CreateScope())
                {
                    var repo = scope.ServiceProvider.GetRequiredService<ILinguaRepository>();
                    var publisher = scope.ServiceProvider.GetRequiredService<StaticBundlePublisher>();
                    await repo.UpsertEntryAsync(
                        "masa-i18n-bridge", "", "RoleNotExist", "当前角色不存在",
                        null, null, new Dictionary<string, string> { ["zh-CN"] = "角色不存在-新文案" });
                    await publisher.PublishProjectAsync("masa-i18n-bridge", "bridge test hot update");
                }

                Assert.True(await sync.SyncAllAsync(factory.CreateClient()));
                Assert.Equal("角色不存在-新文案", i18n.T("RoleNotExist"));

                // 8. en-US 同步到平台版本
                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                Assert.Equal("The role does not exist", i18n.T("RoleNotExist"));
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }
        finally
        {
            try { Directory.Delete(localDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 项目自有词条在<b>根命名空间</b>、同时继承共享命名空间：这是 Auth 两个真实项目的形态。
    /// 平铺 key（<c>I18n.T("UserNotExist")</c>）必须命中根级词条，共享层仍按 <c>common.ok</c> 取。
    /// </summary>
    [Fact]
    public async Task AddLinguaMasaI18n_Should_Resolve_Root_Namespace_Entries_By_Flat_Key()
    {
        using var factory = new IsolatedLinguaFactory();

        using (var scope = factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LinguaDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            // 继承全局共享 common（种子已有 4 条），自有词条写到项目根（defaultNamespace 留空）
            db.Projects.Add(new Project
            {
                Code = "root-namespace-bridge",
                DefaultCulture = "zh-CN",
                SupportedCultures = ["zh-CN", "en-US"],
                SharedNamespaceCodes = ["common"],
            });
            await db.SaveChangesAsync();

            var repo = scope.ServiceProvider.GetRequiredService<ILinguaRepository>();
            var publisher = scope.ServiceProvider.GetRequiredService<StaticBundlePublisher>();

            const string zhImport = """{"UserNotExist":"当前user不存在","MustProviderUserName":"必须提供用户名"}""";
            await repo.ImportJsonBundleAsync("root-namespace-bridge", "zh-CN", Encoding.UTF8.GetBytes(zhImport), "");
            const string enImport = """{"UserNotExist":"The current user does not exist"}""";
            await repo.ImportJsonBundleAsync("root-namespace-bridge", "en-US", Encoding.UTF8.GetBytes(enImport), "");

            await publisher.PublishProjectAsync("root-namespace-bridge", "root namespace bridge");
        }

        var localDir = Path.Combine(Path.GetTempPath(), $"lingua_root_{Guid.NewGuid():N}");
        Directory.CreateDirectory(localDir);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLinguaMasaI18n(o =>
            {
                o.ProjectCode = "root-namespace-bridge";
                o.ServerEndpoint = factory.Server.BaseAddress.ToString().TrimEnd('/');
                o.LocalJsonDirectory = localDir;
                o.PollInterval = TimeSpan.Zero;
                o.EnableServerEvents = false;
            });

            await using var provider = services.BuildServiceProvider();

            var previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
            try
            {
                var sync = provider.GetRequiredService<LinguaJsonSyncService>();
                Assert.True(await sync.SyncAllAsync(factory.CreateClient()));

                var i18n = provider.GetRequiredService<II18n<DefaultResource>>();

                // 平铺 key 命中根命名空间词条 —— 这正是 UserFriendlyExceptionCodes / SsoErrorCodes 的调用形态
                Assert.Equal("当前user不存在", i18n.T("UserNotExist"));
                Assert.Equal("必须提供用户名", i18n.T("MustProviderUserName"));

                // 共享命名空间继承仍然生效，且命名空间形式的 key 照常可用
                Assert.Equal("确定", i18n.T("common.ok"));

                // 写盘产物必须是「根级裸 key + common 子对象」，这是「平铺 key 能命中」的物理证据
                var publishedZh = await File.ReadAllTextAsync(Path.Combine(localDir, "zh-CN.json"));
                Assert.Contains("\"UserNotExist\"", publishedZh);
                Assert.DoesNotContain("\"errors\"", publishedZh);

                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                Assert.Equal("The current user does not exist", i18n.T("UserNotExist"));
            }
            finally
            {
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        }
        finally
        {
            try { Directory.Delete(localDir, recursive: true); } catch (IOException) { }
        }
    }
}
