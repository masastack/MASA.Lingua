using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 内存采样后台服务：只在工作集「明显变化」时记一条日志，给 OOMKilled 事后取证用。
/// <para>
/// 为什么必须有它：OOMKilled（cgroup 超限被 SIGKILL）与探针超时被杀都不会触发任何异常钩子 ——
/// 进程死得无痕。重启后看最后几条 [mem] 就知道是缓慢爬升还是尖峰突刺、离上限还有多远、涨的是托管堆还是堆外。
/// </para>
/// <para>
/// 采样每 <see cref="MemorySamplerOptions.IntervalSeconds"/> 秒一次（极轻），但只有工作集相对「上次落日志的值」
/// 变化 ≥ <see cref="MemorySamplerOptions.ChangeThresholdMB"/> 才写日志；首条作为基线。稳态不写 ——
/// 没有新行就代表仍在上一行 ± 阈值之内。缓慢爬升按阈值步长逐级留痕，尖峰在下一个采样周期立刻留痕。
/// </para>
/// </summary>
public sealed class MemorySamplerService(ILogger<MemorySamplerService> logger, IOptions<MemorySamplerOptions> options)
    : BackgroundService
{
    private const double Mb = 1024d * 1024d;

    // 上次「落了日志」的工作集（MB）；null 表示还没落过，首次采样作为基线必落。
    private double? _lastLoggedMb;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.IntervalSeconds));
        // 启动后先等一个周期再采第一次：避开启动阶段（迁移 + 恢复发布产物）的正常高水位
        await Task.Delay(interval, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Sample();
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>本次采样是否值得落日志（纯函数，便于单测）：首条基线，或相对上次落日志的值变化达到阈值。</summary>
    internal static bool ShouldLog(double? lastLoggedMb, double currentMb, double thresholdMb)
        => lastLoggedMb is not { } last || Math.Abs(currentMb - last) >= Math.Max(0, thresholdMb);

    private void Sample()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / Mb;

            if (!ShouldLog(_lastLoggedMb, workingSetMb, options.Value.ChangeThresholdMB))
            {
                return;
            }

            _lastLoggedMb = workingSetMb;

            // GC 堆与工作集的差值 = 堆外内存（原生分配 / JIT / 映射文件）；峰值抓两次采样之间的尖峰。
            var limit = TryReadContainerMemoryLimit();
            logger.LogInformation(
                "[mem] 工作集 {WorkingSetMB:0} MB / GC堆 {HeapMB:0} MB / 峰值 {PeakMB:0} MB / 上限 {Limit}",
                workingSetMb,
                GC.GetGCMemoryInfo().HeapSizeBytes / Mb,
                process.PeakWorkingSet64 / Mb,
                limit is { } l ? $"{l / Mb:0} MB" : "未知");
        }
        catch (Exception ex)
        {
            // 采样失败绝不能影响服务本身：记一条 Warning 即可（采样器自己用 default 日志器，不会死循环）
            logger.LogWarning(ex, "[mem] 内存采样失败。");
        }
    }

    /// <summary>
    /// 读容器内存上限（cgroup v2 的 /sys/fs/cgroup/memory.max；v1 回退 memory.limit_in_bytes）。
    /// 非容器环境（Windows / 裸机）读不到就返回 null —— 日志里显示「未知」即可。
    /// </summary>
    private static long? TryReadContainerMemoryLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (var path in new[] { "/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes" })
        {
            try
            {
                var text = File.ReadAllText(path).Trim();
                if (text == "max")
                {
                    continue;
                }

                if (long.TryParse(text, out var value) && value is > 0 and < long.MaxValue)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 文件不存在 / 无权限：试下一个路径
            }
        }

        return null;
    }
}

/// <summary>内存采样配置。环境变量形式如 <c>Lingua__MemorySampler__ChangeThresholdMB=50</c>。</summary>
public sealed class MemorySamplerOptions
{
    public const string SectionName = "Lingua:MemorySampler";

    /// <summary>采样周期（秒）。默认 30s，设为 0 或更小会被夹到最小 5s。只决定「多快能发现变化」，不决定日志量。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>变化阈值（MB）：工作集相对上次落日志的值变化达到此值才落一条。默认 30MB。</summary>
    public double ChangeThresholdMB { get; set; } = 30;
}
