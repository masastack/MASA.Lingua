using MASA.Lingua.Core.Localization;

namespace MASA.Lingua.SDK.Abstractions;

public sealed class LinguaClientOptions
{
    public const string SectionName = "Lingua";

    /// <summary>
    /// 业务项目在 Lingua 平台中的唯一编码（如 "auth-web-staging"、"auth-sso-staging"）
    /// </summary>
    public required string ProjectCode { get; set; }

    /// <summary>
    /// Lingua 平台服务端地址（例如 "https://lingua.internal.com"）
    /// </summary>
    public string? ServerEndpoint { get; set; }

    /// <summary>
    /// 业务项目本地存放 {culture}.json 的目录路径（默认 "i18n"，用于离线首屏兜底与同步落盘）
    /// </summary>
    public string LocalJsonDirectory { get; set; } = "i18n";

    /// <summary>
    /// 是否在开发环境下自动上报未定义的缺失 Key 到 Lingua 平台
    /// </summary>
    public bool EnableMissingKeyTelemetry { get; set; } = true;

    /// <summary>
    /// SSE 未连接时的空闲对账周期（默认 60 秒；每次对账通常只是一次 304）。
    /// 它是长连接被代理 / 防火墙掐掉时唯一能让客户端追上线上版本的路径，因此不建议设为 <see cref="TimeSpan.Zero"/>。
    /// SSE 连接正常时空闲对账会自动降到 5 分钟（见 <c>LinguaJsonSyncService.SseIdleSyncInterval</c>）。
    /// 仅服务端 SDK（AspNetCore）使用；MAUI 客户端没有常驻轮询，不读取此选项——更新在应用每次启动时拉取。
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>是否订阅服务端 SSE 发布事件（断线自动退避重连）。开着时发布后业务端秒级热更新，关掉只能靠空闲对账。</summary>
    public bool EnableServerEvents { get; set; } = true;

    /// <summary>
    /// 项目写密钥（缺词上报鉴权用），仅限**服务端** SDK 使用：在 Lingua 管理后台「接入密钥」面板获取，
    /// 随 <c>X-Lingua-Key</c> 请求头发送到 <c>POST /api/v1/telemetry/missing-keys</c>。
    /// 客户端 SDK（MAUI / Vue）不支持缺词上报——密钥无法安全保管在不可信客户端，且服务端只接受携带密钥的上报。
    /// </summary>
    public string? WriteKey { get; set; }
}

/// <summary>
/// 客户端进程内只读字典存储抽象（底层使用 FrozenDictionary 实现无锁纳秒级查找）
/// </summary>
public interface ILinguaMemoryStore
{
    bool TryGetValue(string culture, string key, out string value);
    IEnumerable<KeyValuePair<string, string>> GetAll(string culture, string? namespacePrefix = null);
    void UpdateCultureFromJsonBytes(string culture, ReadOnlySpan<byte> utf8Json);
    void LoadFromLocalDirectory(string directoryPath);

    /// <summary>设置回退语种（由同步服务按平台 manifest 的默认语种设置）。</summary>
    void SetDefaultCulture(string culture) { }

    /// <summary>
    /// 当前进程内已加载的语种（供诊断 / 自检使用）。
    /// 默认实现返回空集合：自带实现者不必强制实现，但诊断端点会据此报告「没有任何语种」。
    /// </summary>
    IReadOnlyList<string> GetLoadedCultures() => [];

    /// <summary>平台下发的默认语种（manifest 的 defaultCulture）；未知时返回 null。</summary>
    string? DefaultCulture => null;

    /// <summary>
    /// 平台下发的支持语种列表（manifest 的 cultures）。这是「站点支持哪些语言」的唯一来源；
    /// 未知（还没同步过 / 非 Lingua 实现）时返回 null，调用方应退回本地推断或不做覆盖。
    /// </summary>
    IReadOnlyList<string>? SupportedCultures => null;

    /// <summary>设置平台下发的支持语种列表（由同步服务按 manifest 写入）。</summary>
    void SetSupportedCultures(IReadOnlyList<string> cultures) { }
}

public interface IMissingKeyReporter
{
    void ReportMissingKey(string culture, string fullKey, string? defaultText = null);
}

/// <summary>
/// 多语言词条热更新或语种切换通知器（供 Blazor Server / Wasm 组件触发 StateHasChanged）
/// </summary>
public interface ILinguaChangeNotifier
{
    event Action<string>? OnCultureUpdated;
    void NotifyCultureUpdated(string culture);
}

/// <summary>
/// 语种资源文件名解析：Lingua 资源目录里一个文件即一个语种，
/// 文件名（不含扩展名）必须是合法的 BCP47 语种代码（ll / ll-RR / ll-Ssss / ll-Ssss-RR 形状，如 zh-CN、en）。
/// <para>
/// 用「语种码形状」白名单替代保留文件名黑名单：supportedCultures.json、languageConfig.json、README.json
/// 等任何元数据 / 杂项文件的文件名都不满足 BCP47 形状，天然被排除，不会被当作语种加载进 store。
/// 校验复用服务端导入接口的同一套规则（<see cref="LinguaCultures.Normalize"/>），单一事实来源。
/// </para>
/// <para>
/// 形状校验比服务端的标准语种表宽松是有意的：平台将来扩语种表不需要客户端 SDK 跟着发版。
/// </para>
/// </summary>
public static class LinguaCultureFiles
{
    /// <summary>
    /// 尝试把资源文件名（不含扩展名）解析为语种名：合法 BCP47 形状返回 true，
    /// 并给出规范化后的语种名（zh_CN / ZH-CN 等不规范写法会被归一为 zh-CN）。
    /// </summary>
    public static bool TryResolveCultureFileName(string? fileNameWithoutExtension, out string culture)
    {
        culture = LinguaCultures.Normalize(fileNameWithoutExtension) ?? string.Empty;
        return culture.Length > 0;
    }
}
