using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 管理后台鉴权装配：SSO 强制登录（未配置即启动失败）、未登录不能进入管理页、
/// 分发链路（机器调用）必须保持匿名。
/// </summary>
public sealed class AuthTests
{
    [Fact]
    public async Task Anonymous_Page_Should_Redirect_To_Login()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 未登录访问管理页必须 302 到 /login（带 returnUrl）。
        // 这条同时守住 AuthorizeRouteView 的级联状态：缺 AddCascadingAuthenticationState 时它会抛异常 → 500。
        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.Redirect,
            $"期望 302 到 /login，实际 {(int)response.StatusCode}。响应体：{body}");
        var location = response.Headers.Location!.ToString();
        Assert.Contains("/login", location);
        // Cookie 中间件生成的参数名是 ReturnUrl，/login 端点的参数绑定大小写不敏感
        Assert.Contains("returnUrl=", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_Authority_Should_Fail_Fast()
    {
        using var factory = new IsolatedLinguaFactory(configureAuth: false);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        // 既没有 Auth:Authority 覆盖，也没有 DCC 连接 —— 启动期必须给出可执行的失败信息
        Assert.Contains("ManageServiceAddress", error.ToString());
    }

    [Fact]
    public async Task Remote_Failure_Should_Redirect_To_Login_Not_500()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // OIDC 回调携带 error 参数（IdP 拒绝 / state 失配等远端失败）：OnRemoteFailure 必须
        // 302 回 /login 自愈，而不是默认行为——异常上抛变 500，用户死在错误页。
        // 该失败在密钥环更换后的第一轮部署必然出现（浏览器带回旧 state），此前真实发生过。
        using var response = await client.GetAsync("/signin-oidc?error=access_denied&error_description=test");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public void Antiforgery_Cookie_Should_Have_Explicit_Name()
    {
        using var factory = new IsolatedLinguaFactory();

        // 默认 antiforgery cookie 名在同域名（cookie 不隔离端口）多应用部署下会互相覆盖，
        // 表现为 "The antiforgery token could not be decrypted"。必须与 .lingua.auth 一样显式命名。
        var options = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        Assert.Equal(".lingua.antiforgery", options.Cookie.Name);
    }

    [Fact]
    public async Task Login_Should_Not_Request_Offline_Access()
    {
        await using var factory = new IsolatedLinguaFactory();

        // 走真实宿主：appsettings.json + AuthOptions 默认值的合并结果（List 绑定是「追加」，任何一处带上 offline_access 都会生效）。
        // 本站点不使用登录用户的 access token，申请 offline_access 只会让 SSO（MASA Auth，refresh token 默认一次性）
        // 签发一个没人用的 refresh token —— 之前正是它被每个请求拿去换新，引发 400 invalid_grant 刷屏。
        var oidc = factory.Services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        Assert.DoesNotContain("offline_access", oidc.Scope);
        Assert.Contains("openid", oidc.Scope);
        Assert.Contains("profile", oidc.Scope);
        // 登出需要 id_token_hint（来自 SaveTokens 存下的 id_token），否则 SSO 不会跳回 PostLogoutRedirectUri
        Assert.True(oidc.SaveTokens);
    }

    [Fact]
    public async Task Sse_Should_Reject_Unknown_Project()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 「查不到项目就放行」是旧行为：现在未知项目一律 404，大小写变体也无法订阅别人的事件。
        using var response = await client.GetAsync("/api/v1/stream?project=SHOP-VUE");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Distribution_Endpoints_Should_Stay_Anonymous()
    {
        await using var factory = new IsolatedLinguaFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // 分发链路是机器调用，不能被 SSO 拦住（改用项目密钥）
        using var response = await client.GetAsync("/api/v1/projects/shop-vue/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
