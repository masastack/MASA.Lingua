using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using MASA.Lingua.SDK.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MASA.Lingua.SDK.AspNetCore;

/// <summary>
/// 业务端 {culture}.json 同步与热重载后台服务：
/// 1. 启动时立即从业务项目本地 i18n/{culture}.json 加载到内存 FrozenDictionary（离线可独立启动）；
/// 2. 监听本地 i18n/*.json 文件变更，文件被覆盖时自动热更新内存字典；
/// 3. 配置了 ServerEndpoint 时按 ETag 对账：manifest 304 即全部未变；版本变化时只拉取 ETag 变化的语种，
///    原子覆盖写入本地磁盘并更新内存；
/// 4. 只有一个同步循环。两个变更来源都往同一个容量为 1 的队列投递，串行执行、自动合并：
///    - SSE 事件：带版本指纹与各语种 ETag，可直接拉变化的语种，跳过 manifest 往返（实时路径）；
///    - 队列空闲超时：静默满一个周期就主动对账一次（兜底路径）。
///    SSE 连着时空闲周期拉长（事件负责实时），断开或未启用时用 PollInterval ——
///    那是「代理掐长连接 / 防火墙拦 event-stream」这类环境里唯一能让客户端追上线上版本的机制。
/// </summary>
public sealed class LinguaJsonSyncService(
    ILinguaMemoryStore memoryStore,
    IOptions<LinguaClientOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    ILogger<LinguaJsonSyncService> logger) : BackgroundService
{
    public const string ClientHeader = "X-Lingua-Client";
    public const string VersionHeader = "X-Lingua-Version";

    /// <summary>并行下载语种的上限：跨机 5 个语种串行要 5 个 RTT，并行后只花 1 个。</summary>
    private const int MaxParallelDownloads = 3;

    private readonly LinguaClientOptions _options = optionsAccessor.Value;
    private readonly ConcurrentDictionary<string, string> _cultureEtags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 每语种一把写盘闸门。启动对账、SSE 事件、手动 SyncAllAsync 可能并发下载同一语种；
    /// Windows 的 File.Move(overwrite) 在目标被占用时会抛异常，串行化后并发写同一文件变成排队覆盖。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cultureWriteLocks =
        new(StringComparer.OrdinalIgnoreCase);

    // 队列里存「最后一次变更提示」：null = 全量对账（启动 / 空闲超时 / 失败重试）。
    // 容量 1 + DropWrite 是有意的：同步永远拉当前线上状态，重复或丢弃的提示都不会漏版本。
    private readonly Channel<BundlePublishedEvent?> _syncRequests =
        Channel.CreateBounded<BundlePublishedEvent?>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // 实例标识：{ProjectCode}/{随机 12 位}，随请求头 X-Lingua-Client 上报，服务端据此统计采用情况
    private readonly string _clientId = $"{optionsAccessor.Value.ProjectCode}/{Guid.NewGuid().ToString("N")[..12]}";
    private FileSystemWatcher? _watcher;
    private string? _manifestEtag;
    private volatile string? _currentVersion;
    private int _consecutiveFailures;

    /// <summary>SSE 是否处于已连接状态：决定空闲对账周期（连着时拉长，断开/未启用时用 PollInterval）。</summary>
    private volatile bool _serverEventsConnected;

    /// <summary>业务端当前持有的发布版本指纹（还没同步过时为 null）。</summary>
    public string? CurrentVersion => _currentVersion;

    /// <summary>SSE 当前是否已连上（仅用于诊断 / 测试）。</summary>
    public bool ServerEventsConnected => _serverEventsConnected;

    /// <summary>请求一次全量对账（可重复调用，会合并为一次）。启动、空闲超时、失败重试都通过它触发。</summary>
    internal void RequestSync() => _syncRequests.Writer.TryWrite(null);

    /// <summary>
    /// 按 manifest 同步所有语种：manifest 未变（304）返回 false；有语种被更新返回 true。
    /// 服务端还没有 manifest 接口（旧版本）时，退回逐语种的 ETag 同步。
    /// </summary>
    public async Task<bool> SyncAllAsync(HttpClient? customClient = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return false;
        }

        var client = customClient ?? httpClientFactory.CreateClient("LinguaClient");
        var manifestUrl = LinguaProtocol.Combine(Endpoint, LinguaProtocol.ManifestPath(_options.ProjectCode));

        using var req = NewRequest(manifestUrl);
        if (!string.IsNullOrEmpty(_manifestEtag))
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", _manifestEtag);
        }

        using var res = await client.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            // 项目还没发布（没有 manifest）：暂时没有可同步的内容，等发布后由 SSE / 空闲对账触发再同步
            return false;
        }

        res.EnsureSuccessStatusCode();
        var manifest = await res.Content.ReadFromJsonAsync<ProjectManifest>(LinguaJson.Protocol, ct)
                       ?? throw new InvalidOperationException("manifest 响应为空。");

        // manifest 里重复的语种条目只取第一个：两份一样的并行任务会同时写同一个 {culture}.json
        var entries = manifest.Cultures
            .DistinctBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = await DownloadCulturesAsync(
            // .NET 客户端优先下载扁平格式：没有键路径冲突的表达缺陷，两种表示拍平后的查询 key 完全一致
            entries.Select(c => (c.Culture, c.ETag, string.IsNullOrWhiteSpace(c.FlatUrl) ? c.Url : c.FlatUrl)).ToList(),
            client,
            ct);

        // 语种与默认语种完全以平台为准，但要在**全部语种下载成功后**才生效：
        // 中途失败不把磁盘上还没有的语种列成「站点支持」，下次对账成功才会更新
        memoryStore.SetDefaultCulture(manifest.DefaultCulture);
        memoryStore.SetSupportedCultures(entries.Select(c => c.Culture).ToList());

        // 全部语种都成功后才推进版本与 manifest ETag；中途失败会在下次触发时重试
        var versionChanged = !string.Equals(_currentVersion, manifest.Version, StringComparison.Ordinal);
        _currentVersion = manifest.Version;
        _manifestEtag = res.Headers.ETag?.Tag ?? manifest.ETag;

        if (versionChanged)
        {
            logger.LogInformation("项目 {Project} 已同步到发布版本 {Version}。", _options.ProjectCode, manifest.Version);
            await AckVersionAsync(client, manifestUrl, ct);
        }

        return changed;
    }

    /// <summary>
    /// 供外部或测试主动触发一次 {culture}.json 同步（携带 If-None-Match ETag）
    /// </summary>
    public async Task<bool> SyncCultureFileAsync(
        string culture,
        HttpClient? customClient = null,
        CancellationToken ct = default,
        string? relativeUrl = null)
    {
        if (string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return false;
        }

        var client = customClient ?? httpClientFactory.CreateClient("LinguaClient");
        var url = LinguaProtocol.Combine(
            Endpoint,
            relativeUrl ?? LinguaProtocol.BundlePath(_options.ProjectCode, culture));

        using var req = NewRequest(url);
        if (_cultureEtags.TryGetValue(culture, out var lastEtag) && !string.IsNullOrWhiteSpace(lastEtag))
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", lastEtag);
        }

        using var res = await client.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotModified)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);

        // 原子覆盖写入业务项目自己的本地 i18n/{culture}.json 磁盘文件。
        // Windows 上 File.Move(overwrite: true) 在目标被另一个进程/句柄占用时会抛 UnauthorizedAccessException，
        // 而启动对账、SSE 事件、手动 SyncAllAsync 可能同时下载同一个语种 —— 必须按语种串行化写入，
        // 否则同步会整批失败（DownloadCulturesAsync 里任一失败都会向上抛）。
        if (!string.IsNullOrWhiteSpace(_options.LocalJsonDirectory))
        {
            Directory.CreateDirectory(_options.LocalJsonDirectory);
            var localPath = Path.Combine(_options.LocalJsonDirectory, $"{culture}.json");
            var gate = _cultureWriteLocks.GetOrAdd(culture, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            var tempPath = Path.Combine(_options.LocalJsonDirectory, $"{culture}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, ct);
                MoveWithRetry(tempPath, localPath);
            }
            finally
            {
                // Move 成功时文件已不存在；失败时兜底删除，避免 i18n 目录积累 .tmp 垃圾
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch (IOException) { }
                }

                gate.Release();
            }
        }

        memoryStore.UpdateCultureFromJsonBytes(culture, bytes);

        var etag = res.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(etag))
        {
            _cultureEtags[culture] = etag;
        }

        logger.LogInformation("已同步并热更新项目 {Project} 的 {Culture}.json (ETag: {ETag})", _options.ProjectCode, culture, etag);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        memoryStore.LoadFromLocalDirectory(_options.LocalJsonDirectory);
        StartLocalFileWatcher();

        if (string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return;
        }

        if (!_options.EnableServerEvents && _options.PollInterval <= TimeSpan.Zero)
        {
            logger.LogWarning(
                "项目 {Project} 同时关闭了 SSE 与空闲对账（PollInterval = 0）：启动后不会再感知任何发布，只会使用启动时的本地文件。",
                _options.ProjectCode);
        }

        // 启动后立即对账一次；之后由 SSE 事件与空闲超时驱动
        RequestSync();

        var loops = new List<Task> { SyncWorkerAsync(stoppingToken) };
        if (_options.EnableServerEvents)
        {
            loops.Add(ServerEventsLoopAsync(stoppingToken));
        }

        await Task.WhenAll(loops);
    }

    /// <summary>
    /// 唯一的同步循环：等队列（超时即兜底对账）→ 取出最近的变更提示 → 同步一次。
    /// 队列空闲多久算「该主动对账了」由 SSE 连接状态决定，见 <see cref="IdleSyncInterval"/>。
    /// </summary>
    private async Task SyncWorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            BundlePublishedEvent? hint = null;

            try
            {
                var idle = IdleSyncInterval();
                if (idle > TimeSpan.Zero)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(idle);
                    try
                    {
                        if (!await _syncRequests.Reader.WaitToReadAsync(wait.Token))
                        {
                            return; // 队列已关闭
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 空闲超时：按全量对账继续往下走
                    }
                }
                else if (!await _syncRequests.Reader.WaitToReadAsync(ct))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 队列容量 1，最多取到一条；null 表示全量对账
            while (_syncRequests.Reader.TryRead(out var queued))
            {
                hint = queued;
            }

            try
            {
                await SyncOnceAsync(hint, ct);
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var failures = Interlocked.Increment(ref _consecutiveFailures);
                logger.LogWarning(ex, "同步 {Project} 语言包失败（第 {Count} 次），继续使用本地 JSON 兜底。", _options.ProjectCode, failures);

                // 失败后退避重试，不必等到下一个空闲周期
                _ = RetryLaterAsync(BackoffDelay(failures), ct);
            }
        }
    }

    /// <summary>
    /// 空闲对账周期：SSE 连着时事件本身就负责实时，这里只是「我是不是错过了什么」的保险，因此拉长；
    /// SSE 断开 / 未启用时才回到 PollInterval —— 那是长连接被环境封掉时唯一能追上线上版本的路径。
    /// </summary>
    private TimeSpan IdleSyncInterval() =>
        _options.EnableServerEvents && _serverEventsConnected ? SseIdleSyncInterval : _options.PollInterval;

    private async Task RetryLaterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            RequestSync();
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
    }

    /// <summary>
    /// 一次同步：有事件提示且载荷完整时走快速路径（直接拉变化的语种），否则拉 manifest 全量对账。
    /// </summary>
    private async Task SyncOnceAsync(BundlePublishedEvent? hint, CancellationToken ct)
    {
        if (hint is not null && await TrySyncFromEventAsync(hint, customClient: null, ct))
        {
            return;
        }

        await SyncAllAsync(ct: ct);
    }

    /// <summary>
    /// 按一条发布事件同步（可直接喂 SSE 事件载荷）：只拉 ETag 变化的语种，不请求 manifest。
    /// 载荷不完整（旧服务端）时退回 <see cref="SyncAllAsync"/> 的全量对账。
    /// </summary>
    public async Task SyncFromEventAsync(BundlePublishedEvent evt, HttpClient? customClient = null, CancellationToken ct = default)
    {
        if (!await TrySyncFromEventAsync(evt, customClient, ct))
        {
            await SyncAllAsync(customClient, ct);
        }
    }

    /// <summary>
    /// 按 SSE 事件里的版本指纹与各语种 ETag 直接拉取，省掉一次 manifest 往返。
    /// 载荷不完整（旧服务端 / 反序列化失败）或已持有该版本时返回 false / true：
    /// 前者交给 manifest 全量对账，后者无需任何动作。
    /// </summary>
    private async Task<bool> TrySyncFromEventAsync(BundlePublishedEvent evt, HttpClient? customClient, CancellationToken ct)
    {
        if (evt.Cultures.Count == 0 || string.IsNullOrWhiteSpace(evt.VersionHash))
        {
            return false;
        }

        if (string.Equals(_currentVersion, evt.VersionHash, StringComparison.Ordinal))
        {
            return true; // 已经是这个版本，事件只是重复通知
        }

        var client = customClient ?? httpClientFactory.CreateClient("LinguaClient");

        // SSE 事件同样携带本次发布的全部语种：重复条目只取第一个
        var entries = evt.Cultures
            .DistinctBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 事件的 ETag 就是分发端点的强 ETag；URL 用固定规则拼（优先扁平格式），不依赖 manifest
        var targets = entries
            .Select(c => (c.Culture, c.ETag, (string?)LinguaProtocol.FlatBundlePath(_options.ProjectCode, c.Culture)))
            .ToList();

        await DownloadCulturesAsync(targets, client, ct);

        // 默认语种与支持语种清单在**全部语种下载成功后**才生效（与全量对账一致）。
        // 实时路径也要更新协商列表，否则「平台新增语种」在纯 SSE 场景下要等下一次全量对账才生效。
        if (!string.IsNullOrWhiteSpace(evt.DefaultCulture))
        {
            memoryStore.SetDefaultCulture(evt.DefaultCulture);
        }
        memoryStore.SetSupportedCultures(entries.Select(c => c.Culture).ToList());

        // 全部语种成功后才推进版本（与全量对账一致）：中途失败会在下次触发时重试
        _currentVersion = evt.VersionHash;
        logger.LogInformation("项目 {Project} 已同步到发布版本 {Version}（SSE 事件驱动）。", _options.ProjectCode, evt.VersionHash);

        // 补一次确认，让平台「采用情况」及时看到这个实例已更新（失败不影响业务）
        await AckVersionAsync(
            client,
            LinguaProtocol.Combine(Endpoint, LinguaProtocol.ManifestPath(_options.ProjectCode)),
            ct);

        return true;
    }

    /// <summary>
    /// 并行下载 ETag 变化的语种（限流 <see cref="MaxParallelDownloads"/>）。
    /// 全部成功才算完成，任一失败直接抛出交由调用方退避重试；已成功的语种下次会被 ETag 跳过。
    /// 返回是否至少更新了一个语种。
    /// </summary>
    private async Task<bool> DownloadCulturesAsync(
        IReadOnlyList<(string Culture, string ETag, string? Url)> cultures,
        HttpClient client,
        CancellationToken ct)
    {
        var pending = cultures
            .DistinctBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
            .Where(c => !_cultureEtags.TryGetValue(c.Culture, out var held) || !string.Equals(held, c.ETag, StringComparison.Ordinal))
            .ToList();

        if (pending.Count == 0)
        {
            return false;
        }

        if (pending.Count == 1)
        {
            var only = pending[0];
            return await SyncCultureFileAsync(only.Culture, client, ct, only.Url);
        }

        var updated = 0;
        using var gate = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
        var tasks = pending.Select(async target =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await SyncCultureFileAsync(target.Culture, client, ct, target.Url))
                {
                    Interlocked.Increment(ref updated);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return updated > 0;
    }

    /// <summary>
    /// 订阅服务端 SSE：收到带 ETag 的发布事件就直接拉变化的语种。服务端每 15 秒发一次心跳，
    /// 超过 <see cref="SseIdleTimeout"/> 没有任何数据视为连接已死，重连；重连按指数退避（1s → 60s）加随机抖动，
    /// 连上后立即补一次全量对账（断线期间的事件一律视为丢失，而拉取的永远是当前线上状态）。
    /// </summary>
    private async Task ServerEventsLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = httpClientFactory.CreateClient("LinguaClient");
                client.Timeout = Timeout.InfiniteTimeSpan;

                using var req = NewRequest(LinguaProtocol.Combine(
                    Endpoint,
                    LinguaProtocol.StreamPath(_options.ProjectCode)));
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();

                attempt = 0;
                _serverEventsConnected = true;
                RequestSync();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(SseIdleTimeout);

                    var line = await reader.ReadLineAsync(idle.Token);
                    if (line is null)
                    {
                        break; // 服务端关闭了连接
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        // 解析失败（旧格式 / 残缺帧）时写 null，退回全量对账
                        _syncRequests.Writer.TryWrite(ParseEvent(line[5..].Trim()));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "SSE 连接中断，准备重连。");
            }
            finally
            {
                _serverEventsConnected = false;
            }

            try
            {
                await Task.Delay(BackoffDelay(++attempt), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 覆盖式 Move：Windows 上目标文件正被其它句柄（杀软扫描、编辑器、另一个实例的读取）短暂占用时会抛
    /// <see cref="UnauthorizedAccessException"/> / <see cref="IOException"/>，重试几次即可，不应让整次同步失败。
    /// </summary>
    private static void MoveWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * attempt));
            }
        }
    }

    private static BundlePublishedEvent? ParseEvent(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BundlePublishedEvent>(json, LinguaJson.Protocol);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SSE 连着时的空闲对账周期：事件负责实时，这里只是「我是不是错过了什么」的保险。</summary>
    public static TimeSpan SseIdleSyncInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>SSE 空闲超时：服务端心跳间隔 15 秒，连续三次收不到数据即判定断线。</summary>
    public static TimeSpan SseIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>退避：1s、2s、4s … 最多 60s，再加 0-1 秒随机抖动，避免大量实例同时重连。</summary>
    public static TimeSpan BackoffDelay(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, Math.Max(0, attempt - 1)), 60);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
    }

    /// <summary>同步成功后再确认一次 manifest（带上新版本号，服务端 304），让「采用情况」及时看到这个实例已更新。</summary>
    private async Task AckVersionAsync(HttpClient client, string manifestUrl, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(manifestUrl);
            if (!string.IsNullOrEmpty(_manifestEtag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", _manifestEtag);
            }

            using var _ = await client.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "上报采用版本失败（不影响业务）。");
        }
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation(ClientHeader, _clientId);
        if (_currentVersion is { } version)
        {
            req.Headers.TryAddWithoutValidation(VersionHeader, version);
        }

        return req;
    }

    private string Endpoint => _options.ServerEndpoint!.TrimEnd('/');

    private void StartLocalFileWatcher()
    {
        if (string.IsNullOrWhiteSpace(_options.LocalJsonDirectory))
        {
            return;
        }

        Directory.CreateDirectory(_options.LocalJsonDirectory);
        _watcher = new FileSystemWatcher(_options.LocalJsonDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += (_, e) => ReloadSingleFileSafe(e.FullPath);
        _watcher.Renamed += (_, e) => ReloadSingleFileSafe(e.FullPath);
    }

    private void ReloadSingleFileSafe(string fullPath)
    {
        if (!fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!LinguaCultureFiles.TryResolveCultureFileName(Path.GetFileNameWithoutExtension(fullPath), out var culture))
        {
            // 文件名不是合法 BCP47 语种码（如 supportedCultures.json 等元数据 / 杂项文件），变更无需热加载
            return;
        }
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length > 0)
            {
                memoryStore.UpdateCultureFromJsonBytes(culture, bytes);
            }
        }
        catch
        {
            // Ignore transient file lock while atomic move is in progress
        }
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// 开发环境缺失词条防抖上报器
/// </summary>
public sealed class HttpMissingKeyReporter(
    IOptions<LinguaClientOptions> optionsAccessor,
    IHttpClientFactory httpClientFactory,
    ILogger<HttpMissingKeyReporter>? logger = null) : IMissingKeyReporter
{
    private const string WriteKeyHeaderName = "X-Lingua-Key";

    private readonly LinguaClientOptions _options = optionsAccessor.Value;
    private readonly ConcurrentDictionary<string, bool> _reportedKeys = new(StringComparer.Ordinal);
    private int _warnedNoKey;

    public void ReportMissingKey(string culture, string fullKey, string? defaultText = null)
    {
        if (!_options.EnableMissingKeyTelemetry || string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.WriteKey))
        {
            // 服务端只接受携带项目写密钥的上报：开关开着但没配密钥，每次上报都会被 401 拒掉。
            // 每进程只警告一次（缺词可能高频触发，逐次记日志会刷屏）；不占用上报去重表，配上密钥后无需重启即可生效。
            if (Interlocked.Exchange(ref _warnedNoKey, 1) == 0)
            {
                logger?.LogWarning(
                    "缺词上报已开启但未配置 Lingua:WriteKey —— 上报会被服务端 401 拒绝。请在 Lingua 管理后台「接入密钥」面板获取后配置。");
            }
            return;
        }

        if (!_reportedKeys.TryAdd($"{culture}:{fullKey}", true))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var splitIdx = fullKey.IndexOfAny(['.', ':']);
                var ns = splitIdx > 0 ? fullKey[..splitIdx] : ""; // 无分隔符的裸 key 归属项目根
                var key = splitIdx > 0 ? fullKey[(splitIdx + 1)..] : fullKey;

                using var client = httpClientFactory.CreateClient("LinguaClient");
                var report = new MissingKeyBatchReport(
                    _options.ProjectCode,
                    culture,
                    [new MissingKeyReportItem(ns, key, defaultText ?? key)]);

                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    LinguaProtocol.Combine(
                        _options.ServerEndpoint.TrimEnd('/'),
                        LinguaProtocol.MissingKeysPath(_options.ProjectCode)))
                {
                    Content = JsonContent.Create(report)
                };
                request.Headers.Add(WriteKeyHeaderName, _options.WriteKey);

                using var _ = await client.SendAsync(request);
            }
            catch
            {
                // Telemetry failure should never impact business application
            }
        });
    }
}
