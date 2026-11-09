using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Server.Services;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// SSE 事件的单调 id 与「最近一次事件」缓存：客户端带 Last-Event-ID 重连时据此补发漏掉的发布。
/// 事件本身只承载版本指纹与各语种 ETag，内容由客户端按 ETag 拉取。
/// </summary>
public sealed class SseRevisionTests
{
    [Fact]
    public void EventId_Should_Be_Monotonic_And_Carried_By_Event()
    {
        var first = new BundlePublishedEvent("p", [new PublishedCulture("zh-CN", "\"e1\"")], "zh-CN", "h1", DateTimeOffset.UtcNow);
        var second = new BundlePublishedEvent("p", [new PublishedCulture("zh-CN", "\"e2\"")], "zh-CN", "h2", DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.True(second.EventId > first.EventId);
        Assert.Equal(first.PublishedAt.UtcTicks, first.EventId);
    }

    [Fact]
    public void Broadcast_Should_Record_Last_Event_Per_Project()
    {
        var broadcaster = new LinguaSseBroadcaster();
        var publishedAt = DateTimeOffset.UtcNow;
        broadcaster.Broadcast(new BundlePublishedEvent(
            "shop-vue",
            [new PublishedCulture("zh-CN", "\"e1\"")],
            "zh-CN",
            "hash-1",
            publishedAt));

        // 项目编码大小写不敏感（与订阅过滤一致），补发时不需要客户端还原原始大小写
        Assert.True(broadcaster.TryGetLast("SHOP-VUE", out var last));
        Assert.Equal("hash-1", last.VersionHash);
        Assert.Equal("\"e1\"", Assert.Single(last.Cultures).ETag);
        Assert.Equal(publishedAt.UtcTicks, last.EventId);

        Assert.False(broadcaster.TryGetLast("auth-sso-staging", out _));
    }
}
