using System.Collections.Concurrent;
using System.Threading.Channels;
using MASA.Lingua.Core.Publishing;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 服务端 SSE 变更广播器：
/// 当管理员在后台点击【发布】生成新的 {culture}.json 后，实时通知已连接的 ASP.NET Core / Blazor Server 业务端拉取最新静态文件落盘。
/// 事件只承载「哪个版本、哪些语种、各自 ETag」——内容一律由客户端按 ETag 拉取，因此丢事件只会变慢，不会变错。
/// </summary>
public sealed class LinguaSseBroadcaster
{
    private readonly ConcurrentDictionary<Guid, (string? ProjectFilter, Channel<BundlePublishedEvent> Channel)> _subscribers = new();

    // 每个项目最近一次发布事件：客户端带 Last-Event-ID 重连时补发漏掉的那一条。
    // 单实例部署，内存即可（多副本需要外置，见 docs/integration/README.md 的部署说明）。
    private readonly ConcurrentDictionary<string, BundlePublishedEvent> _lastByProject = new(StringComparer.OrdinalIgnoreCase);

    // 已发出的最大事件 id（严格单调，见 NextEventId）
    private long _lastEventId;

    public (Guid SubscriptionId, ChannelReader<BundlePublishedEvent> Reader) Subscribe(string? projectCode = null)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<BundlePublishedEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscribers[id] = (projectCode, channel);
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var sub))
        {
            sub.Channel.Writer.TryComplete();
        }
    }

    /// <summary>取该项目最近一次发布事件（用于 <c>Last-Event-ID</c> 补发）。</summary>
    public bool TryGetLast(string projectCode, out BundlePublishedEvent evt) =>
        _lastByProject.TryGetValue(projectCode, out evt!);

    /// <summary>
    /// 忘掉该项目最近一次发布事件（项目被删除时调用）：
    /// 否则同名项目重建后，带旧 <c>Last-Event-ID</c> 重连的客户端会被补发一条指向已不存在产物的陈旧事件。
    /// </summary>
    public void Forget(string projectCode) => _lastByProject.TryRemove(projectCode, out _);

    public void Broadcast(BundlePublishedEvent evt)
    {
        // 事件 id 必须严格单调：发布时间的 ticks 在极端情况下可能相同（同一 100ns 内两次发布），
        // 客户端按 Last-Event-ID 比对，重复 id 会让漏掉的那一次永远补不回来。
        evt = evt with { EventId = NextEventId(evt.PublishedAt.UtcTicks) };
        _lastByProject[evt.ProjectCode] = evt;

        foreach (var (_, (filter, channel)) in _subscribers)
        {
            if (string.IsNullOrWhiteSpace(filter) ||
                string.Equals(filter, evt.ProjectCode, StringComparison.OrdinalIgnoreCase))
            {
                channel.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>取严格递增的事件 id：以 ticks 为基准，与已发出的最大值取 max 后 +1 兜底。</summary>
    private long NextEventId(long ticks)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastEventId);
            var next = Math.Max(ticks, last + 1);
            if (Interlocked.CompareExchange(ref _lastEventId, next, last) == last)
            {
                return next;
            }
        }
    }
}
