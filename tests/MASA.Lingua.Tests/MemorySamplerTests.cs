using MASA.Lingua.Server.Services;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// 内存采样的落日志判定：稳态静默，但首条基线与明显变化（含缓慢爬升的累计变化）必须留痕。
/// 采样是 OOMKilled 取证的唯一依据，降噪不能丢掉「死亡前的内存曲线」。
/// </summary>
public sealed class MemorySamplerTests
{
    private const double Threshold = 30;

    [Fact]
    public void First_sample_is_always_logged_as_baseline()
    {
        Assert.True(MemorySamplerService.ShouldLog(null, 281, Threshold));
    }

    [Fact]
    public void Steady_state_is_silent()
    {
        // 工作集只差几 MB：正是线上刷屏的那种重复行
        Assert.False(MemorySamplerService.ShouldLog(281, 283, Threshold));
        Assert.False(MemorySamplerService.ShouldLog(281, 281, Threshold));
    }

    [Theory]
    [InlineData(281, 311)] // 上升恰好达到阈值
    [InlineData(281, 400)] // 尖峰
    [InlineData(281, 251)] // 骤降（GC 回收 / 重启后的高水位回落）同样是有价值的拐点
    public void Large_change_is_logged(double last, double current)
    {
        Assert.True(MemorySamplerService.ShouldLog(last, current, Threshold));
    }

    [Fact]
    public void Slow_creep_is_logged_once_cumulative_change_crosses_threshold()
    {
        // 每次只涨 10MB：对比的是「上次落日志的值」而不是「上次采样的值」，
        // 所以缓慢爬升不会因为单步很小而永远不留痕。
        const double lastLogged = 200;
        Assert.False(MemorySamplerService.ShouldLog(lastLogged, 210, Threshold));
        Assert.False(MemorySamplerService.ShouldLog(lastLogged, 220, Threshold));
        Assert.True(MemorySamplerService.ShouldLog(lastLogged, 230, Threshold));
    }
}
