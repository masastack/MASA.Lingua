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
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Dictionary<string, string> _etags = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _clientId;
    private string? _manifestEtag;
    private string? _version;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LinguaLocalizer(
        LinguaClientOptions options,
        HttpClient? http = null,
        ILinguaMemoryStore? store = null,
        ILinguaChangeNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProjectCode))
        {
            throw new ArgumentException("必须设置 ProjectCode。", nameof(options));
        }

        _options = options;
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
        if (!IsSafeCultureName(culture))
        {
            return false;
        }

        var path = CachePath(culture);
        if (File.Exists(path) || utf8Json.IsEmpty)
        {
            return false;
        }

        var bytes = utf8Json.ToArray();
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
    /// 没配置 <see cref="LinguaClientOptions.ServerEndpoint"/>、或网络不可用 / 超时时，不抛异常，继续用缓存。
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

        await TrySyncAsync(ct);
    }

    /// <summary>
    /// 同 <see cref="SyncAsync"/>，但网络不可用 / 超时 / 服务端报错时不抛异常，返回 false，继续使用本机缓存。
    /// 适合在「回到前台」这类不方便处理异常的地方调用。调用方主动取消（<paramref name="ct"/>）仍会抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public async Task<bool> TrySyncAsync(CancellationToken ct = default)
    {
        try
        {
            return await SyncAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>
    /// 与平台同步一次：manifest 没变（304）或项目还没发布（404）返回 false；有语种更新返回 true。
    /// 建议在应用回到前台时调用，没变化时开销只是一次 304。并发调用会排队。出错时抛出异常。
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
                .Where(c => IsSafeCultureName(c.Culture))
                .DistinctBy(c => c.Culture, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 并行下载 ETag 变化或缓存缺失的语种（限流 <see cref="MaxParallelDownloads"/>）。
            // 任一失败会向上抛交由调用方处理；已成功的语种文件与内存都已更新，下次同步会被 ETag 跳过。
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
                        using var cultureRes = await _http.SendAsync(NewRequest(LinguaProtocol.Combine(endpoint, c.Url)), ct);
                        cultureRes.EnsureSuccessStatusCode();
                        var bytes = await cultureRes.Content.ReadAsByteArrayAsync(ct);

                        WriteCacheFile(c.Culture, bytes);
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
        finally
        {
            _syncGate.Release();
        }
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
        File.WriteAllBytes(temp, bytes);
        try
        {
            MoveWithRetry(temp, CachePath(culture)); // 原子替换，进程被杀也不会留下半个文件
        }
        finally
        {
            // Move 成功时 temp 已不存在；失败时兜底删除，避免缓存目录积累 .tmp 垃圾
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// 覆盖式 Move：Windows / 移动平台上目标文件被杀软扫描、索引器等句柄短暂占用时会抛
    /// <see cref="UnauthorizedAccessException"/> / <see cref="IOException"/>，重试几次即可，
    /// 不应让整次同步失败（与 AspNetCore 端 <c>LinguaJsonSyncService.MoveWithRetry</c> 同一套策略）。
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

    /// <summary>语种名会变成文件名：只接受普通的语种代码，拒绝路径分隔符等。</summary>
    private static bool IsSafeCultureName(string culture) =>
        !string.IsNullOrWhiteSpace(culture) &&
        culture.Length <= 35 &&
        culture.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
}
