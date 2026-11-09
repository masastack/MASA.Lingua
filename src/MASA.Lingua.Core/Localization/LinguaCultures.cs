using System.Collections.Concurrent;
using System.Globalization;

namespace MASA.Lingua.Core.Localization;

/// <summary>
/// 语种代码校验异常（供导入接口、管理页面直接展示 Message）。
/// </summary>
public sealed class LinguaValidationException(string message) : Exception(message);

/// <summary>
/// 可选语种（下拉框的一项）：代码 + 本地语言名称 + 英文名称 + 中文名称。
/// <para>
/// 三个名称都用于搜索：用户可能记得「한국어」、也可能只记得 Korean 或 韩语。
/// 只按代码匹配会让近 900 项的下拉基本没法手输查找。
/// </para>
/// </summary>
public sealed record LinguaCultureOption(string Code, string NativeName, string EnglishName, string ChineseName);

/// <summary>
/// 语种代码规范化、合法性与可选语种目录：
/// 1. 把 en-us / zh_CN / ZH-cn 之类的写法规范为 BCP47 标准大小写（en-US / zh-CN）；
/// 2. 「合法」的唯一判据是 ICU：用 predefinedOnly 模式的 <see cref="CultureInfo.GetCultureInfo(string, bool)"/>
///    问运行时认不认识这个预定义文化。没有任何人工清单、没有语言特例；
/// 3. 提供语种的本地语言名称（管理后台与 manifest 共用）。
/// </summary>
public static class LinguaCultures
{
    /// <summary>词条源文案默认所在语种（共享命名空间没有所属项目时使用）。</summary>
    public const string DefaultSourceCulture = "zh-CN";

    /// <summary>
    /// 惰性构建一次：枚举 ICU 语种有几百项、要读 ICU 数据表，属静态数据，进程内只算一次。
    /// </summary>
    private static readonly Lazy<IReadOnlyList<LinguaCultureOption>> CatalogLazy =
        new(BuildCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 可选语种目录：ICU 枚举出的预定义文化，按语种代码排序
    /// （按代码而不是按本地名称：名单长达数百项，代码排序可预测，同一语言的不同地区变体也会排在一起）。
    /// <para>
    /// 注意这只是「下拉框的候选来源」，不是「合法语种的集合」——合法性由 <see cref="IsKnownCulture"/> 判定。
    /// 两者不等价：ICU 枚举结果里没有 zh-CN / zh-TW 这类地区式中文代码（它规范成 zh-Hans-CN / zh-Hant-TW），
    /// 但这些代码是真实存在的预定义文化，校验必须放行；它们要不要出现在某个下拉里，取决于该项目是否在用
    /// （见 <see cref="WithExtraCodes"/>）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<LinguaCultureOption> Catalog => CatalogLazy.Value;

    private static IReadOnlyList<LinguaCultureOption> BuildCatalog()
    {
        var byCode = new Dictionary<string, LinguaCultureOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            if (TryGetPredefined(culture.Name) is { } option)
            {
                byCode[option.Code] = option;
            }
        }

        return Sort(byCode.Values);
    }
    /// <summary>
    /// 在目录基础上补上「额外必须可选」的语种（通常是项目已支持的语种与默认语种），去重后重新排序。
    /// <para>
    /// 这是「显示」层面的补齐，不是合法性特例：ICU 枚举不到 zh-CN，但一个项目正在用它，
    /// 或它就是默认源语种时，下拉里必须有它，否则用户无法把它加回来、也无法在下拉里确认当前选择。
    /// 补进来的代码同样要过 <see cref="IsKnownCulture"/> 判定，不满足的直接丢弃。
    /// </para>
    /// </summary>
    public static IReadOnlyList<LinguaCultureOption> WithExtraCodes(IEnumerable<string>? extraCodes)
    {
        if (extraCodes is null)
        {
            return Catalog;
        }

        var byCode = new Dictionary<string, LinguaCultureOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in Catalog)
        {
            byCode[option.Code] = option;
        }

