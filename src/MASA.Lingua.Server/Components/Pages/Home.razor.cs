using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Components.Shared;
using MASA.Lingua.Server.Services;
using Microsoft.Extensions.Logging;

namespace MASA.Lingua.Server.Components.Pages;

public partial class Home
{
    private enum MatrixView { Entries, Report }

    private sealed record LegendItem(string Css, string Label, int Count);

    /// <summary>
    /// 数据加载超过这个毫秒数按异常慢处理，升为 Warning（常规加载是 Debug，默认日志不刷屏）。
    /// 内网/VPN 链路冷加载几百毫秒属正常，不设太紧，只兜「数据库或链路真的出问题了」。
    /// </summary>
    private const int SlowLoadWarningMs = 2000;

    /// <summary>当前项目全量（词条树）：按需只加载顶栏选中的项目，不再整库 dump。</summary>
    private Project? _currentProject;

    /// <summary>全部项目的影响面投影（命名空间编码 + 词条 Key，不含文案与译文）：共享词条的继承/覆盖统计用。</summary>
    private List<ProjectImpact> _projectImpacts = [];
    private List<TranslationNamespace> _sharedNamespaces = [];
    private List<GlossaryTerm> _glossaryTerms = [];
    /// <summary>检查报告展示的问题：当前命名空间 / 搜索筛选范围内的词条的问题（由 <see cref="RefreshFilteredIssues"/> 派生）。</summary>
    private List<ValidationIssue> _validationIssues = [];

    /// <summary>当前范围（共享作用域 = 共享命名空间；项目作用域 = 当前项目）全部词条的问题，按词条顺序拼接。</summary>
    private List<ValidationIssue> _projectIssues = [];

    // ------------------------------------------------------------------
    // 校验状态：按词条 Id 存放，单格 / 单条保存后只重算那一条（见 CheckEntry），不再每次保存全量重跑。
    // 全量校验（加载 / 运行检查）才对全部词条算一遍，且在后台线程执行（RunValidationCheckAsync）。
    // ------------------------------------------------------------------

    /// <summary>词条 Id → 该词条全部语种的校验问题（含 EMPTY_TRANSLATION）。</summary>
    private Dictionary<Guid, IReadOnlyList<ValidationIssue>> _issuesByEntry = [];

    /// <summary>词条 Id → 源文案命中的术语：与语种无关，只在源文案 / 术语库变化时重新匹配；遵循率统计复用。</summary>
    private Dictionary<Guid, IReadOnlyList<GlossaryTerm>> _matchedTerms = [];

    /// <summary>
    /// 矩阵单元格的校验备注查表：(词条 Id, 语种) → 非空译文类问题（EMPTY_TRANSLATION 之外）。
    /// 校验时建好，渲染树里查表取用，不再逐格重跑校验器。
    /// </summary>
    private Dictionary<(Guid EntryId, string Culture), List<ValidationIssue>> _issueLookup = [];

    /// <summary>范围内数据（词条树 / 术语库）的修订号：每次就地改动或重载 +1；后台校验据此判断结果是否过期。</summary>
    private int _dataRevision;

    // 加载守卫（_loadVersion / _disposed / _isLoading / NextLoadVersion / IsCurrent / EndLoad）来自 LinguaPageBase。

    // ------------------------------------------------------------------
    // 派生缓存：词条树 / 筛选条件变化时重建一次，渲染树里直接读，不再每次访问都重算 + 排序。
    // ------------------------------------------------------------------
    private List<TranslationNamespace> _currentNamespaces = [];
    private HashSet<Guid> _sharedNamespaceIds = [];
    private IReadOnlyList<string> _targetCultures = [];

    /// <summary>当前范围内全部词条，按（命名空间, Key）有序（Ordinal，与发布产物的排序口径一致）。</summary>
    private List<TranslationEntry> _scopeEntries = [];

    /// <summary>叠加命名空间 / 搜索筛选后的词条（<see cref="_scopeEntries"/> 的有序子集）。</summary>
    private List<TranslationEntry> _filteredEntries = [];
    private string _lede = "";
    private List<CultureCoverage> _coverage = [];
    private List<GlossaryCompliance> _glossaryCompliance = [];
    private int _entryCount;
    private int _blockingCount;

    private MatrixView _view = MatrixView.Entries;
    private bool _showAdd;
    private string _selectedNamespaceCode = "";
    private string _searchKeyword = "";
    private bool _onlyNeedsReview;

    // ---- 检查报告分页：与矩阵共用每页条数，页序号独立 ----
    private int _reportPageIndex;

    // ---- 词条分页（只影响矩阵展示；词条总数、检查报告、AI 批量补全仍作用于整个筛选范围） ----
    private static readonly int[] PageSizeOptions = [20, 50, 100, 200];
    private int _pageIndex; // 0 起始；渲染时按 totalPages 截断，越界不会显示空页
    private int _pageSize = 20;

    private void ResetPage() => _pageIndex = 0;

    private void GoToPage(int page) => _pageIndex = Math.Max(0, page);
    // 状态提示（_statusText / _statusIsError / _statusMessage / ShowStatusError）来自 LinguaPageBase：
    // 给 _statusMessage 赋值 = 普通提示；失败信息用 ShowStatusError。

    private bool _isBusy;

    // _isLoading（加载骨架标记）来自 LinguaPageBase。

    /// <summary>共享词条影响面的记忆化缓存：(命名空间, Key) → 影响面；随数据重载清空。</summary>
    private readonly Dictionary<(string Namespace, string Key), SharedEntryImpact?> _impactCache = [];

    private string _newNs = "";
    private string _newKey = "";
    private string _newSourceText = "";
    private string? _newContext;
    private int? _newMaxLength;

    /// <summary>
    /// 有未保存编辑的行：矩阵单元格改成「输入只进草稿，Enter / 保存才落库」，页面据此显示总数与「放弃全部」。
    /// 只存 Id，具体文本留在各 <see cref="EntryRow"/> 自己那里（页面对每行的编辑内容不需要感知）。
    /// </summary>
    private readonly HashSet<Guid> _dirtyRowIds = [];

    /// <summary>「放弃全部」时递增：行组件看到它变了就清空本行草稿并复位输入框。</summary>
    private int _discardToken;

    private string SelectedProjectCode => Scope.ProjectCode;

    private bool IsSharedScope => Scope.IsShared;

    private Project? CurrentProject => _currentProject;

    private string ScopeName => IsSharedScope ? "全局共享命名空间" : SelectedProjectCode;

    /// <summary>
    /// 当前项目的语种列（矩阵列顺序、完成度条顺序、校验与 AI 目标语种都用它）：
    /// 默认语种（源文案语种）固定排第一列——它是其余译文的参照，放在中间会被横向滚动挤出去；
    /// 其余语种保持项目里的配置顺序。缓存值，随项目加载 / 语种改动由 <see cref="RebuildActiveCultures"/> 重建。
    /// </summary>
    private IReadOnlyList<string> ActiveCultures => _activeCultures;

    private IReadOnlyList<string> _activeCultures = ["zh-CN", "en-US", "ja-JP"];

    /// <summary>
    /// 「新增语种」下拉的 ExtraCodes：只补默认源语种（ICU 枚举不到 zh-CN，项目一旦移除就选不回来）。
    /// 缓存为字段、随 <see cref="RebuildActiveCultures"/> 重建：CultureSelect 靠「集合实例是否同一份」
    /// 判断要不要重建约 900 项候选，每次渲染现建新集合会让那份缓存永远失效。
    /// </summary>
    private IReadOnlyCollection<string> _addCultureExtraCodes = ["zh-CN"];

