using System.Reflection;

namespace MASA.Lingua.Storage.EfCore;

/// <summary>
/// 内置示例项目（真实业务数据）：词条原样取自 MASA.Auth 两个项目的 <c>Assets/I18n/*.json</c>。
/// <para>
/// 只描述数据形状，不含写库逻辑——播种统一走正式的导入路径
/// （<c>ILinguaRepository.ImportJsonBundleAsync</c>），与后台「导入 JSON」功能共用同一套解析与校验。
/// </para>
/// </summary>
internal sealed record SeedProject(
    string Code,
    string DefaultCulture,
    IReadOnlyList<string> SupportedCultures,
    IReadOnlyList<string> SharedNamespaceCodes,
    /// <summary>内置词条写入的命名空间；空串表示项目根（发布为 JSON 根级裸 key）。</summary>
    string NamespaceCode);

internal static class LinguaSeed
{
    /// <summary>
    /// 内置项目清单。两个项目的错误码词条落在<b>项目根命名空间</b>（<see cref="SeedProject.NamespaceCode"/> 为空串），
    /// 因为 MASA I18n 的调用形态是平铺 key（<c>I18n.T("UserNotExist")</c> / <c>UserFriendlyException(errorCode: "UserNotExist")</c>），
    /// 只有根命名空间才能发布成扁平根 key <c>{"UserNotExist":"…"}</c> 与之对齐；
    /// 放进 <c>errors</c> 命名空间会发布成 <c>{"errors":{"UserNotExist":…}}</c>，平铺 key 永远命不中。
    /// <para>
    /// 项目编码 = PM 中该应用在目标环境的 Identity（Lingua 平台的项目切换器与元数据一律以 PM 为准），
    /// 也即业务宿主配置 <c>Lingua:ProjectCode</c> 的取值：
    /// staging 环境为 <c>auth-web-staging</c>（Auth 管理端应用，词条来自 Masa.Auth.Service.Admin）
    /// 与 <c>auth-sso-staging</c>（SSO 登录站应用，词条来自 Masa.Auth.Web.Sso）。
    /// </para>
    /// <para>
    /// 两个项目同时继承全局 <c>common</c>/<c>errors</c> 命名空间，用于演示「共享层继承」：
    /// 发布产物里 <c>common</c>/<c>errors</c> 作为子对象存在，项目根级的裸 key 与它们互不干扰。
    /// 注意此时不能再用「项目内同名 errors 命名空间覆盖全局 errors」的方式演示覆盖——根级 key 与
    /// 全局 <c>errors</c> 命名空间下的 key 不在同一命名空间，不会发生覆盖。
    /// </para>
    /// </summary>
    internal static readonly SeedProject[] Projects =
    [
        new(
            Code: "auth-web-staging",
            DefaultCulture: "zh-CN",
            SupportedCultures: ["zh-CN", "en-US", "ja-JP", "ru-RU"],
            SharedNamespaceCodes: ["common", "errors"],
            NamespaceCode: ""),
        new(
            Code: "auth-sso-staging",
            DefaultCulture: "zh-CN",
            SupportedCultures: ["zh-CN", "en-US", "ja-JP", "ru-RU"],
            SharedNamespaceCodes: ["common", "errors"],
            NamespaceCode: "")
    ];

    /// <summary>
    /// 读取嵌入的原始语种 JSON。
    /// <para>
    /// 按 <c>.seed.{project}.{culture}.json</c> 后缀匹配，而不是硬编码完整资源名：
    /// 嵌入资源名里的目录分隔符会被替换成 <c>.</c>，且 MSBuild 会把目录名里的 <c>-</c> 改写成 <c>_</c>
    /// （<c>auth-web-staging</c> → <c>auth_web_staging</c>）。因此比较前先把两侧的 <c>-</c>/<c>_</c> 一并去掉。
    /// 找不到直接抛——宁可启动失败，也不要静默播种一个空项目。
    /// </para>
    /// </summary>
    public static ReadOnlyMemory<byte> ReadBundle(string projectCode, string culture)
    {
        var assembly = typeof(LinguaSeed).Assembly;
        var suffix = Normalize($".seed.{projectCode}.{culture}.json");
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => Normalize(name).EndsWith(suffix, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException(
                $"缺少内置种子资源：{projectCode}/{culture}.json（期望资源名以 '.seed.{projectCode}.{culture}.json' 结尾）。"
                + $"现有资源：{string.Join(", ", assembly.GetManifestResourceNames().OrderBy(n => n))}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法打开内置种子资源：{resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>资源名比较键：忽略大小写与 <c>-</c>/<c>_</c> 差异（MSBuild 会把目录名里的 <c>-</c> 改写成 <c>_</c>）。</summary>
    private static string Normalize(string value) =>
        value.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
}
