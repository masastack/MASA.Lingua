using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 内存采样后台服务：周期性把进程与 GC 堆的内存状况写成一条日志。
/// <para>
/// 为什么必须有它：OOMKilled（cgroup 超限被 SIGKILL）与探针超时被杀都不会触发任何异常钩子 ——
/// 进程死得无痕。唯一的取证方式是「死亡前的内存轨迹」：把采样按时间记进日志（console + 文件双写，
/// 见 Program.cs 的 Serilog 配置），重启后看最后几条采样就知道是缓慢爬升还是尖峰突刺、离 limit 还有多远。
/// </para>
/// <para>
/// 采样与落日志是两件事：采样每 <see cref="MemorySamplerOptions.IntervalSeconds"/> 秒一次（极轻，读几个计数器，不碰 GC），
/// 但稳态下内存几乎不动，逐条落日志只会刷屏（每天 2880 条几乎相同的行），把真正有用的行淹没。
/// 因此只在「有信息量」时才写：首条、距上次落日志满 <see cref="MemorySamplerOptions.LogIntervalSeconds"/>（心跳），
/// 或工作集相对「上次落日志的值」变化 ≥ <see cref="MemorySamplerOptions.ChangeThresholdMB"/>（缓慢爬升按此步长逐级留痕，
/// 尖峰则在下一个采样周期立刻留痕）。工作集占 cgroup 上限比例 ≥ <see cref="MemorySamplerOptions.WarnRatio"/> 时升为 Warning。
/// </para>
/// </summary>
public sealed class MemorySamplerService(ILogger<MemorySamplerService> logger, IOptions<MemorySamplerOptions> options)
    : BackgroundService
{
    private const double Mb = 1024d * 1024d;

    // 上次「落了日志」的工作集（MB）与时间戳；null 表示还没落过，首次采样必落。
    private double? _lastLoggedWorkingSetMb;
    private long _lastLoggedAt;

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

    /// <summary>
    /// 本次采样是否值得落日志（纯函数，便于单测）：首条 / 心跳到期 / 工作集相对上次落日志的值变化达到阈值。
    /// </summary>
    internal static bool ShouldLog(
        double? lastLoggedWorkingSetMb,
        TimeSpan sinceLastLog,
        double workingSetMb,
        MemorySamplerOptions options)
    {
        if (lastLoggedWorkingSetMb is not { } last)
        {
            return true;
        }

        return sinceLastLog >= TimeSpan.FromSeconds(Math.Max(0, options.LogIntervalSeconds))
               || Math.Abs(workingSetMb - last) >= Math.Max(0, options.ChangeThresholdMB);
    }

    private void Sample()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / Mb;

            if (!ShouldLog(_lastLoggedWorkingSetMb, Stopwatch.GetElapsedTime(_lastLoggedAt), workingSetMb, options.Value))
            {
                return;
            }

            _lastLoggedWorkingSetMb = workingSetMb;
            _lastLoggedAt = Stopwatch.GetTimestamp();

            var gc = GC.GetGCMemoryInfo();
            var limit = TryReadContainerMemoryLimit();
            var nearLimit = limit is { } cap && workingSetMb * Mb >= cap * options.Value.WarnRatio;

            logger.Log(
                nearLimit ? LogLevel.Warning : LogLevel.Information,
                "[mem] 工作集 {WorkingSetMB:0} MB / 提交 {PrivateMB:0} MB / GC堆 {HeapMB:0} MB" +
                "（Gen0 {Gen0} Gen1 {Gen1} Gen2 {Gen2} LOH {Loh} POH {Poh} MB）" +
                " / 峰值 {PeakMB:0} MB / GC {Gc0}-{Gc1}-{Gc2} 次 / cgroup上限 {Limit}",
                workingSetMb,
                process.PrivateMemorySize64 / Mb,
                gc.HeapSizeBytes / Mb,
                gc.GenerationInfo[0].SizeAfterBytes / Mb,
                gc.GenerationInfo[1].SizeAfterBytes / Mb,
                gc.GenerationInfo[2].SizeAfterBytes / Mb,
                gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes / Mb : 0,
                gc.GenerationInfo.Length > 4 ? gc.GenerationInfo[4].SizeAfterBytes / Mb : 0,
                process.PeakWorkingSet64 / Mb,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
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

/// <summary>内存采样配置。环境变量形式如 <c>Lingua__MemorySampler__LogIntervalSeconds=600</c>。</summary>
public sealed class MemorySamplerOptions
{
    public const string SectionName = "Lingua:MemorySampler";

    /// <summary>采样周期（秒）。默认 30s，设为 0 或更小会被夹到最小 5s。只决定「多快能发现变化」，不决定日志量。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>稳态心跳：内存没有明显变化时，至少每隔多少秒落一条日志。默认 300s（5 分钟）。</summary>
    public int LogIntervalSeconds { get; set; } = 300;

    /// <summary>变化阈值（MB）：工作集相对上次落日志的值变化达到此值就立即落一条。默认 30MB。</summary>
    public double ChangeThresholdMB { get; set; } = 30;

    /// <summary>告警比例：工作集 ≥ cgroup 上限 × 此值时，该条日志升为 Warning。默认 0.85。读不到 cgroup 上限时不生效。</summary>
    public double WarnRatio { get; set; } = 0.85;
}
