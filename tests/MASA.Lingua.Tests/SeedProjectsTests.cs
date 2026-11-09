using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Compilation;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 内置示例项目：MASA.Auth 的 auth-web-staging / auth-sso-staging 必须把真实的服务端错误码词条完整落库，
/// 且能按项目发布策略合成出可发布的语言包（不只是「表里有行」）。
/// <para>
/// 关键契约：MASA.Auth 的调用形态是<b>平铺 key</b>（<c>I18n.T("UserNotExist")</c> /
/// <c>UserFriendlyException(errorCode: "UserNotExist")</c>），所以这些词条必须落在<b>项目根命名空间</b>，
/// 发布成扁平根 key。放进 <c>errors</c> 命名空间会发布成 <c>{"errors":{"UserNotExist":…}}</c>，
/// 平铺 key 永远命不中——平台上改词也就不会对业务端生效。
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SeedProjectsTests
{
    private static async Task<(ILinguaRepository Repository, string DatabaseName)> CreateSeededRepositoryAsync()
    {
        var databaseName = PostgresServer.NewDatabaseName("lingua_seed_auth");
        var services = new ServiceCollection();
        services.AddLinguaEfCoreStorage(PostgresServer.NewConnectionString(databaseName));
        var provider = services.BuildServiceProvider();
        await provider.InitializeLinguaDatabaseAsync();
        return (provider.GetRequiredService<ILinguaRepository>(), databaseName);
    }

    [Fact]
    public async Task BuiltInAuthProjects_Should_Be_Seeded_With_Real_Error_Messages()
    {
        var (repo, databaseName) = await CreateSeededRepositoryAsync();
        try
        {
            var admin = await repo.GetProjectByCodeAsync("auth-web-staging");
            Assert.NotNull(admin);
            Assert.Equal("zh-CN", admin!.DefaultCulture);
            Assert.Equal(["zh-CN", "en-US", "ja-JP", "ru-RU"], admin.SupportedCultures);

            // 词条落在项目根命名空间（空 Code），这是「平铺 key 能命中」的前提
            var adminRoot = Assert.Single(admin.Namespaces, n => n.Code == "");
            Assert.Equal(101, adminRoot.Entries.Count);
            Assert.Equal("当前角色不存在", adminRoot.Entries.Single(e => e.Key == "RoleNotExist").SourceText);

            // 项目自有命名空间里不应再有 errors：词条全在根，避免与全局共享 errors 混淆
            Assert.DoesNotContain(admin.Namespaces, n => n.Code == "errors");

            // 真实词条不含「未翻译即待复核」的历史包袱：全部已审核
            Assert.All(
                adminRoot.Entries.SelectMany(e => e.Translations),
                translation => Assert.Equal(TranslationStatus.Approved, translation.Status));

            var sso = await repo.GetProjectByCodeAsync("auth-sso-staging");
            Assert.NotNull(sso);
            var ssoRoot = Assert.Single(sso!.Namespaces, n => n.Code == "");
            Assert.Equal(16, ssoRoot.Entries.Count);
            Assert.Equal("必须提供用户名", ssoRoot.Entries.Single(e => e.Key == "MustProviderUserName").SourceText);

            // 共享层仍在：项目继承 common/errors（发布产物里作为子对象存在，与根级裸 key 互不干扰）
            Assert.Equal(["common", "errors"], admin.SharedNamespaceCodes);
            var shared = await repo.GetSharedNamespacesAsync();
            Assert.Equal(2, shared.Count);
            Assert.Equal(4, shared.Single(n => n.Code == "common").Entries.Count);
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    [Fact]
    public async Task BuiltInAuthProjects_Should_Produce_Publishable_Layers()
    {
        var (repo, databaseName) = await CreateSeededRepositoryAsync();
        try
        {
            var layers = await repo.BuildTranslationLayersAsync("auth-web-staging", "en-US");
            var items = layers.SelectMany(l => l.Items).ToList();

            // 根命名空间（Namespace == ""）下的平铺 key
            Assert.Contains(items, i => i.Namespace == "" && i.Key == "UserNotExist" && i.Value == "The current user does not exist");
            // 共享命名空间继承仍然生效（common.ok 来自全局共享层）
            Assert.Contains(items, i => i.Namespace == "common" && i.Key == "ok" && i.Value == "Confirm");

            var ssoLayers = await repo.BuildTranslationLayersAsync("auth-sso-staging", "zh-CN");
            Assert.Contains(
                ssoLayers.SelectMany(l => l.Items),
                i => i.Namespace == "" && i.Key == "MustProviderUserName" && i.Value == "必须提供用户名");

            // 认证服务的诊断端点按「探针词条是否在所有语种下都能取到」判断可用性，这里守住这两个探针
            var probes = new (string Project, string Key)[]
            {
                ("auth-web-staging", "UserNotExist"),
                ("auth-sso-staging", "MustProviderUserName")
            };

            foreach (var (project, key) in probes)
            {
                foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP", "ru-RU" })
                {
                    var probeItems = (await repo.BuildTranslationLayersAsync(project, culture)).SelectMany(l => l.Items);
                    Assert.Contains(probeItems, i => i.Namespace == "" && i.Key == key && !string.IsNullOrWhiteSpace(i.Value));
                }
            }
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }

    /// <summary>
    /// 端到端护栏：把发布产物交给真实的 SDK 内存字典，用 MASA 的<b>平铺 key</b>取值。
    /// 这条断言直接模拟 <c>I18n.T("RoleNotExist")</c>，是「平台词条能被业务端取到」的唯一有效证明。
    /// </summary>
    [Fact]
    public async Task Published_Bundles_Should_Be_Reachable_By_Flat_Keys()
    {
        var (repo, databaseName) = await CreateSeededRepositoryAsync();
        try
        {
            var compiler = new BundleCompiler();
            var localDir = Path.Combine(Path.GetTempPath(), $"lingua_seed_flat_{Guid.NewGuid():N}");
            Directory.CreateDirectory(localDir);

            try
            {
                foreach (var culture in new[] { "zh-CN", "en-US", "ja-JP", "ru-RU" })
                {
                    var layers = await repo.BuildTranslationLayersAsync("auth-web-staging", culture);
                    var bundle = compiler.Compile("auth-web-staging", culture, layers);
                    await File.WriteAllBytesAsync(
                        Path.Combine(localDir, $"{culture}.json"),
                        bundle.NestedJsonUtf8Bytes.ToArray());
                }

                var store = new LinguaFrozenMemoryStore();
                store.LoadFromLocalDirectory(localDir);
                store.SetDefaultCulture("zh-CN");

                // 平台配置的默认语种已同步
                Assert.Equal("zh-CN", store.DefaultCulture);

                // 1) 平铺 key 在四个语种下都能命中（= MASA I18n.T("RoleNotExist") 的真实调用形态）
                store.AssertFlatValue("zh-CN", "RoleNotExist", "当前角色不存在");
                store.AssertFlatValue("en-US", "RoleNotExist", "The current role does not exist");
                Assert.True(store.TryGetValue("ja-JP", "RoleNotExist", out var ja) && !string.IsNullOrWhiteSpace(ja));
                Assert.True(store.TryGetValue("ru-RU", "RoleNotExist", out var ru) && !string.IsNullOrWhiteSpace(ru));

                // SsoErrorCodes 的平铺 key 同理
                var ssoLayers = await repo.BuildTranslationLayersAsync("auth-sso-staging", "zh-CN");
                var ssoBundle = compiler.Compile("auth-sso-staging", "zh-CN", ssoLayers);
                var ssoStore = new LinguaFrozenMemoryStore();
                ssoStore.UpdateCultureFromJsonBytes("zh-CN", ssoBundle.NestedJsonUtf8Bytes.Span);
                Assert.True(ssoStore.TryGetValue("zh-CN", "MustProviderUserName", out var ssoValue));
                Assert.Equal("必须提供用户名", ssoValue);

                // 2) 共享命名空间的词条仍按「命名空间.key」取，且不与根级裸 key 冲突
                Assert.True(store.TryGetValue("zh-CN", "common.ok", out var ok));
                Assert.Equal("确定", ok);
                Assert.True(store.TryGetValue("zh-CN", "errors.networkTimeout", out var timeout));
                Assert.False(string.IsNullOrWhiteSpace(timeout));
            }
            finally
            {
                try { Directory.Delete(localDir, recursive: true); } catch (IOException) { }
            }
        }
        finally
        {
            PostgresServer.Drop(databaseName);
        }
    }
}

internal static class LinguaStoreAssertions
{
    /// <summary>平铺 key 必须精确命中且等于期望文案（避免只判「不为空」漏掉串语种/串 key）。</summary>
    public static void AssertFlatValue(this ILinguaMemoryStore store, string culture, string key, string expected)
    {
        Assert.True(store.TryGetValue(culture, key, out var value), $"{culture} 下取不到平铺 key {key}");
        Assert.Equal(expected, value);
    }
}