    private void RebuildActiveCultures()
    {
        var source = DefaultCultureOrFallback;
        _addCultureExtraCodes = [source];
        var configured = CurrentProject?.SupportedCultures;
        if (configured is not { Count: > 0 })
        {
            _activeCultures = ["zh-CN", "en-US", "ja-JP"];
            return;
        }

        var ordered = new List<string>(configured.Count);
        var match = configured.FirstOrDefault(c => string.Equals(c, source, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            ordered.Add(match);
        }

        foreach (var culture in configured)
        {
            if (!ordered.Contains(culture, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(culture);
            }
        }

        _activeCultures = ordered;
    }

    /// <summary>当前范围的命名空间（共享作用域 = 全部共享命名空间；项目作用域 = 继承的共享命名空间 + 项目自己的）。缓存值，见 <see cref="RebuildScope"/>。</summary>
    private IReadOnlyList<TranslationNamespace> CurrentNamespaces => _currentNamespaces;

    /// <summary>叠加筛选后的有序词条。缓存值：词条树 / 筛选变化时由 <see cref="RebuildFilter"/> 重建，渲染树每次访问是 O(1)。</summary>
    private IReadOnlyList<TranslationEntry> FilteredEntries => _filteredEntries;

    /// <summary>需要翻译的目标语种：项目支持的语种里除源语种之外的全部（缓存值，随项目加载更新）。</summary>
    private IReadOnlyList<string> TargetCultures => _targetCultures;

    /// <summary>
    /// 标题下的一句话：这个范围内现在还差什么（全部由词条与校验结果算出；缓存值，校验结果变化时重建）。
    /// 加载中不显示：切换项目时状态已被清空，此时 <see cref="_lede"/> 只可能是「这个项目还没有词条」，
    /// 与下面的加载骨架互相矛盾（数据还在路上，不是没有）。
    /// </summary>
    private string Lede => _isLoading ? "" : _lede;

    private string BuildLede()
    {
        if (_entryCount == 0)
        {
            return IsSharedScope
                ? "共享命名空间下还没有词条。"
                : "这个项目还没有词条。点击「新增词条」添加第一条，或到「导入与发布」导入现有 JSON。";
        }

        if (_projectIssues.Count == 0)
        {
            return $"{_entryCount} 条词条在 {ActiveCultures.Count} 个语种下都已就绪，没有待处理的问题。";
        }

        var parts = _projectIssues
            .GroupBy(i => i.RuleCode)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} 处{UiText.Rule(g.Key)}");
        return $"{_projectIssues.Count} 项待处理：{string.Join("、", parts)}。";
    }

    /// <summary>
    /// 词条树变化（加载 / 新增 / 删除命名空间 / 语种变更）后重建全部派生缓存：命名空间、语种、有序词条、筛选结果。
    /// 词条文本的就地替换不走这里（顺序与归属都没变，见 <see cref="ReplaceEntriesInPlace"/>）。
    /// </summary>
    private void RebuildScope()
    {
        RebuildActiveCultures();
        RebuildNamespaces();
        _targetCultures = ActiveCultures.Where(c => !IsSourceCulture(c)).ToList();
        RebuildScopeEntries();
    }

    private void RebuildNamespaces()
    {
        _sharedNamespaceIds = _sharedNamespaces.Select(n => n.Id).ToHashSet();

        if (IsSharedScope)
        {
            _currentNamespaces = [.. _sharedNamespaces];
            return;
        }

        var list = new List<TranslationNamespace>();
        if (CurrentProject is not null)
        {
            list.AddRange(_sharedNamespaces.Where(s => CurrentProject.SharedNamespaceCodes.Contains(s.Code)));
            list.AddRange(CurrentProject.Namespaces);
        }

        _currentNamespaces = list;
    }

    private void RebuildScopeEntries()
    {
        // Ordinal：与发布产物（BundleCompiler）的排序口径一致，且不走区域性比较，排序明显更快
        _scopeEntries = _currentNamespaces
            .SelectMany(n => n.Entries)
            .OrderBy(e => e.NamespaceCode, StringComparer.Ordinal)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
        RebuildFilter();
    }

    /// <summary>
    /// 由 <see cref="_scopeEntries"/> + 命名空间 / 搜索条件重建筛选结果，并让检查报告跟着同一个筛选范围走
    /// （矩阵与报告共用这一个出口，不再各自在不同时机刷新而互相不一致）。
    /// </summary>
    private void RebuildFilter()
    {
        IEnumerable<TranslationEntry> query = _scopeEntries;
        if (_selectedNamespaceCode == "__root__")
        {
            query = query.Where(e => string.IsNullOrEmpty(e.NamespaceCode));
        }
        else if (!string.IsNullOrWhiteSpace(_selectedNamespaceCode))
        {
            query = query.Where(e => e.NamespaceCode == _selectedNamespaceCode);
        }

        if (!string.IsNullOrWhiteSpace(_searchKeyword))
        {
            query = query.Where(e =>
                e.Key.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase) ||
                e.SourceText.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase) ||
                (e.Context?.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (_onlyNeedsReview)
        {
            query = query.Where(EntryMatrix.HasNeedsReview);
        }

        _filteredEntries = ReferenceEquals(query, _scopeEntries) ? _scopeEntries : query.ToList();
        RefreshFilteredIssues();
    }

    /// <summary>命名空间下拉变化：回到第一页并重建筛选（报告随之收窄）。</summary>
    private void OnNamespaceChanged()
    {
        ResetPage();
        RebuildFilter();
    }

    /// <summary>搜索框（已防抖）确认了新的关键词。</summary>
    private void OnSearchChanged(string keyword)
    {
        if (string.Equals(keyword, _searchKeyword, StringComparison.Ordinal))
        {
            return;
        }

        _searchKeyword = keyword;
        ResetPage();
        RebuildFilter();
    }

    /// <summary>状态筛选切换（全部 / 仅待复核）：回到第一页并重建筛选；报告随之收窄到待复核词条。</summary>
    private void SetNeedsReviewFilter(bool onlyReview)
    {
        if (_onlyNeedsReview == onlyReview)
        {
            return;
        }

        _onlyNeedsReview = onlyReview;
        ResetPage();
        RebuildFilter();
    }

    private bool IsSharedEntry(TranslationEntry entry) => _sharedNamespaceIds.Contains(entry.NamespaceId);

    /// <summary>词条所属的仓储作用域：共享词条走 <c>__shared__</c>，其余走当前项目。</summary>
    private string ScopeOf(TranslationEntry entry) => IsSharedEntry(entry) ? WorkspaceScope.Shared : SelectedProjectCode;

    private IEnumerable<LegendItem> LegendItems
    {
        get
        {
            var all = new[]
            {
                new LegendItem("approved", "已审核", _coverage.Sum(c => c.Approved)),
                new LegendItem("ai", "AI 已译", _coverage.Sum(c => c.Ai)),
                new LegendItem("review", "待复核", _coverage.Sum(c => c.Review)),
                new LegendItem("missing", "缺失", _coverage.Sum(c => c.Missing)),
            };
            return all.Where(i => i.Count > 0);
        }
    }

    protected override async Task OnInitializedAsync()
    {
        Scope.Changed += OnScopeChanged;
        try
        {
            await ReloadAndCheckAsync();
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载词条数据失败：{ex.Message}");
            Logger.LogError(ex, "Home 初次加载失败");
        }
    }

    protected override void OnDisposing() => Scope.Changed -= OnScopeChanged;

    /// <summary>
    /// 顶栏切换项目：先清掉上一个项目的词条与校验状态、立即显示加载骨架，
    /// 数据到位后再整体渲染——避免切换期间停留在旧项目数据上。
    /// 这是事件回调里的 fire-and-forget：异常必须在这里接住（落到状态栏 + 日志），否则会变成未观察异常静默丢失，页面永远停在加载态。
    /// </summary>
    private void OnScopeChanged() => _ = InvokeAsync(async () =>
    {
        if (_disposed)
        {
            return;
        }

        // 先推进加载代数：本次加载立刻成为「最新一次」，此前还在跑的加载不会再把旧项目数据写回来。
        // 必须用 NextLoadVersion 真正把 _loadVersion +1——只是「_loadVersion + 1」取值而不推进的话，
        // ReloadDataAsync 里的 IsCurrent 永远为 false（结果被当成过期丢弃），EndLoad 也不会结束加载态，
        // 页面就会一直停在「正在加载当前项目的词条数据…」。
        var version = NextLoadVersion();
        try
        {
            _selectedNamespaceCode = "";
            _searchKeyword = "";
            _onlyNeedsReview = false;
            _reportPageIndex = 0;
            _pageIndex = 0;
            _statusMessage = null;
            _pendingOverwrite = null;
            _pendingDelete = null;
            _aiBusy = null;
            _view = MatrixView.Entries;
            ResetLoadedState();
            _isLoading = true;
            StateHasChanged();

            await ReloadAndCheckAsync(version);
        }
        catch (Exception ex)
        {
            // 已被更新的加载取代：不弹旧范围的错误（新加载会自己报）
            if (IsCurrent(version))
            {
                ShowStatusError($"加载 {ScopeName} 的词条数据失败：{ex.Message}");
            }

            Logger.LogError(ex, "Home 切换项目加载失败：{Scope}", Scope.ProjectCode);
        }
        finally
        {
            // 兜底结算：清状态阶段就抛异常时还没走到 ReloadAndCheckAsync 的 EndLoad，页面会永远停在骨架屏。
            // EndLoad 幂等（只有最新一次加载能结束加载态），重复调用无副作用。
            EndLoad(version);
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    });

    /// <summary>清掉上一个范围的全部词条 / 校验 / 派生状态。</summary>
    private void ResetLoadedState()
    {
        _dataRevision++;
        _currentProject = null;
        _sharedNamespaces = [];
        _projectImpacts = [];
        _glossaryTerms = [];
        _validationIssues = [];
        _projectIssues = [];
        _issuesByEntry = [];
        _matchedTerms = [];
        _issueLookup = [];
        _currentNamespaces = [];
        _sharedNamespaceIds = [];
        _targetCultures = [];
        _scopeEntries = [];
        _filteredEntries = [];
        _coverage = [];
        _glossaryCompliance = [];
        _entryCount = 0;
        _blockingCount = 0;
        _lede = BuildLede();
        _impactCache.Clear();
        _dirtyRowIds.Clear();
    }

    /// <summary>
    /// 加载 + 全量校验；加载结果被更新的加载取代时什么都不做（新的加载会负责校验与收尾）。
    /// 加载态由「最新一次加载」负责结束：被取代的旧加载不能提前亮出半成品，最新的加载无论成败都要收尾，页面不会卡在骨架上。
    /// </summary>
    /// <param name="version">调用方已通过 <see cref="NextLoadVersion"/> 取的代数（切换项目时要先拿到它才能判断自己的异常要不要提示）；null 表示本次自己取。</param>
    private async Task ReloadAndCheckAsync(int? version = null)
    {
        var token = version ?? NextLoadVersion();
        try
        {
            if (await ReloadDataAsync(token))
            {
                await RunValidationCheckAsync();
            }
        }
        finally
        {
            EndLoad(token);
        }
    }

    /// <summary>
    /// 并行加载：当前项目全量（词条树）、共享命名空间与影响面投影同时发出；
    /// 术语依赖当前项目的 Id（项目作用域 = 全局 + 项目术语），在项目到位后发起。
    /// 各阶段耗时以 [perf] 前缀记入日志（Debug 级，超过 <see cref="SlowLoadWarningMs"/> 升为 Warning），
    /// 用于定位切换链路慢在哪一段。
    /// <para>
    /// 加载代数守卫：全部数据先装进局部变量，等所有查询都返回、确认自己仍是最新一次加载后才一次性赋给页面状态，
    /// 所以较早发起的加载（例如 PM 清单刚返回就纠正了项目、或用户连续切换项目）不会用旧项目的数据覆盖新项目。
    /// 返回 false 表示本次结果已被丢弃。
    /// </para>
    /// </summary>
    private async Task<bool> ReloadDataAsync(int version)
    {
        var sw = Stopwatch.StartNew();
        var projectCode = Scope.ProjectCode;
        var currentTask = IsSharedScope || string.IsNullOrEmpty(projectCode)
            ? Task.FromResult<Project?>(null)
            : Repository.GetProjectByCodeAsync(projectCode);
        var sharedTask = Repository.GetSharedNamespacesAsync();
        var impactsTask = Repository.GetProjectImpactSummariesAsync();

        var project = await currentTask;
        var projectMs = sw.ElapsedMilliseconds;
        var termsTask = Repository.GetGlossaryTermsAsync(project?.Id);

        var shared = (await sharedTask).ToList();
        var sharedMs = sw.ElapsedMilliseconds;
        var impacts = (await impactsTask).ToList();
        var impactsMs = sw.ElapsedMilliseconds;
        var terms = (await termsTask).ToList();
        var totalMs = sw.ElapsedMilliseconds;

        if (!IsCurrent(version))
        {
            return false;
        }

        _currentProject = project;
        _sharedNamespaces = shared;
        _projectImpacts = impacts;
        _glossaryTerms = terms;
        _impactCache.Clear();
        _dataRevision++;
        RebuildScope();

        // 阶段耗时：项目/共享/影响面并行发出，「各自就绪时刻」即各自耗时；术语在项目就绪后发起、与剩余任务并行，
        // 因此它的值是「项目就绪到全部就绪」的墙钟跨度。常规加载记 Debug（默认日志不刷屏），超过阈值升 Warning。
        var termsMs = totalMs - projectMs;
        Logger.Log(
            totalMs > SlowLoadWarningMs ? LogLevel.Warning : LogLevel.Debug,
            "[perf] Home 数据加载: 项目 {ProjectMs}ms / 共享 {SharedMs}ms / 影响面 {ImpactsMs}ms / 术语 {TermsMs}ms / 总计 {TotalMs}ms（词条 {Entries} 条）",
            projectMs, sharedMs, impactsMs, termsMs, totalMs, project?.Namespaces.Sum(n => n.Entries.Count) ?? 0);
        return true;
    }

    // ------------------------------------------------------------------
    // 项目写密钥（缺词上报鉴权）
    // ------------------------------------------------------------------

    private bool _revealWriteKey;

    private void RevealWriteKey() => _revealWriteKey = !_revealWriteKey;

    private async Task CopyWriteKey()
    {
        if (CurrentProject?.WriteKey is not { } key)
        {
            ShowStatusError("项目还没有写密钥。");
            return;
        }

        // 与 CopyVersionHashAsync 同理：剪贴板异常不能炸电路
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", key);
            _statusMessage = "密钥已复制到剪贴板。";
            _statusIsError = false;
        }
        catch (JSException)
        {
            _statusMessage = "复制失败：浏览器未授权剪贴板或页面失去焦点。密钥已明文显示，请手动选中复制。";
            _statusIsError = true;
        }
    }

    private async Task RegenerateWriteKeyAsync()
    {
        var project = CurrentProject;
        if (project is null)
        {
            return;
        }

        try
        {
            var newKey = await Repository.RegenerateWriteKeyAsync(project.Id);
            if (newKey is null)
            {
                ShowStatusError("项目不存在，无法重置密钥。");
                return;
            }

            // 就地更新内存里的项目实体，避免整表重拉；面板切到明文展示，方便立即复制
            project.WriteKey = newKey;
            _currentProject = project;
            _revealWriteKey = true;
            _statusMessage = "密钥已重置，旧密钥立即失效。请同步更新业务项目配置里的 Lingua:WriteKey。";
        }
        catch (Exception ex)
        {
            ShowStatusError($"重置密钥失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 项目语种设置：下拉选中 / 点 × 移除 / 切默认语种都即时落库，不再有「添加」与「保存语种」两步。
    // 为什么不再暂存：暂存版本要用户先理解「添加进了暂存清单但还没保存」这个中间态，
    // 而且同一个面板上「添加」与「保存语种」看起来像同一件事，点哪个都可能不是用户的本意。
    // ------------------------------------------------------------------

    private bool _cultureDrawer;
    private bool _writeKeyDrawer;

    /// <summary>语种写入进行中：抽屉里的控件全部禁用，避免连点造成并发写（每次写入都会整项目重载）。</summary>
    private bool _cultureBusy;

    /// <summary>等待确认移除的语种。</summary>
    private string? _pendingCultureRemove;

    /// <summary>等待确认切换的默认语种（改的是「源语言」语义，值得一次确认）。</summary>
    private string? _pendingDefaultCulture;

    private string CultureDrawerIntro =>
        "改完立即保存。默认语种是源文案语种、也是译文缺失时的回退语种，固定显示在词条矩阵第一列。";

    private string CultureRemoveMessage => _pendingCultureRemove is { } culture
        ? $"将从项目语种中移除 {culture}（{UiText.Locale(culture)}）。该语种已有译文保留在库中，重新添加即可恢复；" +
          "已发布的 " + culture + ".json 会被清理，需要重新发布才能让各端 manifest 生效。"
        : string.Empty;

    private string DefaultCultureConfirmMessage => _pendingDefaultCulture switch
    {
        null => string.Empty,
        var culture when CurrentProject is { } project
            => $"将默认语种从 {project.DefaultCulture} 改为 {culture}。词条的「源语言」列、译文缺失时的回退语种、" +
               "以及新增/导入词条时写入的源文案语种都会随之改变；词条矩阵第一列将改为 " + culture + "。",
        var culture => $"将默认语种改为 {culture}。"
    };

    private void OpenCultureDrawer()
    {
        _cultureDrawer = true;
        _writeKeyDrawer = false;
        _statusMessage = null;
    }

    private void CloseCultureDrawer() => _cultureDrawer = false;

    private void OpenWriteKeyDrawer()
    {
        _writeKeyDrawer = true;
        _cultureDrawer = false;
        _statusMessage = null;
    }

    private void CloseWriteKeyDrawer() => _writeKeyDrawer = false;

    /// <summary>下拉框选中即添加：不做暂存，直接按「现有语种 + 新语种」落库。</summary>
    private async Task AddCultureAsync(string culture)
    {
        // 选回占位项（空值）不是错误，什么都不做
        if (CurrentProject is not { } project || _cultureBusy || string.IsNullOrWhiteSpace(culture))
        {
            return;
        }

        var normalized = LinguaCultures.Normalize(culture);
        if (normalized is null)
        {
            ShowStatusError($"语种代码 \"{culture}\" 格式不合法。");
            return;
        }

        var current = project.SupportedCultures ?? [];
        if (current.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await PersistCulturesAsync(project.DefaultCulture, [.. current, normalized], removed: []);
    }

    private void RequestRemoveCulture(string culture)
    {
        if (CurrentProject is not { } project)
        {
            return;
        }

        if (string.Equals(culture, project.DefaultCulture, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusError("默认语种不可直接移除：请先把默认语种改为其他语种。");
            return;
        }

        if ((project.SupportedCultures?.Count ?? 0) <= 1)
        {
            ShowStatusError("项目至少要保留一个语种。");
            return;
        }

        _pendingCultureRemove = culture;
    }

    private async Task ConfirmRemoveCultureAsync()
    {
        if (CurrentProject is not { } project || _pendingCultureRemove is not { } culture)
        {
            _pendingCultureRemove = null;
            return;
        }

        _pendingCultureRemove = null;
        var remaining = (project.SupportedCultures ?? [])
            .Where(c => !string.Equals(c, culture, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 移除要连同静态产物一起清理：读取端虽按 SupportedCultures 拦截，但磁盘上留着旧文件会让排查者误判
        await PersistCulturesAsync(project.DefaultCulture, remaining, removed: [culture]);
    }

    private void OnDefaultCultureChanged(ChangeEventArgs e)
    {
        var next = e.Value?.ToString();
        if (string.IsNullOrWhiteSpace(next) || CurrentProject is not { } project)
        {
            return;
        }

        if (string.Equals(next, project.DefaultCulture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingDefaultCulture = next;
    }

    private async Task ConfirmChangeDefaultCultureAsync()
    {
        if (CurrentProject is not { } project || _pendingDefaultCulture is not { } culture)
        {
            _pendingDefaultCulture = null;
            return;
        }

        _pendingDefaultCulture = null;
        await PersistCulturesAsync(culture, [.. (project.SupportedCultures ?? [])], removed: []);
    }

    /// <summary>
    /// 语种配置的唯一写入口：调仓储 → 清理被移除语种的静态产物 → 整项目重载（列顺序、完成度、校验口径全都跟着变）。
    /// 失败时不动内存状态（仓储是事务写，页面这一侧本来就没改），只把原因显示在状态栏。
    /// </summary>
    private async Task PersistCulturesAsync(string defaultCulture, IReadOnlyList<string> cultures, IReadOnlyList<string> removed)
    {
        if (_cultureBusy)
        {
            return;
        }

        _cultureBusy = true;
        try
        {
            await Repository.UpdateProjectCulturesAsync(SelectedProjectCode, defaultCulture, cultures);

            if (removed.Count > 0)
            {
                Publisher.RemovePublishedCultures(SelectedProjectCode, removed);
            }

            await ReloadAndCheckAsync();
            _statusMessage = removed.Count > 0
                ? $"语种已更新（移除了 {string.Join("、", removed)}，其静态产物已清理）。该语种已有译文保留在库中，重新添加即可恢复；请重新发布以刷新各端 manifest。"
                : $"语种已更新为 {string.Join("、", cultures)}，默认语种 {defaultCulture}。新语种词条初始为「缺失」；请重新发布让语种进入各端 manifest。";
        }
        catch (LinguaValidationException ex)
        {
            ShowStatusError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            ShowStatusError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowStatusError($"保存语种失败：{ex.Message}");
            Logger.LogError(ex, "保存项目语种失败：{Project}", SelectedProjectCode);
        }
        finally
        {
            _cultureBusy = false;
        }
    }

    /// <summary>复制完整版本指纹：核对线上产物与草稿是否一致时要用整串逐字符比对。</summary>
    private async Task CopyVersionHashAsync()
    {
        if (CurrentProject?.LastPublishedVersionHash is not { Length: > 0 } hash)
        {
            return;
        }

        // 剪贴板 API 在文档无焦点 / 浏览器拒绝授权时会抛 JS 异常（NotAllowedError）：
        // 不接住会顺着事件回调把整个 Blazor 电路炸掉（页面白屏、必须手动刷新）。
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", hash);
            _statusMessage = "版本指纹已复制到剪贴板。";
            _statusIsError = false;
        }
        catch (JSException)
        {
            _statusMessage = "复制失败：浏览器未授权剪贴板或页面失去焦点，请手动选中版本指纹复制。";
            _statusIsError = true;
        }
    }

    /// <summary>源语种（项目默认语种）；还没加载到项目时用 zh-CN 兜底。</summary>
    private string DefaultCultureOrFallback => CurrentProject?.DefaultCulture ?? "zh-CN";

    private bool IsSourceCulture(string culture) => EntryTranslations.IsSource(culture, DefaultCultureOrFallback);

    private static string CoverageSummary(CultureCoverage c) =>
        $"{c.Culture}：已审核 {c.Approved}，AI 已译 {c.Ai}，待复核 {c.Review}，缺失 {c.Missing}，共 {c.Total} 条";

    private static string SeverityCss(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "neg",
        IssueSeverity.Warning => "warn",
        _ => ""
    };

    private static string SeverityLabel(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Error => "错误",
        IssueSeverity.Warning => "警告",
        _ => "提示"
    };

    private GlossaryCompliance? GlossaryOf(string culture) =>
        _glossaryCompliance.FirstOrDefault(c => string.Equals(c.Culture, culture, StringComparison.OrdinalIgnoreCase));

    /// <summary>共享词条的影响面：被几个项目继承、几个已覆盖。结果按 (命名空间, Key) 记忆化，避免每次渲染全库扫描。</summary>
    private SharedEntryImpact? SharedImpactOf(TranslationEntry entry)
    {
        if (!IsSharedEntry(entry))
        {
            return null;
        }

        var cacheKey = (entry.NamespaceCode, entry.Key);
        if (_impactCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var impact = SharedImpact.Compute(_projectImpacts, entry.NamespaceCode, entry.Key);
        _impactCache[cacheKey] = impact;
        return impact;
    }

    // ------------------------------------------------------------------
    // 就地更新：仓储写入返回已保存的词条，直接替换内存数据，
    // 单格编辑 / 删除 / 新增 / AI 写回不再整库重拉。
    // ------------------------------------------------------------------

    private IEnumerable<TranslationNamespace> EnumerateNamespaces() =>
        _sharedNamespaces.Concat(_currentProject?.Namespaces ?? []);

    /// <summary>
    /// 按 Id 把已保存词条替换进内存词条树（命名空间的词条列表 + 有序词条缓存），一批只扫一遍。
    /// 只替换实例、不改旧实例：仓储缓存里共享的词条对象图归仓储所有，这里绝不就地修改它们。
    /// 返回找不到的词条（正常不应发生），调用方据此回库重载兜底。
    /// </summary>
    private List<TranslationEntry> ReplaceEntriesInPlace(IReadOnlyCollection<TranslationEntry> saved)
    {
        var byId = new Dictionary<Guid, TranslationEntry>(saved.Count);
        foreach (var entry in saved)
        {
            byId[entry.Id] = entry; // 同一词条多次保存时后者为准
        }

        var found = new HashSet<Guid>();
        foreach (var ns in EnumerateNamespaces())
        {
            for (var i = 0; i < ns.Entries.Count; i++)
            {
                if (byId.TryGetValue(ns.Entries[i].Id, out var replacement))
                {
                    replacement.Namespace = ns;
                    ns.Entries[i] = replacement;
                    found.Add(replacement.Id);
                }
            }
        }

        // 有序词条缓存里的实例也要换（顺序与归属没变，原位替换即可，不用重排）
        for (var i = 0; i < _scopeEntries.Count; i++)
        {
            if (byId.TryGetValue(_scopeEntries[i].Id, out var replacement))
            {
                _scopeEntries[i] = replacement;
            }
        }

        _dataRevision++;
        return byId.Values.Where(e => !found.Contains(e.Id)).ToList();
    }

    /// <summary>按 Id 从内存词条树移除词条；命名空间因此变空时一并移除（与服务端删除行为一致，避免下拉框残留空项）。</summary>
    private void RemoveEntryInPlace(TranslationEntry entry)
    {
        foreach (var ns in EnumerateNamespaces().ToList())
        {
            var idx = ns.Entries.FindIndex(e => e.Id == entry.Id);
            if (idx < 0)
            {
                continue;
            }

            ns.Entries.RemoveAt(idx);
            if (ns.Entries.Count == 0)
            {
                _sharedNamespaces.Remove(ns);
                _currentProject?.Namespaces.Remove(ns);
                RebuildNamespaces();
            }

            var scopeIdx = _scopeEntries.FindIndex(e => e.Id == entry.Id);
            if (scopeIdx >= 0)
            {
                _scopeEntries.RemoveAt(scopeIdx);
            }

            DropEntryChecks(entry);
            _dataRevision++;
            return;
        }
    }

    /// <summary>把新保存的词条挂进内存词条树：归属命名空间已加载就追加进去，否则把新命名空间一并挂上。</summary>
    private void AddEntryInPlace(TranslationEntry saved)
    {
        var savedNs = saved.Namespace;
        if (savedNs is null)
        {
            return;
        }

        TranslationNamespace target;
        if (savedNs.IsShared)
        {
            target = _sharedNamespaces.FirstOrDefault(s => s.Id == savedNs.Id) ?? AppendNamespaceInPlace(savedNs, shared: true);
        }
        else
        {
            target = _currentProject?.Namespaces.FirstOrDefault(n => n.Id == savedNs.Id) ?? AppendNamespaceInPlace(savedNs, shared: false);
        }

        saved.Namespace = target;

        // 「新增或更新」表单：Key 已存在时保存的是更新后的同一词条，要换掉内存里的旧实例而不是留着旧的
        var existingIdx = target.Entries.FindIndex(e => e.Id == saved.Id);
        if (existingIdx >= 0)
        {
            target.Entries[existingIdx] = saved;
        }
        else
        {
            target.Entries.Add(saved);
        }

        _dataRevision++;
        RebuildNamespaces();
        RebuildScopeEntries(); // 新增是低频操作：重排一次有序词条缓存，换来后续所有读取免排序
    }

    private TranslationNamespace AppendNamespaceInPlace(TranslationNamespace ns, bool shared)
    {
        // 仓储返回的命名空间壳可能带着整个命名空间的词条：只保留壳，词条由调用方单独挂上
        var shell = new TranslationNamespace { Id = ns.Id, ProjectId = ns.ProjectId, Code = ns.Code, IsShared = ns.IsShared };
        if (shared)
        {
            _sharedNamespaces.Add(shell);
        }
        else
        {
            _currentProject?.Namespaces.Add(shell);
        }

        return shell;
    }

    private async Task OnTranslationEditedAsync(TranslationEntry entry, string culture, string newValue)
    {
        var targetProj = ScopeOf(entry);
        // 手工编辑即定稿（单人模式，发布即审核）：规则/术语问题不再降级待复核，由单元格校验提示与发布门禁把关。
        // acknowledge 同时覆盖「确认」动作：内容没变而再次保存（或点单元格里的确认按钮）＝人工已确认，
        // 恢复已审核并清掉 AI 违规备注与「源文案已变更」标记。
        var unchanged = string.Equals(EntryTranslations.Text(entry, culture, DefaultCultureOrFallback), newValue, StringComparison.Ordinal);
        TranslationEntry saved;
        try
        {
            saved = await Repository.UpsertEntryAsync(
                targetProj,
                entry.NamespaceCode,
                entry.Key,
                IsSourceCulture(culture) ? newValue : entry.SourceText,
                entry.Context,
                entry.MaxLength,
                new Dictionary<string, string> { [culture] = newValue },
                status: TranslationStatus.Approved,
                acknowledgeIssues: true);
        }
        catch (Exception ex)
        {
            // 单元格的「保存」是显式动作，失败必须说清楚原因；抛出去会让整个电路断掉，用户看不到任何提示
            ShowStatusError($"保存 [{EntryDisplayKey(entry)}] ({culture}) 失败：{ex.Message}");
            Logger.LogError(ex, "保存译文失败：{Entry} / {Culture}", EntryDisplayKey(entry), culture);
            return;
        }

        // 就地替换内存词条（含校验降级 / stale 标记后的完整状态），只重校验这一条词条，不整库重拉、不全量重跑校验
        if (ReplaceEntriesInPlace([saved]).Count > 0)
        {
            await ReloadAndCheckAsync();
        }
        else
        {
            RecheckEntries([saved]);
        }

        _statusMessage = unchanged
            ? $"已确认 [{EntryDisplayKey(entry)}] ({culture})，保留现译文，恢复为已审核，发布后将更新至静态 {culture}.json 文件。"
            : $"已保存 [{EntryDisplayKey(entry)}] ({culture})，发布后将更新至静态 {culture}.json 文件。";
    }

    /// <summary>词条展示名：项目根（空命名空间）的词条不显示前缀，其余为「命名空间.Key」。</summary>
    private static string EntryDisplayKey(TranslationEntry entry) => EntryMatrix.DisplayKey(entry);

    /// <summary>
    /// 行组件报告「这一行有没有未保存改动」：只在翻转时被调用（键击阶段不回调），
    /// 页面据此维护未保存行数与「放弃全部」按钮的可用性。
    /// <para>
    /// 行被销毁时（翻页 / 换筛选 / 换项目）也会带着 false 回调一次，避免计数挂着一个已经不在屏幕上的行。
    /// 那种回调可能发生在页面自己正在销毁的路上，所以先判 <c>_disposed</c> 再刷新界面。
    /// </para>
    /// </summary>
    private void OnRowPendingChanged(TranslationEntry entry, bool hasPending)
    {
        var changed = hasPending ? _dirtyRowIds.Add(entry.Id) : _dirtyRowIds.Remove(entry.Id);
        if (changed && !_disposed)
        {
            StateHasChanged();
        }
    }

    /// <summary>放弃所有未保存编辑：递增标记让行组件清空草稿并复位输入框，随后清掉计数。</summary>
    private void DiscardAllEdits()
    {
        _discardToken++;
        _dirtyRowIds.Clear();
        _statusMessage = "已放弃未保存的修改。";
    }

    /// <summary>
    /// 矩阵里直接编辑「上下文与约束」（词条下方输入框）：内容没变不落库；
    /// UpsertEntryAsync 以 null 表示「未提供、保持原值」，所以清空用空串而不是 null。
    /// </summary>
    private async Task OnContextEditedAsync(TranslationEntry entry, string? newContext)
    {
        var context = newContext ?? string.Empty;
        if (string.Equals(entry.Context ?? string.Empty, context, StringComparison.Ordinal))
        {
            return;
        }

        var targetProj = ScopeOf(entry);
        var saved = await Repository.UpsertEntryAsync(
            targetProj,
            entry.NamespaceCode,
            entry.Key,
            entry.SourceText,
            context,
            entry.MaxLength);

        if (ReplaceEntriesInPlace([saved]).Count > 0)
        {
            await ReloadAndCheckAsync();
        }
        else
        {
            // 上下文只影响搜索命中与 AI 提示，不影响校验结果：不用重校验，刷新一下筛选即可
            RebuildFilter();
        }
        _statusMessage = $"已保存 [{EntryDisplayKey(entry)}] 的上下文与约束，将随下次翻译与检查生效。";
    }

    // ---- 删除词条：先弹站内确认；共享词条点名继承项目，让影响面在确认前可见 ----
    private TranslationEntry? _pendingDelete;

    private void RequestDeleteEntry(TranslationEntry entry) => _pendingDelete = entry;

    /// <summary>删除确认文案：说清会连带删除全部语种译文、需重新发布才从分发的 JSON 里消失；共享词条点名继承项目。</summary>
    private string DeleteConfirmMessage
    {
        get
        {
            if (_pendingDelete is not { } entry)
            {
                return string.Empty;
            }

            var baseMessage = $"将删除词条 [{EntryDisplayKey(entry)}] 及其全部语种的译文，删除后不可恢复。";
            var publishNote = "该词条会继续留在已发布的 JSON 里，直到下次发布才会移除。";
            return SharedImpactOf(entry) is { InheritingProjects: > 0 } impact
                ? $"{baseMessage}它属于共享命名空间，{impact.InheritingProjects} 个项目正在继承它，删除后这些项目同样失去这条词条。{publishNote}"
                : $"{baseMessage}{publishNote}";
        }
    }

    private async Task ConfirmDeleteEntryAsync()
    {
        if (_pendingDelete is not { } entry)
        {
            return;
        }

        var targetProj = ScopeOf(entry);
        _pendingDelete = null;
        _isBusy = true;
        try
        {
            var deleted = await Repository.DeleteEntryAsync(targetProj, entry.NamespaceCode, entry.Key);
            if (deleted)
            {
                // 就地移除内存词条（含删空的命名空间），聚合结果（覆盖率 / 问题数）廉价重算，不整库重拉、不重跑校验
                RemoveEntryInPlace(entry);
                RebuildAfterCheck();
                _statusMessage = $"已删除词条 [{EntryDisplayKey(entry)}]；重新发布后将从各语种的 JSON 文件中移除。";
            }
            else
            {
                await ReloadAndCheckAsync();
                ShowStatusError($"词条 [{EntryDisplayKey(entry)}] 不存在或已被删除，请刷新后重试。");
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task AddNewEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(_newKey) || string.IsNullOrWhiteSpace(_newSourceText))
        {
            _statusMessage = "请完整填写词条 Key 与源语言文案。";
            return;
        }

        var savedNamespace = _newNs?.Trim() ?? "";
        var savedKey = _newKey.Trim();

        var saved = await Repository.UpsertEntryAsync(
            SelectedProjectCode,
            savedNamespace, // 留空 = 项目根
            savedKey,
            _newSourceText.Trim(),
            _newContext,
            _newMaxLength,
            new Dictionary<string, string> { [CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture] = _newSourceText.Trim() });

        _newKey = "";
        _newSourceText = "";
        _newContext = null;
        _newMaxLength = null;

        // 就地挂进内存词条树（词条可能是新建的，所属命名空间也可能刚创建），只校验这一条
        AddEntryInPlace(saved);
        RecheckEntries([saved]);

        // 跳到新词条所在页；词条按「命名空间 + Key」排序，按索引换算页码
        var index = _filteredEntries.FindIndex(e =>
            (string.IsNullOrEmpty(savedNamespace) ? string.IsNullOrEmpty(e.NamespaceCode) : e.NamespaceCode == savedNamespace) &&
            string.Equals(e.Key, savedKey, StringComparison.Ordinal));
        _pageIndex = index >= 0 ? index / _pageSize : 0;
        _statusMessage = "词条已保存，可点击「AI 批量补全」自动生成其他语种翻译。";
    }

    private async Task OnRunCheckClickedAsync()
    {
        await RunValidationCheckAsync();
        _view = _validationIssues.Count > 0 ? MatrixView.Report : MatrixView.Entries;
    }

    private void GoToReportPage(int page) => _reportPageIndex = Math.Max(0, page);

    /// <summary>从检查报告跳回矩阵，并把命名空间与搜索词定位到该条词条。</summary>
    private void LocateIssue(ValidationIssue issue)
    {
        var dot = issue.EntryKey.IndexOf('.');
        _selectedNamespaceCode = dot > 0 ? issue.EntryKey[..dot] : "";
        _searchKeyword = dot > 0 ? issue.EntryKey[(dot + 1)..] : issue.EntryKey;
        _pageIndex = 0; // 筛选已收窄，回到第一页即可看到目标词条
        _view = MatrixView.Entries;
        RebuildFilter();
    }

    // ------------------------------------------------------------------
    // 校验：全量（加载 / 运行检查，后台线程）与单条增量（保存 / AI 写回后）共用同一套单条校验逻辑；
    // 纯计算在 Core（EntryValidationSet），这里只负责线程调度、过期判断与状态落地。
    // ------------------------------------------------------------------

    /// <summary>词条数不超过这个值时在当前线程直接算（线程切换的开销比计算本身还大）；超过则下沉到线程池，不占用电路的调度线程。</summary>
    private const int InlineCheckThreshold = EntryValidationSet.InlineThreshold;

    /// <summary>
    /// 对当前范围（共享作用域 = 共享命名空间；项目作用域 = 当前项目）的全部词条做一轮校验：
    /// 结果同时供检查报告（按筛选派生）与矩阵单元格（查表）使用，渲染树里不再重复跑校验器。
    /// <para>
    /// 词条多时计算放到线程池里做；期间若词条树又被改动（<see cref="_dataRevision"/> 变化，如保存了某一格），
    /// 这份结果已过期，丢弃后重算一次；仍在被改动就回到当前线程同步算，保证落地的结果与当前数据一致。
    /// </para>
    /// </summary>
    private async Task RunValidationCheckAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revision = _dataRevision;
            var entries = _scopeEntries.ToArray();
            var cultures = ActiveCultures.ToArray();
            var defaultCulture = CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture;
            var terms = _glossaryTerms;
            var validator = Validator;

            var result = entries.Length <= InlineCheckThreshold
                ? EntryValidationSet.Compute(validator, entries, cultures, defaultCulture, terms)
                : await Task.Run(() => EntryValidationSet.Compute(validator, entries, cultures, defaultCulture, terms));

            if (revision == _dataRevision && !_disposed)
            {
                ApplyCheckResult(result);
                return;
            }
        }

        if (!_disposed)
        {
            ApplyCheckResult(EntryValidationSet.Compute(
                Validator,
                _scopeEntries.ToArray(),
                ActiveCultures.ToArray(),
                CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture,
                _glossaryTerms));
        }
    }

    private void ApplyCheckResult(EntryValidationResult result)
    {
        _matchedTerms = result.MatchedTerms;
        _issuesByEntry = result.IssuesByEntry;
        _issueLookup = result.Lookup;
        RebuildAfterCheck();
    }

    /// <summary>
    /// 增量校验：只重算给定的这几条词条（保存单格 / 新增 / AI 写回后），其余词条的校验结果原样保留；
    /// 然后重建聚合结果（问题总表、覆盖率、术语遵循率、标题语、报告筛选）——这些都是不含正则 / 术语匹配的廉价遍历。
    /// </summary>
    private void RecheckEntries(IReadOnlyCollection<TranslationEntry> entries)
    {
        var cultures = ActiveCultures;
        var defaultCulture = CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture;
        foreach (var entry in entries)
        {
            var (terms, issues) = EntryValidationSet.CheckOne(Validator, entry, cultures, defaultCulture, _glossaryTerms, _issueLookup);
            _matchedTerms[entry.Id] = terms;
            _issuesByEntry[entry.Id] = issues;
        }

        _dataRevision++;
        RebuildAfterCheck();
    }

    /// <summary>词条被删除：清掉它的全部校验痕迹（问题 / 命中术语 / 单元格备注）。</summary>
    private void DropEntryChecks(TranslationEntry entry)
    {
        _issuesByEntry.Remove(entry.Id);
        _matchedTerms.Remove(entry.Id);
        foreach (var culture in ActiveCultures)
        {
            _issueLookup.Remove((entry.Id, culture));
        }
    }

    /// <summary>校验结果或词条集合变化后，重建依赖它们的聚合状态。</summary>
    private void RebuildAfterCheck()
    {
        // 问题总表：按有序词条拼接，随后阻断数 / 标题语 / 报告筛选都由它派生
        var all = new List<ValidationIssue>();
        foreach (var entry in _scopeEntries)
        {
            if (_issuesByEntry.TryGetValue(entry.Id, out var list))
            {
                all.AddRange(list);
            }
        }

        _projectIssues = all;

        // 阻断级问题：Error 规则 + 术语库违规
        _blockingCount = all.Count(i => i.IsBlocking);

        ComputeCoverage(_scopeEntries);
        _lede = BuildLede();

        // 词条实例刚被就地替换 / 删除：筛选结果里的旧实例也要换掉，报告随之按新的筛选结果派生
        RebuildFilter();
    }

    /// <summary>矩阵单元格的校验备注：查校验时建好的表，不再在渲染树里重跑校验器。</summary>
    private IReadOnlyList<ValidationIssue> CellNotes(TranslationEntry entry, string culture) =>
        _issueLookup.TryGetValue((entry.Id, culture), out var notes) ? notes : NoNotes;

    /// <summary>「没有备注」共用同一个空列表实例（行组件靠列表实例判断备注有没有变）。</summary>
    private static readonly IReadOnlyList<ValidationIssue> NoNotes = [];

    /// <summary>某一行各语种单元格的校验备注（下标与 <see cref="ActiveCultures"/> 对齐），传给行组件。</summary>
    private IReadOnlyList<IReadOnlyList<ValidationIssue>> NotesFor(TranslationEntry entry)
    {
        var cultures = ActiveCultures;
        var notes = new IReadOnlyList<ValidationIssue>[cultures.Count];
        for (var i = 0; i < notes.Length; i++)
        {
            notes[i] = CellNotes(entry, cultures[i]);
        }

        return notes;
    }

    /// <summary>这一行当前的 AI 状态（决定 AI 列显示按钮还是「进行中」）。</summary>
    private EntryRow.AiState AiStateOf(TranslationEntry entry) =>
        _aiBusy is { } running && running.EntryId == entry.Id
            ? running.Overwrite ? EntryRow.AiState.Retranslating : EntryRow.AiState.Filling
            : EntryRow.AiState.Idle;

    /// <summary>
    /// 检查报告 = 当前命名空间 / 搜索筛选范围内词条的问题：由 <see cref="_filteredEntries"/> 直接派生（按词条 Id 取，只过滤、不重新校验）。
    /// 筛选条件变化走 <see cref="RebuildFilter"/>，校验结果变化走 <see cref="RebuildAfterCheck"/>，两处最后都落到这里，报告与矩阵始终一致。
    /// </summary>
    private void RefreshFilteredIssues()
    {
        if (_projectIssues.Count == 0)
        {
            _validationIssues = [];
        }
        else if (ReferenceEquals(_filteredEntries, _scopeEntries))
        {
            _validationIssues = _projectIssues; // 没有筛选：报告就是全量问题，不用再拷贝一遍
        }
        else
        {
            var filtered = new List<ValidationIssue>();
            foreach (var entry in _filteredEntries)
            {
                if (_issuesByEntry.TryGetValue(entry.Id, out var list))
                {
                    filtered.AddRange(list);
                }
            }

            _validationIssues = filtered;
        }

        _reportPageIndex = 0;
    }

    /// <summary>
    /// 每个语种的完成度：已审核 / AI 已译 / 待复核（含草稿）/ 缺失。
    /// 口径与发布前检查、manifest 共用 <see cref="CoverageCalculator"/>；
    /// 术语遵循率复用已缓存的源文案命中结果，只做判定、不再重新匹配术语。
    /// </summary>
    private void ComputeCoverage(IReadOnlyCollection<TranslationEntry> entries)
    {
        var defaultCulture = CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture;
        _entryCount = entries.Count;
        _coverage = CoverageCalculator.Compute(entries, ActiveCultures, defaultCulture).ToList();
        _glossaryCompliance = GlossaryComplianceCalculator.Compute(entries, ActiveCultures, defaultCulture, _matchedTerms).ToList();
    }

    private async Task RunAiBatchTranslateAsync()
    {
        _isBusy = true;
        try
        {
            var outcome = await TranslateEntriesAsync(FilteredEntries);

            if (outcome.Failure is not null)
            {
                Toasts.Error($"AI 批量补全未完成：{outcome.Failure}{DescribeSavedPart(outcome)}");
            }
            else if (outcome.Generated == 0)
            {
                Toasts.Success("AI 批量补全完成：当前范围内没有需要补全的译文。");
            }
            else
            {
                Toasts.Success($"AI 批量补全完成：写入 {outcome.Generated} 项译文" +
                               (outcome.NeedsReview > 0 ? $"，其中 {outcome.NeedsReview} 项标为「待复核」" : string.Empty) +
                               $"，跳过 {outcome.Skipped} 项已审核或已通过的译文。");
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    /// <summary>失败时说明已经落库的部分，让用户知道哪些已完成、不用重复处理。</summary>
    private static string DescribeSavedPart(AiRunOutcome outcome) =>
        outcome.Generated > 0
            ? $" 失败之前已保存 {outcome.Generated} 项译文" + (outcome.NeedsReview > 0 ? $"（其中 {outcome.NeedsReview} 项待复核）。" : "。")
            : " 没有写入任何译文。";

    // 单词条 AI 翻译的界面状态：（行 Id, 是否重译）表示哪一行正在跑哪个动作；PendingOverwrite 表示哪一条在等用户确认覆盖
    private (Guid EntryId, bool Overwrite)? _aiBusy;
    private PendingOverwrite? _pendingOverwrite;

    /// <summary>等待确认的覆盖动作：重新翻译会覆盖这些已人工审核的语种。</summary>
    private sealed record PendingOverwrite(TranslationEntry Entry, IReadOnlyList<string> ApprovedCultures);

    private string OverwriteConfirmMessage => _pendingOverwrite is { } pending
        ? $"「{EntryDisplayKey(pending.Entry)}」的 {string.Join("、", pending.ApprovedCultures)} 译文已人工审核，重新翻译会用 AI 结果覆盖它们，且不可撤销。"
        : string.Empty;

    /// <summary>
    /// 「重新翻译全部」会覆盖的目标语种：已经人工审核（Approved）且有内容的译文。
    /// 这决定了点下去要不要弹确认框；行上的提示用同一份口径（<see cref="EntryMatrix.ApprovedCultures"/>）。
    /// </summary>
    private IReadOnlyList<string> ApprovedCultures(TranslationEntry entry) =>
        EntryMatrix.ApprovedCultures(entry, TargetCultures);

    /// <summary>
    /// 单条词条的 AI 翻译入口。
    /// 补全（overwrite=false）：只翻译缺失 / 草稿 / 待复核的语种，不会覆盖已审核与 AI 已通过的内容，直接执行；
    /// 重新翻译（overwrite=true）：会覆盖全部非源语种，存在已人工审核的译文时先弹站内确认。
    /// </summary>
    private async Task RequestAiTranslateAsync(TranslationEntry entry, bool overwrite)
    {
        // 双击或确认框还开着时的重复点击一律忽略：一次只允许一个翻译动作或一个待确认请求
        if (_isBusy || _pendingOverwrite is not null)
        {
            return;
        }

        var approved = overwrite ? ApprovedCultures(entry) : [];

        if (approved.Count > 0)
        {
            _pendingOverwrite = new PendingOverwrite(entry, approved);
            return;
        }

        await ExecuteAiTranslateAsync(entry, overwrite);
    }

    private async Task ConfirmOverwriteAsync()
    {
        if (_pendingOverwrite is not { } pending)
        {
            return;
        }

        _pendingOverwrite = null;
        await ExecuteAiTranslateAsync(pending.Entry, overwrite: true);
    }

    private void CancelOverwrite() => _pendingOverwrite = null;

    private async Task ExecuteAiTranslateAsync(TranslationEntry entry, bool overwrite)
    {
        _isBusy = true;
        _aiBusy = (entry.Id, overwrite); // 这一行整列显示「AI 补全中…」或「AI 重新翻译中…」
        try
        {
            var outcome = await TranslateEntriesAsync([entry], overwrite);

            var action = overwrite ? "重新翻译" : "补全";
            if (outcome.Failure is not null)
            {
                Toasts.Error($"「{EntryDisplayKey(entry)}」AI {action}未完成：{outcome.Failure}{DescribeSavedPart(outcome)}");
                return;
            }

            if (outcome.Generated == 0)
            {
                Toasts.Success($"「{EntryDisplayKey(entry)}」各语种译文都已就绪，没有需要{action}的内容。");
                return;
            }

            Toasts.Success($"「{EntryDisplayKey(entry)}」AI 已{action} {outcome.Generated} 项译文" +
                           (outcome.NeedsReview > 0 ? $"，其中 {outcome.NeedsReview} 项违反规则/术语库，已标为「待复核」。" : "。"));
        }
        finally
        {
            _isBusy = false;
            _aiBusy = null;
        }
    }

    /// <summary>
    /// AI 翻译入口：编排（按语种并发、失败隔离、批量写回）在 <see cref="EntryAiTranslator"/>，
    /// 页面只负责把写回结果就地替换进内存词条树，并决定之后是增量校验还是整页重载。
    /// overwrite=false 时只处理「缺失、草稿、待复核」的译文；overwrite=true 时全部非源语种都重新翻译。
    /// </summary>
    private async Task<AiRunOutcome> TranslateEntriesAsync(
        IReadOnlyList<TranslationEntry> entries,
        bool overwrite = false)
    {
        var changed = new Dictionary<Guid, TranslationEntry>(); // 已写回并替换进内存的词条（同一词条多个语种时以最后一次为准）

        var outcome = await AiTranslator.TranslateAsync(
            entries,
            SelectedProjectCode,
            TargetCultures,
            CurrentProject?.DefaultCulture ?? LinguaCultures.DefaultSourceCulture,
            ScopeOf,
            _glossaryTerms,
            overwrite,
            saved => DispatchAsync<IReadOnlyList<TranslationEntry>>(() =>
            {
                // 必须回到电路的同步上下文：ReplaceEntriesInPlace 改的是页面状态（内存词条树 + 有序词条缓存）
                var missing = ReplaceEntriesInPlace(saved);
                if (missing.Count == 0)
                {
                    foreach (var entry in saved)
                    {
                        changed[entry.Id] = entry;
                    }
                }

                return missing;
            }));

        // 无论中途是否失败，已落库并替换进内存的词条都要重新校验，页面状态才与数据一致
        if (outcome.NeedsReload)
        {
            await ReloadAndCheckAsync();
        }
        else if (changed.Count > 0)
        {
            RecheckEntries(changed.Values);
        }

        return outcome;
    }

    private async Task PublishCurrentProjectAsync()
    {
        if (IsSharedScope)
        {
            return;
        }

        _isBusy = true;
        try
        {
            var snapshot = await Publisher.PublishProjectAsync(SelectedProjectCode, "管理后台手动发布");

            // 草稿与线上内容完全一致：发布器没有生成新版本（也不再写发布历史），只提示用户
            if (snapshot is null)
            {
                var hash = _currentProject?.LastPublishedVersionHash;
                _statusMessage = string.IsNullOrWhiteSpace(hash)
                    ? "草稿与线上内容一致，没有新内容要发布。"
                    : $"草稿与线上内容一致，没有新内容要发布，线上版本指纹仍是 {hash}。若刚修改了译文请确认：译文状态是否达到发布策略（草稿 / 待复核不进发布文件），或「导入与发布」页发布前检查里的键路径冲突提示。";
                return;
            }

            // 发布只会改项目的「最近发布」两个字段，词条与译文都没变：直接用发布结果更新内存里的项目，
            // 不再为刷新一行文字把整棵词条树 + 共享命名空间 + 影响面 + 术语重新拉一遍
            if (_currentProject is { } published && string.Equals(published.Code, SelectedProjectCode, StringComparison.Ordinal))
            {
                published.LastPublishedAt = snapshot.PublishedAt;
                published.LastPublishedVersionHash = snapshot.VersionHash;
            }

            _statusMessage = $"发布成功！已生成 App_Data/i18n/{SelectedProjectCode}/*.json（只经 /i18n 分发端点对外，版本指纹: {snapshot.VersionHash}），并已通过 SSE 通知业务端。";
        }
        catch (PublishBlockedException ex)
        {
            _statusMessage = $"{ex.Message}。请先处理这些问题，或到「导入与发布」页查看检查详情，填写原因后强制发布。";
        }
        finally
        {
            _isBusy = false;
        }
    }
}
