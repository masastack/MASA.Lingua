using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Formatting;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MASA.Lingua.SDK.Maui;

/// <summary>
/// 客户端应用（MAUI / Blazor Hybrid / WPF 等）的 Lingua 本地化入口：
/// 1. 语言包缓存在本机磁盘，启动时先读缓存（离线可用），再联网按 manifest 同步：应用每次重启都会拉取一次更新；
/// 2. 只下载 ETag 变化的语种，manifest 没变时是一次 304；ETag 会落盘，下次启动不重复下载；
/// 3. 提供可绑定的字符串索引器（XAML：<c>{Binding [order.submitBtn], Source=...}</c>），语种切换或同步到新内容时通知界面刷新；
/// 4. 语种列表与默认语种完全以平台 manifest 为准，客户端不配置。
/// 没有常驻轮询，也不订阅 SSE（移动端后台会被系统挂起、长连接会被掐断），更新只在应用启动时拉取；
/// 需要更及时（如「回到前台」）由应用自行调用 <see cref="TrySyncAsync"/> / <see cref="SyncAsync"/>。
/// 它不依赖 MAUI 工作负载，任何 .NET 8 客户端都能用。
/// </summary>
/// <remarks>
/// <para>
/// 线程契约：<see cref="InitializeAsync"/> / <see cref="SyncAsync"/> / <see cref="TrySyncAsync"/> 内部**刻意不使用**
/// <c>ConfigureAwait(false)</c>，因此 <see cref="PropertyChanged"/> 与 <see cref="ILinguaChangeNotifier.OnCultureUpdated"/>
/// 都在**调用它们的上下文**里触发：在 UI 线程（<c>App</c> 构造、<c>OnResume</c>）发起同步，通知就在 UI 线程上，
/// 绑定刷新与 Blazor Hybrid 的 <c>StateHasChanged</c> 无需再切线程；从后台线程发起则通知也在后台线程上，由应用自行切回 UI 线程。
/// 不要给这些方法里的 await 补 <c>ConfigureAwait(false)</c>：那会让通知跑到线程池线程（有回归测试守着）。
/// </para>
/// </remarks>
public sealed class LinguaLocalizer : INotifyPropertyChanged, IDisposable
{
    private const string EtagFileName = "lingua.etags"; // 不用 .json 后缀，避免被当成语种文件加载

