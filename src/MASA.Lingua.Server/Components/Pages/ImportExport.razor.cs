using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Server.Services;

namespace MASA.Lingua.Server.Components.Pages;

public partial class ImportExport
{
    /// <summary>页签：发布（默认）/ 导入 / 历史 / 分发。低频内容不再与主操作平铺在一屏里。</summary>
    private enum ReleaseTab { Publish, Import, History, Distribution }

    private ReleaseTab _tab = ReleaseTab.Publish;

    private bool _policyDrawer;

    /// <summary>项目元数据用轻量概览：页面只需要语种 / 策略 / 最近发布结果，不为读几个字段加载全部词条。</summary>
    private List<ProjectOverview> _projects = [];
    private string _importCulture = "zh-CN";
    private string _previewCulture = "zh-CN";
    private string _previewEtag = "—";
    private string _previewJsonContent = "";
    // 提示状态由 LinguaPageBase 提供（_statusMessage / _statusIsError / ShowStatusError）
    private bool _isBusy;

    // _isLoading（加载骨架标记）来自 LinguaPageBase。

    private JsonImportResult? _importResult;
    private bool _publishAfterImport;

    private string _jsonText = "{\n  \"checkout\": {\n    \"couponPlaceholder\": \"请输入优惠券码\",\n    \"payNow\": \"立即支付 {amount} 元\"\n  }\n}";

    private ProjectOverview? Project =>
        Scope.IsShared ? null : _projects.FirstOrDefault(p => p.Code == Scope.ProjectCode);

    /// <summary>页头摘要里的「草稿」状态：尚未发布 / 有新改动 / 与线上一致（检查报告未回来前留空）。</summary>
    private string DraftStateLabel => _diff switch
    {
        null => "—",
        { NeverPublished: true } => "尚未发布过",
        { HasChanges: true } diff => $"比线上新 {diff.ChangedKeyCount} 条",
        _ => "与线上一致"
    };

    private static string GateLabel(PublishCheckReport report) =>
        report.CanPublish ? "门禁通过" : $"阻断 {report.Blockers.Count} 项";

    /// <summary>门禁未过且没有勾选强制发布时不能点发布：直接禁用比让用户点下去再吃一次错误更清楚。</summary>
    private bool PublishDisabled => _isBusy || (_report is { CanPublish: false } && !_forcePublish);

    private string PublishTitle => _report is { CanPublish: false } && !_forcePublish
        ? "未通过发布前检查：到「发布」页签处理阻断问题，或勾选强制发布并填写原因"
        : "过发布门禁后生成各语种语言包，并通过 SSE 通知业务端重新同步";

    /// <summary>
    /// 导入语种下拉里除 ICU 目录外必须可选的代码：项目已支持的语种 + 默认语种。
    /// 这些可能是真实存在但 ICU 不枚举的代码（如 zh-CN），漏了就没法导入到它们名下。
    /// <para>
    /// 缓存为字段、随 <see cref="ReloadAsync"/> 重建：CultureSelect 靠「集合实例是否同一份」判断要不要
    /// 重建约 900 项的候选来源，每次渲染现算现建新集合会让那份缓存永远失效。
    /// </para>
    /// </summary>
    private IReadOnlyCollection<string> _importCultureCandidates = [LinguaCultures.DefaultSourceCulture];

    private IReadOnlyCollection<string> ImportCultureCandidates => _importCultureCandidates;

    // 加载守卫（_loadVersion / _disposed / _isLoading / NextLoadVersion / IsCurrent / EndLoad）来自 LinguaPageBase。

    protected override async Task OnInitializedAsync()
    {
        Scope.Changed += OnScopeChanged;
        try
        {
            await ReloadAsync(resetCultures: true);
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载发布数据失败：{ex.Message}");
        }
    }

    protected override void OnDisposing()
    {
        Scope.Changed -= OnScopeChanged;
    }

    /// <summary>切页签：不重载数据，只有「分发」在首次打开时才去编译预览（见 <see cref="EnsurePreviewAsync"/>）。</summary>
    private async Task SelectTabAsync(ReleaseTab tab)
    {
        _tab = tab;
        _rollbackConfirmId = null;

        if (tab != ReleaseTab.Distribution)
        {
            return;
        }

        // 先把页签切过去再编译产物：否则点击后要等整个 bundle 编译完页面才有反应
        StateHasChanged();
        await EnsurePreviewAsync();
    }

    private void OpenPolicyDrawer() => _policyDrawer = true;

    private void ClosePolicyDrawer() => _policyDrawer = false;

