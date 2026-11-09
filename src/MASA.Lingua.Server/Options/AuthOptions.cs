namespace MASA.Lingua.Server.Options;

/// <summary>
/// 管理后台鉴权配置：SSO 强制登录（OIDC 客户端），无匿名模式。
/// <para>
/// Authority（SSO 站点根地址）不在这里：它由 <c>Startup/MasaStackStartup</c> 在启动期从
/// DCC（MasaStackConfig.GetSsoDomain）解析，测试/离线调试可用 <c>Auth:Authority</c> 覆盖。
/// 本类型只承载客户端身份与流程参数。
/// </para>
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>SSO 客户端标识（MASA Auth「客户端管理」注册，如 lingua-web）。必填。</summary>
    public string? ClientId { get; set; }

    /// <summary>SSO 客户端密钥；留空时按 PKCE 公共客户端发起授权码流程。</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// 请求的 scope。默认 openid / profile。
    /// <para>
    /// 刻意不含 offline_access：本站点不使用登录用户的 access token，没有理由让 SSO 签发 refresh token
    /// （MASA Auth 的 refresh token 默认一次性，没有消费者的签发只会白白占用 SSO 的授权存储）。
    /// </para>
    /// <para>
    /// 注意：配置绑定到 <see cref="List{T}"/> 是「追加」而非「覆盖」——此处默认值与 appsettings 里的 Scopes 会合并，
    /// 所以默认值与 appsettings.json 必须一起改，只改一处不会生效。
    /// </para>
    /// </summary>
    public List<string> Scopes { get; set; } = ["openid", "profile"];

    /// <summary>是否要求 HTTPS 元数据（公司内网 SSO 常为 http，默认 false）。</summary>
    public bool RequireHttpsMetadata { get; set; }

    /// <summary>是否校验 Audience（默认 false）。</summary>
    public bool ValidateAudience { get; set; }

    /// <summary>Cookie 会话有效期（默认 12 小时，滑动续期）。</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    internal string? ValidationError =>
        string.IsNullOrWhiteSpace(ClientId)
            ? "Auth:ClientId 未配置 —— 管理后台强制 SSO 登录（无匿名模式）。请在 MASA Auth 注册客户端后配置 ClientId。"
            : Scopes.Count == 0
                ? "Auth:Scopes 不能为空（至少包含 openid）。"
                : null;
}
