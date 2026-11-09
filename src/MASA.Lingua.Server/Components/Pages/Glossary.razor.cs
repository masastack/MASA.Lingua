using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Localization;
using MASA.Lingua.Core.Publishing;
using MASA.Lingua.Core.Validation;
using MASA.Lingua.Server.Services;

namespace MASA.Lingua.Server.Components.Pages;

public partial class Glossary
{
    /// <summary>术语命中表的渲染上限：命中词条可能成百上千，全部渲染会拖慢整个页面。</summary>
    private const int MaxHitRows = 200;

    /// <summary>当前项目全量（词条树）：项目作用域只加载顶栏选中的项目。</summary>
    private Project? _currentProject;

    /// <summary>全局作用域下参与命中分析的全部项目（各项目用自己的生效术语）；项目作用域下只含当前项目。</summary>
    private List<Project> _projects = [];
    private List<TranslationNamespace> _sharedNamespaces = [];
    private List<GlossaryTerm> _terms = [];
    private List<GlossaryTerm> _globalTerms = [];
    private Dictionary<Guid, int> _refCounts = [];
    private List<TermHit> _hits = [];
    private List<GlossaryCompliance> _compliance = [];

    /// <summary>一条词条的源文案命中了哪些术语，以及哪些语种的译文没有遵循术语。</summary>
    private sealed record TermHit(string Origin, TranslationEntry Entry, IReadOnlyList<GlossaryTerm> Terms, IReadOnlyList<string> ViolatedCultures);

    // false = 全局术语；true = 顶栏当前项目的项目术语
    private bool _useProject;
    private bool _formOpen;

    private Guid? _editingId;
    private string _sourceTerm = "";
    private string? _description;
    private bool _doNotTranslate;
    private bool _caseSensitive;
    private readonly Dictionary<string, string> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _variantInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _forbiddenInputs = new(StringComparer.OrdinalIgnoreCase);

    // 提示状态与加载骨架标记（_isLoading）由 LinguaPageBase 提供。

    private Project? CurrentProject => _currentProject;

    private Project? ScopeProject => _useProject ? CurrentProject : null;

    private Guid? ScopeProjectId => ScopeProject?.Id;

    /// <summary>
    /// 源术语所属语种：项目作用域取项目默认语种；全局作用域下若所有项目默认语种一致则取该语种。
    /// </summary>
    private string? ScopeSourceCulture
    {
        get
        {
            if (ScopeProject is not null)
            {
                return ScopeProject.DefaultCulture;
            }

            var defaults = _projects.Select(p => p.DefaultCulture).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return defaults.Count == 1 ? defaults[0] : null;
        }
    }

    private string ScopeSourceLabel => ScopeSourceCulture ?? "项目默认语种";

    /// <summary>标题下的一句话：术语命中了多少词条，有没有被违反（由词条与校验结果算出）。</summary>
    private string Lede
    {
        get
        {
            var inherited = ScopeProject is null ? 0 : _globalTerms.Count(g => !IsOverridden(g));
            var inForce = _terms.Count + inherited;
            if (inForce == 0)
            {
                return "术语库还没有术语。点击「新增术语」，统一品牌词与专有名词的译法。";
            }

            if (_hits.Count == 0)
            {
                return $"{inForce} 条术语生效，暂时没有词条的源文案命中它们。";
            }

            var violations = _hits.Sum(h => h.ViolatedCultures.Count);
            return violations == 0
                ? $"{inForce} 条术语生效，命中 {_hits.Count} 条词条，译文全部遵循。"
                : $"{inForce} 条术语生效，命中 {_hits.Count} 条词条，其中 {violations} 处译文没有遵循术语。";
        }
    }

    /// <summary>
    /// 需要维护标准译法的目标语种：项目作用域取项目 SupportedCultures；全局作用域取所有项目语种并集；
    /// 再并上已有术语里出现过的语种，保证历史数据可见、可编辑。
    /// </summary>
    private IReadOnlyList<string> ScopeCultures => _scopeCultures;

    /// <summary>
    /// 语种列表：表格每一行、每个表单字段都要用，所以算好缓存，不在渲染循环里重算；
    /// 项目 / 术语加载或变化后由 <see cref="RebuildScopeCultures"/> 重建。
    /// </summary>
    private List<string> _scopeCultures = [];