    /// <summary>
    /// 顶栏切换项目：先清掉上一个项目的导入结果与发布状态、立即显示加载骨架，
    /// 数据到位后一次性渲染新项目内容——避免切换期间长时间停留在旧数据上。
    /// fire-and-forget 的事件回调：异常在这里接住并落到状态栏，否则会变成未观察异常静默丢失、页面停在加载态。
    /// </summary>
    private void OnScopeChanged() => _ = InvokeAsync(async () =>
    {
        if (_disposed)
        {
            return;
        }

        _importResult = null;
        _statusMessage = null;
        ClearFile();
        _forcePublish = false;
        _forceReason = "";
        _policyDrawer = false;
        _tab = ReleaseTab.Publish;
        _report = null;
        _diff = null;
        _history = [];
        _historyTotalCount = 0;
        _historyPageIndex = 0;
        _adoption = AdoptionSummary.Empty;
        _previewEtag = "—";
        _previewJsonContent = "";
        _previewTruncated = false;
        _previewLoaded = false;
        _isLoading = true;
        StateHasChanged();
        try
        {
            await ReloadAsync(resetCultures: true);
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载 {Scope.ProjectCode} 的发布数据失败：{ex.Message}");
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    });

    /// <summary>项目的默认导入语种与预览语种：导入取项目默认语种；预览优先默认语种，其次第一个支持的语种。</summary>
    private static (string Import, string Preview) CultureDefaults(ProjectOverview? project) =>
        project is null
            ? ("zh-CN", "zh-CN")
            : (project.DefaultCulture,
                project.SupportedCultures.Contains(project.DefaultCulture)
                    ? project.DefaultCulture
                    : project.SupportedCultures.FirstOrDefault() ?? "zh-CN");

    /// <summary>文件导入的大小上限。文件内容只留在服务端，不回显进文本框（见 <see cref="_fileBytes"/>）。</summary>
    private const int MaxImportFileBytes = 5 * 1024 * 1024;

    /// <summary>
    /// 文本框粘贴上限（字符）。粘贴的内容失焦时会整段回传服务端，而 SignalR 单条消息有大小上限
    /// （Program.cs 里配置为 1MB）：25 万字符按 UTF-8 最坏 3 字节算也不到 1MB。更大的 JSON 请走文件选择。
    /// </summary>
    private const int PasteMaxChars = 250_000;

    /// <summary>
    /// 已选择文件的内容（UTF-8 字节）。以前读成字符串塞进文本框：最大 5MB 的文本会经 SignalR 整段下发到浏览器，
    /// 用户再编辑失焦时又整段回传，超过默认 32KB 的接收上限会直接断开电路。现在文件内容只留在服务端，页面只显示文件名与大小。
    /// 有文件时导入以文件为准；点「改用文本框」清掉。
    /// </summary>
    private byte[]? _fileBytes;

    private string? _fileName;

    /// <summary>InputFile 的重建键：清掉已选文件时换键，强制浏览器端的文件选择框一并复位。</summary>
    private int _fileInputKey;

    private async Task OnFileSelectedAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        try
        {
            await using var stream = file.OpenReadStream(maxAllowedSize: MaxImportFileBytes);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _fileBytes = Encoding.UTF8.GetBytes(await reader.ReadToEndAsync());
            _fileName = file.Name;
        }
        catch (IOException ex)
        {
            // 超过大小上限等：OpenReadStream / 读取抛出的 IOException 不接住会让整个电路报错
            ClearFile();
            ShowStatusError($"读取文件失败：{ex.Message}");
            return;
        }

        // 文件名里带语种时自动选中（如 en-US.json、app.zh-CN.json）：去掉扩展名后取最后一个点分段再规范化
        //（app.zh-CN → zh-CN；单段文件名如 en-US.json 取自身）。
        // 合法性判定与校验同一口径（ICU 预定义文化），不看目录枚举结果——否则 zh-CN.json 这种最常见的文件名会被忽略。
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        var candidate = stem.Contains('.') ? stem[(stem.LastIndexOf('.') + 1)..] : stem;
        if (LinguaCultures.Normalize(candidate) is { } normalized && LinguaCultures.IsKnownCulture(normalized))
        {
            _importCulture = normalized;
        }

        _statusMessage = null;
    }

    /// <summary>导入语种下拉：选中即用，不需要额外的「确认」动作。</summary>
    private void OnImportCultureChanged(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return;
        }

