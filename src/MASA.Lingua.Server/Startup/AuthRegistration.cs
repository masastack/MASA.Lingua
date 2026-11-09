using MASA.Lingua.Server.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;

namespace MASA.Lingua.Server.Startup;

/// <summary>
/// 管理后台鉴权装配：Cookie（会话）+ OIDC（challenge → 公司 MASA Auth）两方案并存。
/// 分发链路（/i18n、/api/v1 的机器调用）不走这里，仍用项目只读密钥/写密钥。
/// </summary>
public static class AuthRegistration
{
    /// <summary>管理后台唯一授权策略：必须已完成 SSO 登录。</summary>
    public const string AdminPolicy = "LinguaAdmin";

    public static void AddLinguaAuth(this IServiceCollection services, AuthOptions auth, string authority)
    {
        if (auth.ValidationError is { } error)
        {
            throw new InvalidOperationException(error);
        }

        // DataProtection 密钥环在 Program.cs 注册：持久化到 DataProtection:KeysDirectory
        // （镜像内置 /data/dataprotection，部署挂卷），含相对路径与可写性的启动期 fail-fast 探测。

        // 刻意不做 access token / refresh token 的静默刷新，也不申请 offline_access：
        // 本站点没有任何代码使用登录用户的 access token（PM 外呼走 client_credentials 的应用身份，见 PmClientTokenGenerater），
        // 会话时长完全由下面的认证 cookie（SessionLifetime，滑动续期）决定，与 access token 是否过期无关。
        // MASA Auth（IdentityServer4）的 refresh token 默认「一次性」：凡是去刷新，旧 token 立即作废，
        // 任何一个仍带着旧 cookie 的请求再用它都会得到 400 invalid_grant。没有消费者的刷新只会制造这类失败。
        // 将来若真要以用户身份外呼，应使用成熟的令牌管理组件，而不是在 cookie 校验事件里手写刷新。

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // 默认挑战也必须走 Cookie：未登录访问管理页时只需 302 到 /login，
                // 由 /login 端点再显式 challenge OIDC。若这里直接给 OIDC，任何未登录请求都会先去拉
                // IdP 元数据（IdP 不可达时变成 500，而不是登录跳转）。
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = ".lingua.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax; // OIDC 回调是顶层 GET 重定向
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = auth.SessionLifetime;
                options.SlidingExpiration = true;
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = auth.ClientId!;
                options.ClientSecret = auth.ClientSecret; // 空 = 公共客户端
                options.ResponseType = "code";
                // 无条件启用 PKCE：机密客户端同样适用（OAuth 2.1 / 安全 BCP 要求）
                options.UsePkce = true;
                // 保留：登出要带 id_token_hint（来自这里存下的 id_token），IdentityServer4 才会按它校验并跳回 PostLogoutRedirectUri
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false; // 保留原始 claim（顶栏直接读 name）
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                options.TokenValidationParameters.ValidateAudience = auth.ValidateAudience;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.Scope.Clear();
                foreach (var scope in auth.Scopes)
                {
                    options.Scope.Add(scope);
                }
                options.Events = new OpenIdConnectEvents
                {
                    OnRemoteFailure = context =>
                    {
                        // 回调处理失败的统一出口：最典型的是 state 解不开（AuthenticationFailureException）——
                        // 密钥环更换后浏览器带回的旧 state / 旧 cookie、或登录页停留过久 state 过期都会走到这里。
                        // 默认行为是抛 500 把用户死在错误页；302 回 /login 重新发起一次干净的 OIDC 往返即可自愈。
                        // returnUrl 一律不透传（failure 上下文里的消息不可信），登录成功后回落首页。
                        context.Response.Redirect("/login");
                        context.HandleResponse();
                        return Task.CompletedTask;
                    }
                };
            });

        // antiforgery cookie 显式命名：默认名（.AspNetCore.Antiforgery.*）在同域名（不同端口也算同域，
        // cookie 不隔离端口）部署多个 ASP.NET 应用时会互相覆盖，表现为 "The antiforgery token could not be decrypted"。
        // 与认证 cookie 的 .lingua.auth 命名风格统一。
        services.AddAntiforgery(options => options.Cookie.Name = ".lingua.antiforgery");

        // 策略显式绑定 Cookie 方案：未登录时 302 到 LoginPath，由 /login 端点再发起 OIDC challenge。
        // 若用默认 OIDC 挑战，任何未登录请求都会先去拉 IdP 元数据（IdP 不可达时变成 500，而不是登录跳转）。
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());
    }
}
