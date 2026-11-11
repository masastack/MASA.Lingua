using System.Text.Json;
using Microsoft.JSInterop;

namespace MASA.Lingua.Server.Services;

/// <summary>
/// 进程级全局异常兜底：让「未处理异常导致的崩溃」留下最后的证据。
/// <para>
/// 为什么必须自己挂钩子：默认日志管道在进程崩溃时来不及把缓冲区刷出去 —— 容器里 stdout/stderr
/// 由运行时驱动采集（kubectl logs 能看到的最后几行就是它），而文件日志/Serilog 管道有自己的缓冲与
/// 刷写间隔。这里双写：Console.Error 立即同步写出（stderr 无缓冲），再加一次尽力而为的落盘。
/// </para>
/// <para>
/// 两个钩子各管一类死亡：
/// <see cref="AppDomain.UnhandledException"/> 管线程上的未捕获异常（含 Blazor 渲染线程逃逸的异常）；
/// <see cref="TaskScheduler.UnobservedTaskException"/> 管被丢弃的 fire-and-forget 任务里的异常
/// （GC 终结时才浮出，默认直接吞掉）。后者不该让进程死，但必须记录 —— 它们往往就是
/// 「用户操作偶发失败、页面卡住」的真凶。
/// </para>
/// <para>
/// 注意：SIGKILL（OOMKilled / 探针超时被杀）不会触发这两个钩子 —— 那类死亡靠 GC 硬上限
/// （Dockerfile）与内存采样日志（MemorySamplerService）留证据，不靠这里。
/// </para>
/// </summary>
public static class CrashReporter
{
    private static readonly object FileLock = new();
    private static string? _crashLogDirectory;

    /// <summary>
    /// 注册两个全局钩子。必须排在应用构建的最前面：越早注册，越早期的崩溃越有痕迹。
    /// </summary>
    /// <param name="crashLogDirectory">
    /// 崩溃记录的落盘目录（尽力而为，写不进就只剩 Console.Error 一份）。
    /// 传 null 表示不落盘，只写 stderr。
    /// </param>
    public static void Register(string? crashLogDirectory = null)
    {
        _crashLogDirectory = crashLogDirectory;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(source: "UnhandledException", e.ExceptionObject as Exception, e.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 标记为已观察：.NET 4.5 之后未观察任务异常不再杀进程，这里记录后吞掉即可；
            // 不 SetObserved 会依赖终结器线程的默认行为（同样不死进程），但显式标记语义更清楚。
            e.SetObserved();

            if (TryDescribeKnownBenignNoise(e.Exception, out var noise))
            {
                // Masa.Blazor 1.12.0-alpha.2 库内 fire-and-forget JS interop 的两类「已核实无功能影响」模式，
                // 压成单行 NOISE 落盘，避免淹没 crash.log 里真正的信号；其余模式一律完整记录。
                // 为什么兄弟项目（masa.specdoc 站点等同样跑着 1.12.0-alpha.2）没见过这条：它们没有
                // UnobservedTaskException 观测，.NET 默认静默吞掉 —— 是本仓库的 CrashReporter 照见了它。
                // 已核实 JS 侧在异常前已执行、无功能影响；真正的修法在上游。
                // TODO(Masa.Blazor 上游修复后移除本分支)：OutsideClickJSModule.UpdateDependentElementsAsync 里
                // InvokeAsync[bool]("updateExcludeSelectors") 应为 InvokeVoidAsync —— JS 侧函数无返回值。
                // 上游 masastack/MASA.Blazor main 分支（2026-11 核实，blob e1eee3c4）仍未修复。
                ReportNoise(noise);
                return;
            }

            Report(source: "UnobservedTaskException", e.Exception, isTerminating: false);
        };
    }

    private static void Report(string source, Exception? exception, bool isTerminating)
    {
        var line =
            $"[{DateTimeOffset.UtcNow:O}] [LINGUA-{(isTerminating ? "FATAL" : "WARNING")}] {source} " +
            $"IsTerminating={isTerminating}\n{exception}";

        // stderr 无缓冲、同步写出：即使紧接着被杀，这条也已写透，kubectl logs / docker logs 能看到
        try
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush();
        }
        catch
        {
            // stderr 都写不进去时没有任何可做的了，落盘兜底
        }

        if (_crashLogDirectory is not { } directory)
        {
            return;
        }

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "crash.log"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // 落盘失败（磁盘满 / 只读卷）：stderr 那份还在，不能因为记录崩溃而再抛异常
        }
    }

    /// <summary>
    /// 识别「已核实无功能影响」的未观察异常并给出单行描述。判定刻意保守 —— 未观察异常往往是
    /// 「用户操作偶发失败、页面卡住」的真凶线索，只放行两类铁定无害的：
    /// ① Masa.Blazor OutsideClickJSModule 的 null→bool 反序列化失败：JS 侧 updateExcludeSelectors
    ///    已成功执行，仅 .NET 侧 InvokeAsync[bool] 的返回值声明错误（库 bug，纯噪音）；
    /// ② JSDisconnectedException：电路已断开（用户关页/断线）后残留的 interop 调用，
    ///    Blazor 官方认定的 dispose 期正常现象。
    /// 其余（含 InputJSModule 的 ArgumentNullException、NavigationDrawer 的 NRE）一律完整记录。
    /// </summary>
    internal static bool TryDescribeKnownBenignNoise(Exception exception, out string noise)
    {
        if (exception is AggregateException aggregate)
        {
            // 混合聚合（任一内层不认识）必须整体按未知处理 —— 宁可多记不可漏记
            var leaves = aggregate.Flatten().InnerExceptions;
            if (leaves.Count == 0)
            {
                noise = string.Empty;
                return false;
            }

            foreach (var leaf in leaves)
            {
                if (!TryDescribeKnownBenignNoise(leaf, out _))
                {
                    noise = string.Empty;
                    return false;
                }
            }

            TryDescribeKnownBenignNoise(leaves[0], out noise);
            return true;
        }

        noise = string.Empty;

        if (exception is JSException { InnerException: JsonException }
            && exception.StackTrace?.Contains("OutsideClickJSModule.UpdateDependentElementsAsync") == true)
        {
            noise = "Masa.Blazor OutsideClickJSModule: JS updateExcludeSelectors already applied; "
                + "library bug InvokeAsync[bool] cannot deserialize null (no functional impact)";
            return true;
        }

        if (exception is JSDisconnectedException)
        {
            noise = "JS interop after circuit disconnect (JSDisconnectedException: "
                + "user closed the page, normal during dispose)";
            return true;
        }

        return false;
    }

    /// <summary>已知良性噪音的单行落盘：与 <see cref="Report"/> 双写同一对出口，但只占一行、打 NOISE 标。</summary>
    private static void ReportNoise(string noise)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] [LINGUA-NOISE] UnobservedTaskException(known-benign) {noise}";

        // 与 Report 同一套双写：stderr 立即同步（无缓冲），文件尽力而为
        try
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush();
        }
        catch
        {
            // stderr 都写不进去时没有任何可做的了，落盘兜底
        }

        if (_crashLogDirectory is not { } directory)
        {
            return;
        }

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "crash.log"),
                    line + Environment.NewLine);
            }
        }
        catch
        {
            // 落盘失败：stderr 那份还在，不能因为记录噪音而再抛异常
        }
    }
}
