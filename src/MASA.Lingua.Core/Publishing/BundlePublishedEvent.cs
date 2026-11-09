namespace MASA.Lingua.Core.Publishing;

/// <summary>
/// 发布事件里的单个语种。带 ETag 是有意为之：客户端收到事件后可以直接比对本地持有的 ETag，
/// 跳过「先请求 manifest 才知道哪个语种变了」的那一跳。
/// </summary>
public sealed record PublishedCulture(string Culture, string ETag);

/// <summary>
/// 平台发布 / 回滚后广播的变更通知。刻意只带「变了什么」而不带内容：
/// 内容永远是客户端按 ETag 主动拉取的，所以事件丢失 / 重复 / 乱序都只影响延迟，不影响正确性。
/// </summary>
public sealed record BundlePublishedEvent(
    string ProjectCode,
    IReadOnlyList<PublishedCulture> Cultures,
    string DefaultCulture,
    string VersionHash,
    DateTimeOffset PublishedAt)
{
    /// <summary>
    /// SSE 事件 id：默认取发布时间的 UTC ticks（跨重启仍可比），由广播器盖成严格单调值。
    /// 浏览器 EventSource 断线重连时用 <c>Last-Event-ID</c> 带回它，服务端据此补发漏掉的最近一次发布。
    /// </summary>
    public long EventId { get; init; } = PublishedAt.UtcTicks;
}
