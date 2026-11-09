namespace MASA.Lingua.SDK.Abstractions;

/// <summary>
/// SDK 与服务端之间的协议常量：路径、请求头、URL 组装。
/// 所有 SDK 包（AspNetCore / Maui / MasaBlazor / MasaI18n）都必须走这里，
/// 避免同一份协议在多个包里各写一遍（以前的 manifest 路径就有三份拷贝）。
/// </summary>
public static class LinguaProtocol
{
    /// <summary>客户端实例标识，服务端据此统计「采用情况」。</summary>
    public const string ClientHeader = "X-Lingua-Client";

    /// <summary>客户端当前持有的发布版本号。</summary>
    public const string VersionHeader = "X-Lingua-Version";

    public static string ManifestPath(string projectCode) =>
        $"/api/v1/projects/{Uri.EscapeDataString(projectCode)}/manifest";

    public static string BundlePath(string projectCode, string culture) =>
        $"/i18n/{Uri.EscapeDataString(projectCode)}/{Uri.EscapeDataString(culture)}.json";

    public static string StreamPath(string projectCode) =>
        $"/api/v1/stream?project={Uri.EscapeDataString(projectCode)}";

    public static string MissingKeysPath(string projectCode) =>
        $"/api/v1/telemetry/missing-keys?project={Uri.EscapeDataString(projectCode)}";

    /// <summary>
    /// 把服务端返回的相对路径拼到 endpoint 上；已经是绝对地址时原样返回
    /// （服务端将来改成 CDN 绝对地址时不会拼错）。
    /// </summary>
    public static string Combine(string endpoint, string relativeOrAbsolute) =>
        relativeOrAbsolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        relativeOrAbsolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? relativeOrAbsolute
            : endpoint.TrimEnd('/') + (relativeOrAbsolute.StartsWith('/') ? relativeOrAbsolute : "/" + relativeOrAbsolute);
}
