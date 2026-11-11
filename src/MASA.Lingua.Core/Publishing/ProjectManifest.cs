using System.Text.Json.Serialization;

namespace MASA.Lingua.Core.Publishing;

public sealed record ManifestCulture(
    string Culture,
    string Name,
    string Url,
    string ETag,
    int EntryCount,
    int Completion,
    /// <summary>扁平格式的下载地址（<c>?format=flat</c>）。嵌套 JSON 无法表达「裸 key 与同名命名空间共存」
    /// （键路径冲突），扁平文件没有这个限制——.NET 客户端优先用它下载。旧服务端不返回该字段，客户端回退 <see cref="Url"/>。</summary>
    string? FlatUrl = null);

/// <summary>
/// 项目发布清单：SDK / CLI 只需要请求这一个地址就能知道「现在线上是哪个版本、有哪些语种、各自的地址和 ETag」。
/// 清单自身的 ETag 由版本指纹决定，一次 304 即可确认所有语种都没有变化。
/// </summary>
public sealed record ProjectManifest(
    string Project,
    string DefaultCulture,
    string Version,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ManifestCulture> Cultures,
    string ManifestUrl,
    string StreamUrl)
{
    /// <summary>清单的 HTTP ETag（强校验，由版本指纹决定）。</summary>
    [JsonIgnore]
    public string ETag => $"\"lingua-m-{Version}\"";
}
