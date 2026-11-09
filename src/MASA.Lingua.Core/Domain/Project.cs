namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 接入多语言平台的业务项目（如 auth-web-staging, auth-sso-staging）。
/// <para>
/// 只承载「多语言配置」本身：语种、共享命名空间继承、发布策略与最近一次发布结果。
/// 项目展示名、描述、创建时间、同步落盘目录等不属于多语言配置，分别由 Code 展示与部署配置承担。
/// </para>
/// </summary>
    public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>项目唯一编码，同时是客户端引用项目、发布文件路径与仓储接口的标识。</summary>
    public required string Code { get; set; }

    /// <summary>
    /// 项目写密钥（缺词上报鉴权）：客户端 SDK 调缺词上报端点时随 <c>X-Lingua-Key</c> 头上送，服务端比对。
    /// 明文存储以便管理后台「接入密钥」面板查看与重置（内部平台，库本身与部署配置同一信任边界）。
    /// </summary>
    public string? WriteKey { get; set; }

    public string DefaultCulture { get; set; } = "zh-CN";

    public List<string> SupportedCultures { get; set; } = ["zh-CN", "en-US", "ja-JP"];

    /// <summary>继承的公共命名空间编码列表（如 "common", "errors"）。</summary>
    public List<string> SharedNamespaceCodes { get; set; } = ["common"];

    public DateTimeOffset? LastPublishedAt { get; set; }

    public string? LastPublishedVersionHash { get; set; }

    /// <summary>项目级发布策略（发布内容过滤与发布门禁）。</summary>
    public PublishPolicy PublishPolicy { get; set; } = new();

    public List<TranslationNamespace> Namespaces { get; set; } = [];

    public List<GlossaryTerm> GlossaryTerms { get; set; } = [];
}
