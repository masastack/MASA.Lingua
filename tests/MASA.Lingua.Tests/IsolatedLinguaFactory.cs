using MASA.Lingua.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace MASA.Lingua.Tests;

/// <summary>每个测试一份独立数据库与发布目录：并行运行的测试类之间不会互相污染，也不碰开发库与仓库里的 wwwroot。</summary>
internal sealed class IsolatedLinguaFactory(bool configureAuth = true) : WebApplicationFactory<Program>
{
    public string DatabaseName { get; } = PostgresServer.NewDatabaseName("lingua_iso");

    public string ConnectionString => PostgresServer.NewConnectionString(DatabaseName);

    public string WebRoot { get; } = Path.Combine(Path.GetTempPath(), $"lingua_iso_root_{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 测试宿主固定为生产语义：站点里「仅生产」的行为（如响应压缩）在测试里必须生效，
        // 不能随跑测试的机器环境变量（ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT）漂移。
        builder.UseSetting(WebHostDefaults.EnvironmentKey, Environments.Production);

        Directory.CreateDirectory(WebRoot);
        builder.UseSetting("Lingua:Database:ConnectionString", ConnectionString);
        builder.UseSetting(WebHostDefaults.WebRootKey, WebRoot);
        // 发布产物默认落在 ContentRoot/App_Data（那是仓库里的服务端项目目录），测试必须指到临时目录
        builder.UseSetting(StaticBundlePublisher.BundleDirectoryKey, Path.Combine(WebRoot, "i18n"));
        // 管理后台强制 SSO：测试只验证「未登录被拦」，不需要真实 IdP。
        // OIDC 元数据在 challenge 时才拉取，这里给一个不可达地址即可。
        // Auth:Authority 是启动期的显式覆盖分支：设了它就完全不碰 DCC/Redis，
        // 这是测试宿主能在无 DCC 环境启动的唯一途径。
        if (configureAuth)
        {
            builder.UseSetting("Auth:Authority", "https://sso.invalid");
            builder.UseSetting("Auth:ClientId", "lingua-tests");
            builder.UseSetting("Auth:RequireHttpsMetadata", "false");
        }
        else
        {
            // 反向用例：既没有 Auth:Authority，DCC 也被清空 —— 必须在启动期 fail fast
            builder.UseSetting("DCC:ManageServiceAddress", "");
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        PostgresServer.Drop(DatabaseName);
        try { Directory.Delete(WebRoot, recursive: true); } catch (IOException) { }
    }
}
