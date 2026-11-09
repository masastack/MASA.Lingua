using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace MASA.Lingua.Server.Startup;

/// <summary>
/// 登录/登出端点：浏览器导航到这里会 302 到公司 MASA Auth，
/// 未登录访问管理页时由 <c>RedirectToLogin</c> 组件强制跳转过来。
/// </summary>
public static class AuthEndpoints
{
    public static void MapLinguaAuthEndpoints(this WebApplication app)
    {
        // 匿名：登录入口本身当然不能要求已登录
        app.MapGet("/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrlSanitizer.Sanitize(returnUrl) },
                [OpenIdConnectDefaults.AuthenticationScheme]))
            .AllowAnonymous();

        // 登出仅对已登录用户开放（避免匿名触发不必要的 SSO 往返）
        app.MapPost("/logout", (HttpContext http, string? returnUrl) =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = ReturnUrlSanitizer.Sanitize(returnUrl) },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]))
            .RequireAuthorization(AuthRegistration.AdminPolicy);
    }
}
