using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 内存采样后台服务：周期性把进程与 GC 堆的内存状况写成一条日志。
/// <para>
/// 为什么必须有它：OOMKilled（cgroup 超限被 SIGKILL）与探针超时被杀都不会触发任何异常钩子 ——
/// 进程死得无痕。唯一的取证方式是「死亡前的内存轨迹」：把每次采样按时间记进日志（console + 文件双写，
/// 见 Program.cs 的 Serilog 配置），重启后看最后几条采样就知道是缓慢爬升还是尖峰突刺、离 limit 还有多远。
/// </para>
/// <para>
/// 采样本身极轻（读几个计数器，不碰 GC），30s 一条在默认日志级别下不刷屏。
/// </para>
/// </summary>
public sealed class MemorySamplerService(ILogger<MemorySamplerService> logger, IOptions<MemorySamplerOptions> options)
    : BackgroundService
{
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

    private void Sample()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var gc = GC.GetGCMemoryInfo();
            var limit = TryReadContainerMemoryLimit();

            logger.LogInformation(
                "[mem] 工作集 {WorkingSetMB:0} MB / 提交 {PrivateMB:0} MB / GC堆 {HeapMB:0} MB" +
                "（Gen0 {Gen0} Gen1 {Gen1} Gen2 {Gen2} LOH {Loh} POH {Poh} MB）" +
                " / 峰值 {PeakMB:0} MB / GC {Gc0}-{Gc1}-{Gc2} 次 / cgroup上限 {Limit}",
                process.WorkingSet64 / 1024d / 1024d,
                process.PrivateMemorySize64 / 1024d / 1024d,
                gc.HeapSizeBytes / 1024d / 1024d,
                gc.GenerationInfo[0].SizeAfterBytes / 1024d / 1024d,
                gc.GenerationInfo[1].SizeAfterBytes / 1024d / 1024d,
                gc.GenerationInfo[2].SizeAfterBytes / 1024d / 1024d,
                gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes / 1024d / 1024d : 0,
                gc.GenerationInfo.Length > 4 ? gc.GenerationInfo[4].SizeAfterBytes / 1024d / 1024d : 0,
                process.PeakWorkingSet64 / 1024d / 1024d,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                limit is { } l ? $"{l / 1024d / 1024d:0} MB" : "未知");
        }
        catch (Exception ex)
        {
            // 采样失败绝不能影响服务本身：记一条 Warning 即可（采样器自己用 default 日志器，不会死循环）
            logger.LogWarning(ex, "[mem] 内存采样失败。");
        }
    }

    /// <summary>
    /// 读容器内存上限（cgroup v2 的 /sys/fs/cgroup/memory.max；v1 回退 memory.limit_in_bytes）。
    /// 非容器环境（Windows / 裸机）读不到就返回 null —— 采样日志里显示「未知」即可。
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
            catch (IOException)
            {
                // 文件不存在 / 无权限：试下一个路径
            }
        }

        return null;
    }
}

/// <summary>内存采样配置：采样周期（秒）。默认 30s，设为 0 或更小会被夹到最小 5s。</summary>
public sealed class MemorySamplerOptions
{
    public const string SectionName = "Lingua:MemorySampler";

    public int IntervalSeconds { get; set; } = 30;
}