        var added = false;
        foreach (var raw in extraCodes)
        {
            var code = Normalize(raw);
            if (code is null || byCode.ContainsKey(code) || TryGetPredefined(code) is not { } option)
            {
                continue;
            }

            byCode[code] = option;
            added = true;
        }

        return added ? Sort(byCode.Values) : Catalog;
    }

    private static IReadOnlyList<LinguaCultureOption> Sort(IEnumerable<LinguaCultureOption> options)
    {
        var list = options.ToList();
        list.Sort(static (a, b) => string.CompareOrdinal(a.Code, b.Code));
        return list;
    }

    /// <summary>
    /// 已判定的预定义文化（值为 null ＝ 不是预定义文化），按代码记忆化，见 <see cref="TryGetPredefined"/>。
    /// </summary>
    private static readonly ConcurrentDictionary<string, LinguaCultureOption?> PredefinedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 用 ICU 判定「这是一个真实存在的预定义文化」，且写法与 <see cref="Normalize"/> 的口径一致。
    /// <para>
    /// predefinedOnly 是关键：它只认 ICU 预定义文化，拒绝 xx-YY 这类可以凭空合成出来的假代码，
    /// 也拒绝 zh-CHS 这类不符合 BCP47 脚本段写法的退役别名；而 zh-CN / iw / tl 这些真实存在的旧别名可以过。
    /// 整个类只有这一处判断「语种是否合法」，不含任何人工名单或语言特例。
    /// </para>
    /// <para>
    /// 判定结果按代码记忆化：<see cref="GetName"/> 在矩阵渲染热路径上被高频调用（每个单元格的
    /// data-label、每列表头都要取语种名），而非中文 UI 环境下（容器默认 Invariant）读中文名要靠
    /// 独立线程切换一次 <see cref="CultureInfo.CurrentUICulture"/> —— 不缓存的话每渲染一个单元格
    /// 就要创建并 Join 一个线程。键是已过规范化检查的写法，数量以 ICU 目录（约几百项）为上限。
    /// </para>
    /// </summary>
    private static LinguaCultureOption? TryGetPredefined(string? code)
    {
        if (code is null || !string.Equals(Normalize(code), code, StringComparison.Ordinal))
        {
            return null;
        }

        return PredefinedCache.GetOrAdd(code, static c =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(c, predefinedOnly: true);
                return new LinguaCultureOption(
                    c,
                    NativeNameOf(culture),
                    EnglishNameOf(culture),
                    ChineseNameOf(culture));
            }
            catch (CultureNotFoundException)
            {
                // 不变文化（代码为空）/ 格式合法但本运行时 ICU 没有的文化（如 xx-YY）
                return null;
            }
        });
    }

    /// <summary>ICU 的本地语言名称；个别文化没有 NativeName 时退回 DisplayName，再退回代码。</summary>
    private static string NativeNameOf(CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(culture.NativeName) ? culture.NativeName
        : !string.IsNullOrWhiteSpace(culture.DisplayName) ? culture.DisplayName
        : culture.Name;

    /// <summary>
    /// 语种的英文名称（供搜索）：取 <see cref="CultureInfo.EnglishName"/>。
    /// 它来自 ICU 数据、与运行环境区域设置无关。
    /// </summary>
    private static string EnglishNameOf(CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(culture.EnglishName) ? culture.EnglishName : culture.Name;

    /// <summary>
    /// 语种的中文名称（供搜索）：如 ko-KR → 韩语（韩国）。
    /// <para>
    /// <see cref="CultureInfo.DisplayName"/> 随 <see cref="CultureInfo.CurrentUICulture"/> 变化，
    /// 容器里可能是 en-US（于是变成 "Korean (Korea)"，搜「韩语」就搜不到）。
    /// 所以在固定 zh-CN 的线程上取：Culture 是线程级状态，不影响其他线程，也不依赖部署环境的区域设置。
    /// </para>
    /// </summary>
    private static string ChineseNameOf(CultureInfo culture)
    {
        // 已经就是中文环境时直接读，省掉一次线程切换（目录构建时会走几百次）
        if (string.Equals(CultureInfo.CurrentUICulture.Name, ChineseUiCulture, StringComparison.OrdinalIgnoreCase))
        {
            return DisplayNameUnder(culture, null);
        }

        return DisplayNameUnder(culture, ChineseUiCulture);
    }

    /// <summary>取中文名的目标 UI 区域（本平台管理后台的语言）。</summary>
    private const string ChineseUiCulture = "zh-CN";

    /// <summary>
    /// 在（可选的）指定 UI 区域下读 DisplayName。
    /// <paramref name="uiCulture"/> 为 null 表示沿用当前线程的区域设置。
    /// </summary>
    private static string DisplayNameUnder(CultureInfo culture, string? uiCulture)
    {
        if (uiCulture is null)
        {
            return Fallback(culture.DisplayName, culture.Name);
        }

        var captured = "";
        // 独立线程：CurrentUICulture 是线程静态的，改动不会泄漏到请求线程
        var worker = new Thread(() =>
        {
            var original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
                captured = culture.DisplayName;
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        })
        {
            IsBackground = true,
            Name = "LinguaCultures.ChineseName"
        };
        worker.Start();
        worker.Join();

        return Fallback(captured, culture.Name);
    }

    private static string Fallback(string value, string code) =>
        !string.IsNullOrWhiteSpace(value) ? value : code;

    /// <summary>
    /// 这个代码是不是本运行时认识的语种（导入时按文件名识别语种、语种配置校验都用它）。
    /// 与 <see cref="Resolve"/> 共用同一判定，不依赖目录枚举结果。
    /// </summary>
    public static bool IsKnownCulture(string? code) => TryGetPredefined(Normalize(code)) is not null;

    /// <summary>
    /// 语种的本地语言名称（取自 ICU 的 NativeName，如 ja-JP → 日本語 (日本)）；不认识时原样返回代码。
    /// </summary>
    public static string GetName(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return culture;
        }

        return TryGetPredefined(culture)?.NativeName ?? culture;
    }

    /// <summary>
    /// 规范化语种代码：去空白、下划线换连字符、语言小写、文字脚本首字母大写、地区大写。
    /// 格式不合法（不是 ll、ll-RR、ll-Ssss、ll-Ssss-RR 之一）时返回 null。
    /// </summary>
    public static string? Normalize(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return null;
        }

        var parts = culture.Trim().Replace('_', '-').Split('-');
        if (parts.Length is < 1 or > 3 || parts.Any(p => p.Length == 0))
        {
            return null;
        }

        var language = parts[0];
        if (language.Length is < 2 or > 3 || !language.All(char.IsAsciiLetter))
        {
            return null;
        }

        var result = new List<string> { language.ToLowerInvariant() };
        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 4 && part.All(char.IsAsciiLetter) && i == 1)
            {
                result.Add(char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant());
            }
            else if ((part.Length == 2 && part.All(char.IsAsciiLetter)) || (part.Length == 3 && part.All(char.IsAsciiDigit)))
            {
                result.Add(part.ToUpperInvariant());
            }
            else
            {
                return null;
            }
        }

        return string.Join('-', result);
    }

    /// <summary>
    /// 解析用户输入的语种：先规范化，再要求它属于项目已有语种或 ICU 预定义文化。
    /// 与项目已有语种仅大小写/分隔符不同的写法会被映射回项目里已有的那个代码。
    /// </summary>
    /// <exception cref="LinguaValidationException">格式不合法或不是已知语种。</exception>
    public static string Resolve(string? input, IEnumerable<string>? projectCultures = null)
    {
        var normalized = Normalize(input)
            ?? throw new LinguaValidationException(
                $"语种代码 \"{input}\" 格式不合法，请使用 zh-CN、en-US 这样的 BCP47 写法。");

        var existing = projectCultures?.FirstOrDefault(c => string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        if (IsKnownCulture(normalized))
        {
            return normalized;
        }

        throw new LinguaValidationException(
            $"语种 \"{normalized}\" 不是有效的语种代码。请从语种下拉框中选择，或检查是否写错。");
    }
}