    private void RebuildScopeCultures()
    {
        var set = new List<string>();

        void Add(string? culture)
        {
            if (!string.IsNullOrWhiteSpace(culture) &&
                !string.Equals(culture, ScopeSourceCulture, StringComparison.OrdinalIgnoreCase) &&
                !set.Contains(culture, StringComparer.OrdinalIgnoreCase))
            {
                set.Add(culture);
            }
        }

        if (ScopeProject is not null)
        {
            ScopeProject.SupportedCultures.ForEach(Add);
        }
        else
        {
            _projects.SelectMany(p => p.SupportedCultures).ToList().ForEach(Add);
        }

        _terms.SelectMany(t => t.TargetTranslations.Keys).ToList().ForEach(Add);

        if (set.Count == 0)
        {
            Add("en-US");
            Add("ja-JP");
        }

        _scopeCultures = set;
    }

    // ------------------------------------------------------------------
    // 加载守卫（_loadVersion / _disposed / _isLoading / IsCurrent / EndLoad）来自 LinguaPageBase；
    // 这里只多一个命中分析的代数（命中分析会多次触发，与数据加载不同步）。
    // ------------------------------------------------------------------
    private int _usageVersion;

    /// <summary>命中分析（后台线程）进行中：术语表已是最新，命中词条 / 引用数 / 遵循率稍后更新。</summary>
    private bool _usageComputing;

    protected override async Task OnInitializedAsync()
    {
        Scope.Changed += OnScopeChanged;
        try
        {
            await LoadTermsAsync();
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载术语数据失败：{ex.Message}");
        }
    }

    protected override void OnDisposing() => Scope.Changed -= OnScopeChanged;

    /// <summary>
    /// 顶栏切换项目：先显示加载骨架，数据到位后整体渲染；
    /// 新项目没有本地配置时自动退回全局术语作用域。
    /// fire-and-forget 的事件回调：异常在这里接住并落到状态栏，否则会变成未观察异常静默丢失。
    /// </summary>
    private void OnScopeChanged() => _ = InvokeAsync(async () =>
    {
        if (_disposed)
        {
            return;
        }

        ResetForm();
        _formOpen = false;
        _statusMessage = null;
        _isLoading = true;
        StateHasChanged();
        try
        {
            await LoadTermsAsync();
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载术语数据失败：{ex.Message}");
        }

        if (!_disposed)
        {
            StateHasChanged();
        }
    });

    /// <summary>
    /// 加载术语与词条数据：共享命名空间与当前项目并行取回；
    /// 项目作用域只加载当前项目的词条树，全局作用域（术语命中分析要对每个项目各用其生效术语）保持整库加载。
    /// 全部数据先装进局部变量，确认自己仍是最新一次加载后才一次性赋给页面状态。
    /// </summary>
    private async Task LoadTermsAsync()
    {
        var version = NextLoadVersion();
        _isLoading = true;
        try
        {
            var sharedTask = Repository.GetSharedNamespacesAsync();
            var currentTask = Scope.IsShared
                ? Task.FromResult<Project?>(null)
                : Repository.GetProjectByCodeAsync(Scope.ProjectCode);

            var project = await currentTask;

            // 项目作用域只分析当前项目的词条；全局作用域（以及项目内查看全局术语时）要对每个项目各用其生效术语，需要整库。
            // 只读消费：命中分析只遍历不落笔，走免克隆重载 —— 每次进页面深克隆整库是纯粹的内存尖峰
            //（项目多、词条多时一次克隆几十 MB，全局作用域每个电路都克隆一遍）。
            var useProject = project is not null && _useProject;
            var projects = useProject
                ? [project!]
                : (await Repository.GetAllProjectsReadOnlyAsync()).ToList();

            Guid? scopeProjectId = useProject ? project!.Id : null;
            var terms = (await Repository.GetGlossaryTermsByScopeAsync(scopeProjectId)).ToList();
            var globalTerms = scopeProjectId is null
                ? []
                : (await Repository.GetGlossaryTermsByScopeAsync(null)).ToList();
            var shared = (await sharedTask).ToList();

            if (!IsCurrent(version))
            {
                return;
            }

            _currentProject = project;
            _useProject = useProject;
            _projects = projects;
            _terms = terms;
            _globalTerms = globalTerms;
            _sharedNamespaces = shared;
            RebuildScopeCultures();
            await RecomputeUsageAsync();
        }
        finally
        {
            EndLoad(version);
        }
    }

