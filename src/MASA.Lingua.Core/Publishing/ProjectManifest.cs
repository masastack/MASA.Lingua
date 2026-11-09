using System.Text.Json.Serialization;

namespace MASA.Lingua.Core.Publishing;

public sealed record ManifestCulture(
    string Culture,
    string Name,
    string Url,
    string ETag,
    int EntryCount,
    int Completion);

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
