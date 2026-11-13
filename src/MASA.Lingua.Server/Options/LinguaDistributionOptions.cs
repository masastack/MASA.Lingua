namespace MASA.Lingua.Server.Options;

/// <summary>
/// 分发端点（/i18n 与 /api/v1/*）的访问控制选项（配置节 <c>Lingua:Distribution</c>）。
/// <para>
/// 读端点永远匿名（SDK / CLI / 浏览器直读发布产物）。写类端点默认匿名——内网受控网段部署的既有默认，
/// 见 docs/integration/README.md 的「访问策略」；配置 <see cref="WriteApiKey"/> 后，
/// 创建 / 删除 / 导入 / 导出必须携带 <c>X-Lingua-Key</c> 请求头且与密钥匹配（缺失 401、不匹配 403），
/// 防止端口误暴露公网后被任意写入。
/// </para>
/// </summary>
public sealed class LinguaDistributionOptions
{
    public const string SectionName = "Lingua:Distribution";

    /// <summary>
    /// 写类端点（POST/DELETE /api/v1/projects、import-json、export-zip）的共享 API 密钥。
    /// 留空 = 匿名放行（默认，内网部署）；配置后由 LinguaDistributionEndpoints 统一校验（常量时间比较）。
    /// </summary>
    public string? WriteApiKey { get; set; }
}