    /// <summary>
    /// 术语增删改之后只重载术语本身（两条小查询），词条树 / 项目 / 共享命名空间不变、不重取；
    /// 术语表立即是最新的，命中分析（对全部词条匹配术语，最重的一段）放到后台线程重算，完成后再更新命中表与遵循率。
    /// </summary>
    private async Task ReloadTermsAsync()
    {
        var version = _loadVersion; // 期间若发生整体重载（切换项目 / 作用域），代数会变，这份结果就丢弃
        var scopeProjectId = ScopeProjectId;
        var terms = (await Repository.GetGlossaryTermsByScopeAsync(scopeProjectId)).ToList();
        var globalTerms = scopeProjectId is null
            ? []
            : (await Repository.GetGlossaryTermsByScopeAsync(null)).ToList();

        if (!IsCurrent(version))
        {
            return;
        }

        _terms = terms;
        _globalTerms = globalTerms;
        RebuildScopeCultures();
        await RecomputeUsageAsync();
    }

    /// <summary>命中分析的输入快照：纯数据，可以交给后台线程计算。</summary>
    private sealed record UsageInput(
        IReadOnlyList<(string Origin, string DefaultCulture, List<TranslationEntry> Entries, IReadOnlyList<GlossaryTerm> Terms)> Evaluations,
        IReadOnlyList<string> Cultures,
        HashSet<Guid> GlobalIds,
        bool ProjectScope);

    private sealed record UsageResult(List<TermHit> Hits, Dictionary<Guid, int> RefCounts, List<GlossaryCompliance> Compliance);

    /// <summary>
    /// 重算术语命中分析：在当前线程只做快照，真正的计算放进线程池；
    /// 期间再次触发（又改了术语）则以最后一次为准，较早的结果丢弃。
    /// </summary>
    private async Task RecomputeUsageAsync()
    {
        var version = ++_usageVersion;
        _usageComputing = true;
        UsageResult result;
        try
        {
            var input = BuildUsageInput();
            var validator = Validator;
            result = await Task.Run(() => ComputeUsage(validator, input));
        }
        finally
        {
            if (version == _usageVersion)
            {
                _usageComputing = false;
            }
        }

        if (version != _usageVersion || _disposed)
        {
            return;
        }
        _hits = result.Hits;
        _refCounts = result.RefCounts;
        _compliance = result.Compliance;
    }

    private UsageInput BuildUsageInput()
    {
        var globals = ScopeProject is null ? _terms : _globalTerms;
        var globalIds = globals.Select(g => g.Id).ToHashSet();

        var evaluations = new List<(string Origin, string DefaultCulture, List<TranslationEntry> Entries, IReadOnlyList<GlossaryTerm> Terms)>();
        if (ScopeProject is { } project)
        {
            var shared = _sharedNamespaces.Where(s => project.SharedNamespaceCodes.Contains(s.Code));
            evaluations.Add((project.Code, project.DefaultCulture, shared.Concat(project.Namespaces).SelectMany(n => n.Entries).ToList(), EffectiveTerms(_terms, globals)));
        }
        else
        {
            foreach (var p in _projects)
            {
                evaluations.Add((p.Code, p.DefaultCulture, p.Namespaces.SelectMany(n => n.Entries).ToList(), EffectiveTerms(p.GlossaryTerms, globals)));
            }

            evaluations.Add(("全局共享", LinguaCultures.DefaultSourceCulture, _sharedNamespaces.SelectMany(n => n.Entries).ToList(), globals));
        }

        return new UsageInput(evaluations, [.. ScopeCultures], globalIds, ScopeProject is not null);
    }

