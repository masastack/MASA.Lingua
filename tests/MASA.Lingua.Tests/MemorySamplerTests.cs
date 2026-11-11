using MASA.Lingua.Server.Services;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 内存采样的落日志判定：稳态不刷屏（心跳之外静默），但首条、心跳到期、明显变化（含缓慢爬升的累计变化）必须留痕。
/// 采样是 OOMKilled 取证的唯一依据，降噪不能丢掉「死亡前的内存曲线」。
/// </summary>
public sealed class MemorySamplerTests
{
    private static readonly MemorySamplerOptions Defaults = new(); // 心跳 300s / 阈值 30MB

    [Fact]
    public void First_sample_is_always_logged()
    {
        Assert.True(MemorySamplerService.ShouldLog(null, TimeSpan.Zero, 281, Defaults));
    }

    [Fact]
    public void Steady_state_within_heartbeat_is_silent()
    {
        // 30s 后工作集只差 2MB：正是线上刷屏的那种重复行
        Assert.False(MemorySamplerService.ShouldLog(281, TimeSpan.FromSeconds(30), 283, Defaults));
        Assert.False(MemorySamplerService.ShouldLog(281, TimeSpan.FromSeconds(299), 281, Defaults));
    }

    [Fact]
    public void Heartbeat_logs_even_when_memory_is_flat()
    {
        Assert.True(MemorySamplerService.ShouldLog(281, TimeSpan.FromSeconds(300), 281, Defaults));
    }

    [Theory]
    [InlineData(281, 311)] // 上升恰好达到阈值
    [InlineData(281, 400)] // 尖峰
    [InlineData(281, 251)] // 骤降（GC 回收 / 重启后的高水位回落）同样是有价值的拐点
    public void Large_change_is_logged_immediately(double last, double current)
    {
        Assert.True(MemorySamplerService.ShouldLog(last, TimeSpan.FromSeconds(30), current, Defaults));
    }

    [Fact]
    public void Slow_creep_is_logged_once_cumulative_change_crosses_threshold()
    {
        // 每次只涨 10MB：对比的是「上次落日志的值」而不是「上次采样的值」，
        // 所以缓慢爬升不会因为单步很小而永远不留痕。
        const double lastLogged = 200;
        Assert.False(MemorySamplerService.ShouldLog(lastLogged, TimeSpan.FromSeconds(30), 210, Defaults));
        Assert.False(MemorySamplerService.ShouldLog(lastLogged, TimeSpan.FromSeconds(60), 220, Defaults));
        Assert.True(MemorySamplerService.ShouldLog(lastLogged, TimeSpan.FromSeconds(90), 230, Defaults));
    }
}
