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
}