    /// <summary>
    /// 哪些词条的源文案命中了术语，以及这些词条的译文有没有遵循术语。
    /// 命中逻辑（<see cref="GlossaryMatcher"/>）与遵循判断（<see cref="DeterministicTranslationValidator"/>）都与「规则检查」完全一致。
    /// 项目作用域按「全局术语 + 项目术语（同名覆盖）」判断；全局作用域对每个项目各用它自己的生效术语，只展示由全局术语命中的词条。
    /// 每条词条的源文案只匹配一次术语（命中与语种无关），遵循率统计、命中表、逐语种校验共用这一份结果。
    /// </summary>
    private static UsageResult ComputeUsage(DeterministicTranslationValidator validator, UsageInput input)
    {
        // 术语遵循率：项目作用域为该项目；全局作用域为各项目合计（每个项目用自己的生效术语）
        var sums = new Dictionary<string, (int Checked, int Followed)>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<TermHit>();
        var refCounts = new Dictionary<Guid, int>();

        foreach (var (origin, defaultCulture, entries, terms) in input.Evaluations)
        {
            var matchedByEntry = new Dictionary<Guid, IReadOnlyList<GlossaryTerm>>(entries.Count);
            if (terms.Count > 0)
            {
                foreach (var entry in entries)
                {
                    matchedByEntry[entry.Id] = GlossaryMatcher.FindMatchedTerms(entry.SourceText, terms);
                }
            }

            foreach (var c in GlossaryComplianceCalculator.Compute(entries, input.Cultures, defaultCulture, matchedByEntry))
            {
                var prev = sums.GetValueOrDefault(c.Culture);
                sums[c.Culture] = (prev.Checked + c.Checked, prev.Followed + c.Followed);
            }

            foreach (var entry in entries
                         .OrderBy(e => e.NamespaceCode, StringComparer.Ordinal)
                         .ThenBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!matchedByEntry.TryGetValue(entry.Id, out var allMatched))
                {
                    continue;
                }

                var matched = allMatched
                    .Where(t => input.ProjectScope || input.GlobalIds.Contains(t.Id))
                    .ToList();
                if (matched.Count == 0)
                {
                    continue;
                }

                var violated = new List<string>();
                foreach (var culture in input.Cultures)
                {
                    var translation = entry.Translations.FirstOrDefault(t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));
                    if (translation is null)
                    {
                        continue;
                    }

                    var issues = validator.ValidateEntryWithMatches(entry, culture, translation.Text, allMatched);
                    if (issues.Any(i => i.RuleCode.StartsWith("GLOSSARY_", StringComparison.Ordinal)))
                    {
                        violated.Add(culture);
                    }
                }

                hits.Add(new TermHit(origin, entry, matched, violated));
                foreach (var term in matched)
                {
                    refCounts[term.Id] = refCounts.GetValueOrDefault(term.Id) + 1;
                }
            }
        }

        var compliance = input.Cultures
            .Where(sums.ContainsKey)
            .Select(c => new GlossaryCompliance(c, sums[c].Checked, sums[c].Followed))
            .ToList();
        return new UsageResult(hits, refCounts, compliance);
    }

    /// <summary>项目的生效术语：全局术语中被项目同名术语覆盖的不算，再加上项目自己的术语。</summary>
    private static IReadOnlyList<GlossaryTerm> EffectiveTerms(IEnumerable<GlossaryTerm> own, IEnumerable<GlossaryTerm> globals)
    {
        var ownList = own.ToList();
        var overridden = ownList.Select(t => t.SourceTerm.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return globals.Where(g => !overridden.Contains(g.SourceTerm.Trim())).Concat(ownList).ToList();
    }

    private int RefCount(GlossaryTerm term) => _refCounts.GetValueOrDefault(term.Id);

    private async Task SetScopeAsync(bool useProject)
    {
        if (useProject == _useProject)
        {
            return;
        }

        _useProject = useProject && CurrentProject is not null;
        ResetForm();
        _formOpen = false;
        _statusMessage = null;
        _isLoading = true;
        StateHasChanged();
        try
        {
            await LoadTermsAsync();
        }
        catch (Exception ex)
        {
            ShowStatusError($"加载术语数据失败：{ex.Message}");
        }

        StateHasChanged();
    }

    private bool IsOverridden(GlossaryTerm global) =>
        _terms.Any(t => string.Equals(t.SourceTerm.Trim(), global.SourceTerm.Trim(), StringComparison.OrdinalIgnoreCase));

    private string GetInput(string culture) => _inputs.GetValueOrDefault(culture, "");

    private static RenderFragment RenderTarget(GlossaryTerm term, string culture) => builder =>
    {
        if (term.DoNotTranslate)
        {
            builder.AddContent(0, term.SourceTerm);
        }
        else
        {
            var hasTarget = term.TargetTranslations.TryGetValue(culture, out var text) && !string.IsNullOrWhiteSpace(text);
            var variants = term.AcceptedVariants.TryGetValue(culture, out var v) ? v : [];
            var forbidden = term.ForbiddenTranslations.TryGetValue(culture, out var f) ? f : [];

            if (hasTarget)
            {
                builder.AddContent(0, text);
            }
            else if (forbidden.Count == 0)
            {
                builder.OpenElement(1, "span");
                builder.AddAttribute(2, "class", "zero");
                builder.AddAttribute(3, "title", "未设置时该语种的译文不受此术语约束");
                builder.AddContent(4, "未设置");
                builder.CloseElement();
            }

            if (variants.Count > 0)
            {
                builder.OpenElement(5, "div");
                builder.AddAttribute(6, "class", "sub");
                builder.AddContent(7, $"变体：{string.Join("、", variants)}");
                builder.CloseElement();
            }

            if (forbidden.Count > 0)
            {
                builder.OpenElement(8, "div");
                builder.AddAttribute(9, "class", "sub neg-text");
                builder.AddContent(10, $"禁用：{string.Join("、", forbidden)}");
                builder.CloseElement();
            }
        }
    };

    private void StartCreate()
    {
        ResetForm();
        _statusMessage = null;
        _formOpen = true;
    }

    private void StartEdit(GlossaryTerm term)
    {
        _editingId = term.Id;
        _sourceTerm = term.SourceTerm;
        _description = term.Description;
        _doNotTranslate = term.DoNotTranslate;
        _caseSensitive = term.CaseSensitive;
        _inputs.Clear();
        _variantInputs.Clear();
        _forbiddenInputs.Clear();
        foreach (var (culture, text) in term.TargetTranslations)
        {
            _inputs[culture] = text;
        }

        foreach (var (culture, words) in term.AcceptedVariants)
        {
            _variantInputs[culture] = string.Join(", ", words);
        }

        foreach (var (culture, words) in term.ForbiddenTranslations)
        {
            _forbiddenInputs[culture] = string.Join(", ", words);
        }

        _statusMessage = null;
        _formOpen = true;
    }

    private void CancelEdit()
    {
        ResetForm();
        _formOpen = false;
    }

    private void ResetForm()
    {
        _editingId = null;
        _sourceTerm = "";
        _description = null;
        _doNotTranslate = false;
        _caseSensitive = false;
        _inputs.Clear();
        _variantInputs.Clear();
        _forbiddenInputs.Clear();
    }

    private static List<string> SplitWords(string? text) =>
        (text ?? string.Empty).Split([',', '，', ';', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private async Task SaveTermAsync()
    {
        var term = new GlossaryTerm
        {
            // 编辑时沿用 Id，仓储层会更新而不是新增
            Id = _editingId ?? Guid.NewGuid(),
            ProjectId = ScopeProjectId,
            SourceTerm = _sourceTerm.Trim(),
            Description = string.IsNullOrWhiteSpace(_description) ? null : _description.Trim(),
            DoNotTranslate = _doNotTranslate,
            CaseSensitive = _caseSensitive,
            TargetTranslations = new(StringComparer.OrdinalIgnoreCase)
        };

        if (!_doNotTranslate)
        {
            foreach (var (culture, text) in _inputs)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    term.TargetTranslations[culture] = text.Trim();
                }
            }

            foreach (var (culture, text) in _variantInputs)
            {
                var words = SplitWords(text);
                if (words.Count > 0)
                {
                    term.AcceptedVariants[culture] = words;
                }
            }

            foreach (var (culture, text) in _forbiddenInputs)
            {
                var words = SplitWords(text);
                if (words.Count > 0)
                {
                    term.ForbiddenTranslations[culture] = words;
                }
            }
        }

        try
        {
            await Repository.SaveGlossaryTermAsync(term);
        }
        catch (GlossaryValidationException ex)
        {
            ShowStatusError(ex.Message);
            return;
        }

        _statusMessage = $"术语「{term.SourceTerm}」已保存。已有译文不会自动修改，请到词条矩阵运行「检查」查看受影响的词条。";
        ResetForm();
        _formOpen = false;
        await ReloadTermsAsync();
    }

    private async Task DeleteTermAsync(GlossaryTerm term)
    {
        var scopeText = ScopeProject is null
            ? "该术语为全局术语，删除后将影响所有项目"
            : $"删除后项目「{ScopeProject.Code}」将不再受该术语约束（若存在同名全局术语，则恢复使用全局术语）";

        var confirmed = await JS.InvokeAsync<bool>(
            "confirm",
            $"确定删除术语「{term.SourceTerm}」吗？\n{scopeText}。已有译文不会被修改。");
        if (!confirmed)
        {
            return;
        }

        await Repository.DeleteGlossaryTermAsync(term.Id);
        if (_editingId == term.Id)
        {
            ResetForm();
            _formOpen = false;
        }

        _statusMessage = $"术语「{term.SourceTerm}」已删除。";
        await ReloadTermsAsync();
    }
}