    /// <summary>并行下载语种的上限（与 AspNetCore 端 <c>LinguaJsonSyncService.MaxParallelDownloads</c> 一致）。</summary>
    private const int MaxParallelDownloads = 3;

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    private readonly LinguaClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILinguaMemoryStore _store;
    private readonly ILinguaChangeNotifier? _notifier;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Dictionary<string, string> _etags = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _clientId;
    private string? _manifestEtag;
    private string? _version;
    private volatile Exception? _lastSyncError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LinguaLocalizer(
        LinguaClientOptions options,
        HttpClient? http = null,
        ILinguaMemoryStore? store = null,
        ILinguaChangeNotifier? notifier = null,
        ILogger<LinguaLocalizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProjectCode))
        {
            throw new ArgumentException("必须设置 ProjectCode。", nameof(options));
        }

        _options = options;
        _logger = logger ?? NullLogger<LinguaLocalizer>.Instance;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _notifier = notifier;
        _store = store ?? new LinguaFrozenMemoryStore(notifier);
        _clientId = $"{options.ProjectCode}/{Guid.NewGuid().ToString("N")[..12]}";
        CacheDirectory = ResolveCacheDirectory(options);
        Culture = CultureInfo.CurrentUICulture.Name;
    }

    /// <summary>
    /// 语言包缓存目录。<see cref="LinguaClientOptions.LocalJsonDirectory"/> 是相对路径时，放在应用私有的本地数据目录下：
    /// <c>{LocalApplicationData}/Lingua/{项目}/{LocalJsonDirectory}</c>
    /// （Android / iOS 上是应用沙盒内的私有目录，卸载应用时一起删除）；绝对路径则原样使用。
    /// </summary>
    public string CacheDirectory { get; }

    /// <summary>当前语种，默认取设备的界面语种。平台没有发布该语种时，取词回退到平台默认语种。</summary>
    public string Culture { get; private set; }

    /// <summary>当前持有的平台发布版本指纹；还没同步过为 null。</summary>
    public string? Version => _version;

    /// <summary>
    /// 最近一次同步（<see cref="SyncAsync"/> / <see cref="TrySyncAsync"/> / <see cref="InitializeAsync"/> 里的联网部分）失败的异常；
    /// 之后一次同步成功（含「没变化」304、项目未发布 404）会清空为 null。调用方主动取消不算失败。
    /// <see cref="TrySyncAsync"/> 与 <see cref="InitializeAsync"/> 不向外抛异常，线上排查「为什么一直没更新」就看这里
    /// （例如酒店 Wi-Fi 登录页返回了 HTML、磁盘写满、<see cref="LinguaClientOptions.ServerEndpoint"/> 写错）。
    /// 失败也会以 Warning 级别写入 <see cref="ILogger{TCategoryName}"/>（应用注册了日志时，经 <c>AddLinguaMaui</c> 自动接入）。
    /// </summary>
    public Exception? LastSyncError => _lastSyncError;

    /// <summary>
    /// 平台 manifest 下发的语种清单（语言切换器的数据来源）。
    /// 启动时先用本机缓存里已有的语种兜底，manifest 对账成功后以平台为准。
    /// </summary>
    public IReadOnlyList<string> AvailableCultures { get; private set; } = [];

    /// <summary>取词；找不到（当前语种、中性语种、平台默认语种都没有）时返回 key 本身。key 里的 <c>:</c> 等价于 <c>.</c>。</summary>
    public string this[string key] => TryGetValue(key, out var value) ? value : key;

    public bool TryGetValue(string key, out string value)
    {
        // 客户端（MAUI）不做缺词上报：密钥无法安全保管在终端设备，且服务端只接受携带项目写密钥的上报。
        // 取词未命中直接返回 key 本身；漏翻由服务端应用的上报与 CI 检查发现。
        if (_store.TryGetValue(Culture, key, out value))
        {
            return true;
        }

        return false;
    }

    /// <summary>带占位符的取词：<c>Get("order.submitBtn", new { count = 3 })</c> 或位置参数 <c>{0}</c>。</summary>
    public string Get(string key, params object[] arguments) => LinguaInterpolation.Format(this[key], arguments);

    public IEnumerable<KeyValuePair<string, string>> GetAll(string? namespacePrefix = null) =>
        _store.GetAll(Culture, namespacePrefix);

    /// <summary>切换当前语种并通知界面刷新。</summary>
    public void SetCulture(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            throw new ArgumentException("语种不能为空。", nameof(culture));
        }

        Culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        RaiseItemsChanged();
        _notifier?.NotifyCultureUpdated(culture);
    }

    /// <summary>
    /// 放入应用包内置的语言包（例如 Resources/Raw 里 <c>lingua-sync pull</c> 拉下来的文件），作为首次安装且离线时的兜底。
    /// 只有缓存里还没有该语种时才生效；已有缓存（可能比内置的新）不会被覆盖。在 <see cref="InitializeAsync"/> 之前调用。
    /// 返回是否采用了内置内容。
    /// </summary>
    public bool Seed(string culture, ReadOnlySpan<byte> utf8Json)
    {
        if (!LinguaCultureFiles.IsSafeCultureName(culture))
        {
            return false;
        }

        // 语种名会变成文件名，先规范化：Android 文件系统区分大小写、iOS（APFS 默认）不区分，
        // 不规范化的话 Seed("zh-cn") 在 Android 上会写出一个与 manifest 的 zh-CN.json 并存的第二个文件，
        // 两个平台的缓存内容随之分叉。规范化不了的写法（通过了 IsSafeCultureName 但不符合 ll[-Ssss][-RR] 形状）保持原样，不误拒。
        if (LinguaCultureFiles.TryResolveCultureFileName(culture, out var normalized))
        {
            culture = normalized;
        }

        var path = CachePath(culture);
        if (File.Exists(path) || utf8Json.IsEmpty)
        {
            return false;
        }

        var bytes = utf8Json.ToArray();
        // 顺序：校验 → 原子落盘 → 最后更新内存，任何一步失败都不会留下「半个」状态。
        // 先校验：内容不是合法 JSON 时这里抛出，不会留下一个永远挡住后续 Seed（File.Exists 为真）的坏缓存文件。
        // 落盘在内存之前：写盘失败（磁盘满 / 权限 / 被杀软占用）时内存还没动，
        // 不会出现「本次会话有种子内容、重启后消失」的内存与磁盘分叉。
        // 反方向（文件已落盘、内存没更新）是安全且自愈的：下次启动 LoadFromLocalDirectory 会把它读回来。
        // 校验用的字典直接丢弃：store 的接口只收字节，会在内部再解析一次。代价只在「内容真的变了」时才付（启动时读缓存不经过这里）。
        JsonBundleConverter.FlattenToLookupDictionary(bytes);
        WriteCacheFile(culture, bytes);
        _store.UpdateCultureFromJsonBytes(culture, bytes);
        return true;
    }

    /// <summary>流版本，配合 MAUI：<c>using var s = await FileSystem.OpenAppPackageFileAsync("zh-CN.json");</c></summary>
    public bool Seed(string culture, Stream json)
    {
        using var ms = new MemoryStream();
        json.CopyTo(ms);
        return Seed(culture, ms.ToArray());
    }

    /// <summary>
    /// 启动时调用一次：先读本机缓存（立即可用，离线也行），再联网拉取一次更新——应用每次重启都会经过这里。
    /// 这是客户端唯一的内置拉取时机（没有常驻轮询）；需要更及时的更新（如「回到前台」）由应用自行调用 <see cref="TrySyncAsync"/>。
    /// 没配置 <see cref="LinguaClientOptions.ServerEndpoint"/>、或联网同步失败（网络不可用 / 超时 / 服务端或响应异常 / 磁盘写不进）时，
    /// 不抛异常，继续用缓存；失败原因见 <see cref="LastSyncError"/>。
    /// 不建议在应用启动路径上 <c>await</c> 它：弱网 / 黑洞网络下联网部分可能要等到 HTTP 超时才返回，
    /// 推荐 <c>_ = lingua.InitializeAsync();</c>（本机缓存的读取在第一个 <c>await</c> 之前同步完成，页面立即有文案）。
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _store.LoadFromLocalDirectory(CacheDirectory);
        LoadEtags();
        AvailableCultures = _store.GetLoadedCultures(); // 联网前先给语言切换器一个缓存兜底
        RaiseItemsChanged();

        if (string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return;
        }

        // 注意：这里（以及本类所有 await）刻意不加 ConfigureAwait(false)，见类注释里的线程契约。
        await TrySyncAsync(ct);
    }

    /// <summary>
    /// 同 <see cref="SyncAsync"/>，但除调用方主动取消（<paramref name="ct"/>，仍会抛出 <see cref="OperationCanceledException"/>）外，
    /// 任何失败都不抛异常、返回 false、继续使用本机缓存；失败原因记录在 <see cref="LastSyncError"/>。
    /// 失败包括：网络不可用 / 超时 / 服务端报错、响应不是合法 JSON（例如酒店 Wi-Fi 登录页返回了 HTML）、磁盘写不进、地址配错等。
    /// 适合在「回到前台」这类不方便处理异常的地方调用。
    /// </summary>
    public async Task<bool> TrySyncAsync(CancellationToken ct = default)
    {
        try
        {
            return await SyncAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 异常已由 SyncAsync 记入 LastSyncError，这里只负责不向外抛
            return false;
        }
    }

    /// <summary>
    /// 与平台同步一次：manifest 没变（304）或项目还没发布（404）返回 false；有语种更新返回 true。
    /// 建议在应用回到前台时调用，没变化时开销只是一次 304。并发调用会排队。出错时抛出异常（同时记入 <see cref="LastSyncError"/>）。
    /// </summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ServerEndpoint))
        {
            return false;
        }

        await _syncGate.WaitAsync(ct);
        try
        {
            // 结果记录在闸门之内：并发调用按闸门串行执行，LastSyncError 因此始终对应「最后完成的那一次同步」。
            // 记在闸门之外的话，先失败的调用可能在后成功的调用之后才写入，把已经恢复的错误又写回来。
            try
            {
                var changed = await SyncCoreAsync(ct);
                _lastSyncError = null;
                return changed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _lastSyncError = ex;
                // TrySyncAsync / InitializeAsync 不向外抛，日志是线上排查「为什么一直没更新」的第二条线索
                _logger.LogWarning(ex, "Lingua 同步失败（项目 {ProjectCode}），继续使用本机缓存。", _options.ProjectCode);
                throw;
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    /// <summary>同步的实际工作；调用方必须已持有 <see cref="_syncGate"/> 且已确认配置了 ServerEndpoint。</summary>
    private async Task<bool> SyncCoreAsync(CancellationToken ct)
    {
        var endpoint = _options.ServerEndpoint!.TrimEnd('/');
        var manifestUrl = LinguaProtocol.Combine(endpoint, LinguaProtocol.ManifestPath(_options.ProjectCode));

        using var req = NewRequest(manifestUrl);
        // manifest ETag 只能证明线上没变，不能证明本机文件还在（可能被系统清理 / 用户删除）。
        // 只要「我们持有 ETag 的语种」缺了缓存文件，就不带 If-None-Match 拉全量 manifest，
        // 让下面的下载条件（!File.Exists）把这些语种补回来 —— 否则 304 会永远跳过补下载。
        if (!string.IsNullOrEmpty(_manifestEtag) && !HasMissingCacheFile())
        {
            req.Headers.TryAddWithoutValidation("If-None-Match", _manifestEtag);
        }

        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotModified or HttpStatusCode.NotFound)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        var manifest = await res.Content.ReadFromJsonAsync<ProjectManifest>(ManifestJson, ct)
                       ?? throw new InvalidOperationException("manifest 响应为空。");

        // manifest 里重复的语种条目只取第一个：两份一样的并行任务会同时写同一个缓存文件
        var entries = manifest.Cultures
            .Where(c => LinguaCultureFiles.IsSafeCultureName(c.Culture))
            .DistinctBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 并行下载 ETag 变化或缓存缺失的语种（限流 <see cref="MaxParallelDownloads"/>）。
        // 任一失败会向上抛交由调用方处理；已成功的语种文件与内存都已更新，下次同步会被 ETag 跳过。
        // 每个语种内部「校验 → 原子落盘 → 最后更新内存」：响应内容不是合法 JSON（代理 / 登录页改写等）时
        // 在磁盘与内存都还没动的阶段就抛出；写盘失败（磁盘满 / 权限）时内存与 ETag 也保持原样，
        // 不会出现「界面已是新文案、重启后又回到旧缓存」的内存与磁盘分叉。两种情况 ETag 都不推进，下次同步会重试。
        var changedCount = 0;
        var pending = entries
            .Where(c => !_etags.TryGetValue(c.Culture, out var held) || held != c.ETag || !File.Exists(CachePath(c.Culture)))
            .ToList();

        if (pending.Count > 0)
        {
            using var gate = new SemaphoreSlim(MaxParallelDownloads, MaxParallelDownloads);
            var tasks = pending.Select(async c =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // .NET 客户端优先下载扁平格式：没有键路径冲突的表达缺陷，拍平后的查询 key 与嵌套格式完全一致
                    using var cultureRes = await _http.SendAsync(
                        NewRequest(LinguaProtocol.Combine(endpoint, string.IsNullOrWhiteSpace(c.FlatUrl) ? c.Url : c.FlatUrl)), ct);
                    cultureRes.EnsureSuccessStatusCode();
                    var bytes = await cultureRes.Content.ReadAsByteArrayAsync(ct);

                    JsonBundleConverter.FlattenToLookupDictionary(bytes); // 非法 JSON 在这里抛出，磁盘与内存都不会被改
                    await WriteCacheFileAsync(c.Culture, bytes, ct);       // 写盘失败在这里抛出，内存与 ETag 仍是旧的
                    _store.UpdateCultureFromJsonBytes(c.Culture, bytes);
                    lock (_etags)
                    {
                        _etags[c.Culture] = c.ETag;
                    }
                    Interlocked.Increment(ref changedCount);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        var changed = changedCount > 0;

        // 默认语种与语种清单在**全部语种下载成功后**才生效：
        // 中途失败时语言切换器不会列出磁盘上还没有的语种，下次同步成功才会更新
        var cultures = entries.Select(c => c.Culture).ToList();
        _store.SetDefaultCulture(manifest.DefaultCulture);
        _store.SetSupportedCultures(cultures);
        AvailableCultures = cultures;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableCultures)));

        // 平台已移除的语种顺手清掉过期的 ETag 记录（缓存文件保留给离线兜底）：
        // 否则该文件一旦丢失，HasMissingCacheFile 会让之后每次同步都退回全量 manifest
        lock (_etags)
        {
            var alive = new HashSet<string>(cultures, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _etags.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _etags.Remove(stale);
            }
        }

        // 全部语种都成功后才推进版本与 manifest ETag；中途失败，下次同步会重试
        var versionChanged = !string.Equals(_version, manifest.Version, StringComparison.Ordinal);
        _version = manifest.Version;
        _manifestEtag = res.Headers.ETag?.Tag ?? manifest.ETag;
        SaveEtags();

        if (changed)
        {
            RaiseItemsChanged();
        }

        if (versionChanged)
        {
            await AckVersionAsync(manifestUrl, ct);
        }

        return changed;
    }

    /// <summary>
    /// 释放同步闸门与自建的 HttpClient。不启动任何后台任务，无需停止。
    /// </summary>
    public void Dispose()
    {
        _syncGate.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation(LinguaProtocol.ClientHeader, _clientId);
        if (_version is { } version)
        {
            req.Headers.TryAddWithoutValidation(LinguaProtocol.VersionHeader, version);
        }

        return req;
    }

    /// <summary>同步成功后再确认一次 manifest（带新版本号，服务端 304），让平台「采用情况」及时看到这台设备已更新。</summary>
    private async Task AckVersionAsync(string manifestUrl, CancellationToken ct)
    {
        try
        {
            using var req = NewRequest(manifestUrl);
            if (!string.IsNullOrEmpty(_manifestEtag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", _manifestEtag);
            }

            using var _ = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 上报失败不影响业务
        }
    }

    private void RaiseItemsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // 让所有索引器绑定重新取值

    private string CachePath(string culture) => Path.Combine(CacheDirectory, $"{culture}.json");

    /// <summary>我们持有 ETag 的语种中，是否已有缓存文件丢失（被系统清理 / 用户删除）。</summary>
    private bool HasMissingCacheFile()
    {
        lock (_etags)
        {
            return _etags.Keys.Any(culture => !File.Exists(CachePath(culture)));
        }
    }

    private void WriteCacheFile(string culture, byte[] bytes)
    {
        Directory.CreateDirectory(CacheDirectory);
        var temp = Path.Combine(CacheDirectory, $"{culture}.{Guid.NewGuid():N}.tmp");
        // 写入本身也在 try 内：磁盘写满时 WriteAllBytes 会在写到一半抛出，已经落下的半截临时文件同样要清掉
        try
        {
            File.WriteAllBytes(temp, bytes);
            MoveWithRetry(temp, CachePath(culture)); // 原子替换，进程被杀也不会留下半个文件
        }
        finally
        {
            // Move 成功时 temp 已不存在；任何失败路径都兜底删除，避免缓存目录积累 .tmp 垃圾
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// <see cref="WriteCacheFile"/> 的异步版：网络同步路径（<see cref="SyncCoreAsync"/> 的下载循环）专用。
    /// 重试等待走 <see cref="Task.Delay"/> 而不是 Thread.Sleep——同步是少数可能在 UI 线程上被 await 的入口
    /// （文档建议 fire-and-forget，但应用常在 App 构造 / 回到前台时直接 await），Thread.Sleep 会卡住 UI 线程。
    /// 刻意**不**加 ConfigureAwait(false)：与类级线程契约一致，后续通知仍要回到调用方的上下文。
    /// </summary>
    private async Task WriteCacheFileAsync(string culture, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDirectory);
        var temp = Path.Combine(CacheDirectory, $"{culture}.{Guid.NewGuid():N}.tmp");
        // 写入本身也在 try 内：磁盘写满 / 写到一半被取消时，已经落下的半截临时文件同样要清掉。
        // 否则这正是「磁盘写满」场景下的反向放大——每次回到前台的重试都往缓存目录多留一个半截文件，把磁盘越填越满。
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct);
            await MoveWithRetryAsync(temp, CachePath(culture), ct); // 原子替换，进程被杀也不会留下半个文件
        }
        finally
        {
            // Move 成功时 temp 已不存在；任何失败路径都兜底删除，避免缓存目录积累 .tmp 垃圾
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// 覆盖式 Move：Windows / 移动平台上目标文件被杀软扫描、索引器等句柄短暂占用时会抛
    /// <see cref="UnauthorizedAccessException"/> / <see cref="IOException"/>，重试几次即可，不应让整次同步失败。
    /// 同步版只服务 <see cref="Seed"/>（应用启动路径由应用代码直接调用，且目标文件此前不存在、重试极罕见）；
    /// 网络同步路径一律走 <see cref="MoveWithRetryAsync"/>——Thread.Sleep 在 UI 线程上会卡界面。
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

    /// <summary><see cref="MoveWithRetry"/> 的异步版：重试等待不占用调用线程（UI 线程 / 线程池）。</summary>
    private static async Task MoveWithRetryAsync(string source, string destination, CancellationToken ct)
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
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt), ct);
            }
        }
    }

    private void LoadEtags()
    {
        _etags.Clear();
        try
        {
            var path = Path.Combine(CacheDirectory, EtagFileName);
            if (!File.Exists(path))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            foreach (var (culture, etag) in saved ?? [])
            {
                _etags[culture] = etag;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 损坏就当没有，下次同步会重新下载
        }
    }

    private void SaveEtags()
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(Path.Combine(CacheDirectory, EtagFileName), JsonSerializer.Serialize(_etags));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不了只是下次启动多下载一次
        }
    }

    private static string ResolveCacheDirectory(LinguaClientOptions options)
    {
        var dir = string.IsNullOrWhiteSpace(options.LocalJsonDirectory) ? "i18n" : options.LocalJsonDirectory;
        if (Path.IsPathRooted(dir))
        {
            return dir;
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Lingua", SafeSegment(options.ProjectCode), dir);
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}