        _importCulture = culture;
        _importResult = null;
        _statusMessage = null;
    }

    private void ClearFile()
    {
        _fileBytes = null;
        _fileName = null;
        _fileInputKey++;
    }

    private static string FormatSize(int bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private async Task ImportJsonAsync()
    {
        var project = Project;
        var bytes = _fileBytes ?? (string.IsNullOrWhiteSpace(_jsonText) ? null : Encoding.UTF8.GetBytes(_jsonText));
        if (project is null || bytes is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            _importResult = await Repository.ImportJsonBundleAsync(project.Code, _importCulture, bytes);
            _importCulture = _importResult.Culture;
            _statusMessage = null;

            if (_publishAfterImport)
            {
                try
                {
                    await Publisher.PublishProjectAsync(project.Code, $"导入 {_importCulture}.json 后发布");
                }
                catch (PublishBlockedException ex)
                {
                    ShowStatusError($"导入已写入草稿，但没有发布：{ex.Message}。");
                }
            }

            // 导入可能新增了语种：重新读取项目，让语种切换器、发布检查与预览同步
            _previewCulture = _importCulture;
            _previewLoaded = false;
            await ReloadAsync(resetCultures: false);
        }
        catch (LinguaValidationException ex)
        {
            _importResult = null;
            ShowStatusError(ex.Message);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _importResult = null;
            ShowStatusError($"不是合法的 JSON：{ex.Message}");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task SelectPreviewAsync(string culture)
    {
        _previewCulture = culture;
        try
        {
            var preview = await LoadPreviewAsync(Project?.Code, culture);
            if (_disposed)
            {
                return;
            }

            _previewLoaded = true;
            if (string.Equals(culture, _previewCulture, StringComparison.Ordinal))
            {
                ApplyPreview(preview); // 加载期间用户又点了别的语种：这份结果已过期
            }
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载预览失败：{ex.Message}");
        }
    }

    /// <summary>「分发」页签首次打开时才编译预览：未打开时不把 MB 级产物读出来，也不往渲染树里塞 20K 字符。</summary>
    private async Task EnsurePreviewAsync()
    {
        if (_previewLoaded)
        {
            return;
        }

        var projectCode = Project?.Code;
        var culture = _previewCulture;
        try
        {
            var preview = await LoadPreviewAsync(projectCode, culture);
            if (_disposed)
            {
                return;
            }

            // 只有成功取回才记「已加载」：失败时下次切回页签会重试，而不是永远停在空预览
            _previewLoaded = true;
            if (string.Equals(culture, _previewCulture, StringComparison.Ordinal))
            {
                ApplyPreview(preview);
            }
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载预览失败：{ex.Message}");
        }
    }

    private AdoptionSummary _adoption = AdoptionSummary.Empty;

    private void RefreshAdoption()
    {
        if (Project is { } project)
        {
            _adoption = Adoption.GetSummary(project.Code);
        }
    }

    private PublishCheckReport? _report;
    private DraftDiff? _diff;
    private IReadOnlyList<PublishSnapshot> _history = [];
    private bool _forcePublish;
    private string _forceReason = "";
    private Guid? _rollbackConfirmId;

    // ---- 发布历史分页：改页码 / 每页条数只重查历史这一块，不重跑发布概览与预览 ----
    private static readonly int[] HistoryPageSizeOptions = [10, 20, 50];
    private int _historyPageIndex; // 0 起始
    private int _historyPageSize = 20;
    private int _historyTotalCount;

    private int HistoryPageIndex => Math.Min(_historyPageIndex, Math.Max(0, HistoryTotalPages - 1));
    private int HistoryTotalPages => Math.Max(1, (int)Math.Ceiling(_historyTotalCount / (double)_historyPageSize));

    private PublishContentMode _policyContent = PublishContentMode.ApprovedAndAi;
    private int _policyMinCoverage;
    private bool _policyBlockErrors = true;
    private bool _policyBlockGlossary = true;

    private static string ContentModeLabel(PublishContentMode mode) => mode switch
    {
        PublishContentMode.ApprovedOnly => "仅已审核",
        PublishContentMode.ApprovedAndAi => "已审核 + AI 已译",
        _ => "全部"
    };

    /// <summary>导入 / 发布 / 回滚 / 保存策略后刷新：保留用户刚操作出的状态（如导入结果与预览语种），不重置语种默认值。</summary>
    private Task RefreshAllAsync() => ReloadAsync(resetCultures: false);

    /// <summary>
    /// 统一加载：轻量概览先到位（决定语种默认值），随后发布概览（检查+差异）、发布历史页、采用情况并行取回。
    /// 发布概览内部只做一次聚合加载（检查与差异共用），不再串行打两轮全量查询。
    /// <para>
    /// 预览不在这里加载：它要编译整个产物，改为切到「分发」页签时按需取（见 <see cref="EnsurePreviewAsync"/>）。
    /// </para>
    /// </summary>
    /// <param name="resetCultures">是否把导入 / 预览语种重置为项目默认值（切换项目时为 true）。</param>
    /// <param name="policyOnly">
    /// 只刷新受发布策略影响的部分（策略字段、发布检查、草稿差异）。保存策略既不产生发布记录也不改线上产物，
    /// 发布历史与线上预览不会变，没必要重新读库并解码一遍。
    /// </param>
    private async Task ReloadAsync(bool resetCultures, bool policyOnly = false)
    {
        var version = NextLoadVersion();
        _isLoading = true;
        try
        {
            // 全部结果先放进局部变量，所有查询返回、确认自己仍是最新一次加载后才一次性赋给页面状态：
            // 连续切换项目时，较早发起的加载不会用旧项目的发布检查 / 历史 / 预览覆盖新项目。
            var projects = (await Repository.GetProjectOverviewsAsync()).ToList();
            var project = Scope.IsShared ? null : projects.FirstOrDefault(p => p.Code == Scope.ProjectCode);
            var projectCode = project?.Code;

            var (importCulture, previewCulture) = resetCultures
                ? CultureDefaults(project)
                : (_importCulture, _previewCulture);

            var historyTask = projectCode is null || policyOnly
                ? Task.FromResult(new PublishSnapshotPage(_history, _historyTotalCount))
                : Repository.GetPublishSnapshotPageAsync(
                    projectCode, HistoryPageIndex * _historyPageSize, _historyPageSize);

            // 发布概览（检查 + 差异）是重活，与历史并行跑
            ReleaseOverview? overview = projectCode is null ? null : await Publisher.GetReleaseOverviewAsync(projectCode);
            var history = await historyTask;
            var adoption = project is { } p ? Adoption.GetSummary(p.Code) : AdoptionSummary.Empty;

            if (!IsCurrent(version))
            {
                return;
            }

            _projects = projects;
            // 导入语种下拉的候选补齐代码跟着项目走（见 _importCultureCandidates 的缓存说明）
            _importCultureCandidates = project is null
                ? [LinguaCultures.DefaultSourceCulture]
                : [.. project.SupportedCultures, project.DefaultCulture];
            _importCulture = importCulture;
            _previewCulture = previewCulture;
            _history = history.Items;
            _historyTotalCount = history.TotalCount;
            _adoption = adoption;
            _report = overview?.Report;
            _diff = overview?.Diff;

            if (project is { } policyProject)
            {
                var policy = policyProject.PublishPolicy;
                _policyContent = policy.Content;
                _policyMinCoverage = policy.MinCoveragePercent;
                _policyBlockErrors = policy.BlockOnErrors;
                _policyBlockGlossary = policy.BlockOnGlossaryViolations;
            }
        }
        finally
        {
            // 加载态只由最新一次加载结束；被取代的旧加载不动它（新的加载会负责收尾）
            if (IsCurrent(version))
            {
                _isLoading = false;
                _rollbackConfirmId = null;
            }

            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    }

    private Task GoToHistoryPageAsync(int page)
    {
        _historyPageIndex = Math.Clamp(page, 0, Math.Max(0, HistoryTotalPages - 1));
        _rollbackConfirmId = null;
        return LoadHistoryAsync();
    }

    /// <summary>每页条数改回第一页：原来的页码可能已越界，从第 1 页重查最直观。</summary>
    private Task ResetHistoryPageAsync()
    {
        _historyPageIndex = 0;
        return LoadHistoryAsync();
    }

    /// <summary>
    /// 只刷新当前页发布历史：不占用加载代数（否则会把同时在跑的 <see cref="ReloadAsync"/> 判为过期），
    /// 改为比对「发起时的项目与页码」是否仍是当前值，避免快速连点翻页时旧页结果覆盖新页。
    /// </summary>
    private async Task LoadHistoryAsync()
    {
        if (Project is not { } project)
        {
            return;
        }

        var code = project.Code;
        var pageIndex = HistoryPageIndex;
        try
        {
            var page = await Repository.GetPublishSnapshotPageAsync(
                code, pageIndex * _historyPageSize, _historyPageSize);

            if (_disposed || code != Project?.Code || pageIndex != HistoryPageIndex)
            {
                return;
            }

            _history = page.Items;
            _historyTotalCount = page.TotalCount;
            _rollbackConfirmId = null;
        }
        catch (Exception ex)
        {
            // 翻页是页面内的局部动作：失败只提示，不让异常冒到电路上把整个页面打断
            ShowStatusError($"加载发布历史失败：{ex.Message}");
        }
    }

    private async Task SavePolicyAsync()
    {
        var project = Project;
        if (project is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            await Repository.SavePublishPolicyAsync(project.Code, new PublishPolicy
            {
                Content = _policyContent,
                MinCoveragePercent = _policyMinCoverage,
                BlockOnErrors = _policyBlockErrors,
                BlockOnGlossaryViolations = _policyBlockGlossary
            });
            await ReloadAsync(resetCultures: false, policyOnly: true);
            _policyDrawer = false;
            _statusMessage = "发布策略已保存，已按新策略重新检查。";
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task PublishAsync()
    {
        var project = Project;
        if (project is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var snapshot = await Publisher.PublishProjectAsync(
                project.Code,
                new PublishRequest("管理后台发布", _forcePublish, _forceReason));
            _statusMessage = snapshot.Forced
                ? $"已强制发布「{project.Code}」，版本指纹 {snapshot.VersionHash}，原因已记录。"
                : $"已发布「{project.Code}」，版本指纹 {snapshot.VersionHash}。";
            _forcePublish = false;
            _forceReason = "";
            _historyPageIndex = 0; // 新记录在第 1 页首行，别让用户停留在旧页找它
            _previewLoaded = false;   // 线上产物变了，预览下次打开时重取
            await RefreshAllAsync();
            if (_tab == ReleaseTab.Distribution)
            {
                await EnsurePreviewAsync();
            }
        }
        catch (PublishBlockedException ex)
        {
            // 门禁拦下时把用户带到「发布」页签：强制发布的开关与原因输入都在那里，否则只看到一句报错无从操作
            _tab = ReleaseTab.Publish;
            ShowStatusError(ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task RollbackAsync(Guid snapshotId)
    {
        var project = Project;
        if (project is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var snapshot = await Publisher.RollbackProjectAsync(project.Code, snapshotId);
            _statusMessage = $"已回滚「{project.Code}」，线上版本恢复为 {snapshot.VersionHash}。草稿词条未改动。";
            _historyPageIndex = 0; // 回滚同样产生一条新快照
            _previewLoaded = false;
            await RefreshAllAsync();
            if (_tab == ReleaseTab.Distribution)
            {
                await EnsurePreviewAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            ShowStatusError(ex.Message);
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>预览截断阈值（字符）：整个已发布 bundle 塞进 SignalR 渲染批次会让大项目页面明显变重，完整内容走固定地址下载。</summary>
    private const int PreviewMaxChars = 20_000;

    private bool _previewTruncated;

    /// <summary>当前 <see cref="_previewCulture"/> 的内容是否已取过：切到「分发」页签时才真正加载。</summary>
    private bool _previewLoaded;

    /// <summary>预览加载结果：纯数据，先拿到手、确认没过期再赋给页面状态。</summary>
    private sealed record PreviewData(string Etag, string Content, bool Truncated);

    private static readonly PreviewData EmptyPreview = new("—", "", false);

    private void ApplyPreview(PreviewData preview)
    {
        _previewEtag = preview.Etag;
        _previewJsonContent = preview.Content;
        _previewTruncated = preview.Truncated;
    }

    private async Task<PreviewData> LoadPreviewAsync(string? projectCode, string culture)
    {
        if (projectCode is null)
        {
            return EmptyPreview;
        }

        var bundle = await Publisher.GetOrCompileBundleAsync(projectCode, culture);
        if (bundle is null)
        {
            return new PreviewData("—", "（该语种尚未发布。导入与编辑只写入草稿，在上方「发布」后才会对外生效。）", false);
        }

        // 只解码预览需要的前缀：大项目的 bundle 可能有数 MB，没必要整个转成字符串再截断。
        // UTF-8 每个 UTF-16 字符至多占 3 字节（4 字节的字符占 2 个 UTF-16 字符），所以前 3×上限 个字节至少解出「上限」个字符；
        // 前缀之外还有字节，就说明总字符数一定超限。
        var span = bundle.NestedJsonUtf8Bytes.Span;
        var window = Math.Min(span.Length, PreviewMaxChars * 3);
        var json = Encoding.UTF8.GetString(span[..window]);
        var truncated = span.Length > window || json.Length > PreviewMaxChars;
        if (json.Length > PreviewMaxChars)
        {
            var cut = PreviewMaxChars;
            if (char.IsHighSurrogate(json[cut - 1]))
            {
                cut--; // 别把代理对从中间切开
            }

            json = json[..cut];
        }

        return new PreviewData(bundle.ETag, json, truncated);
    }
}
