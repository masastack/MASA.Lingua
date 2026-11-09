using System.Reflection;
using System.Text.RegularExpressions;

namespace MASA.Lingua.Core.Formatting;

/// <summary>
/// 跨平台占位符插值格式化器：
/// 同时支持位置占位符 ("Hello {0}") 与命名占位符 ("Hello {userName}, you have {count} items")
/// </summary>
public static partial class LinguaInterpolation
{
    /// <summary>
    /// 占位符词法必须与 <c>DeterministicTranslationValidator.ExtractPlaceholders</c> 完全一致：
    /// 允许数字开头，这样 <c>{0}</c>（位置参数）与 <c>{name}</c>（命名参数）都算合法占位符。
    /// 两边不一致会造成「校验通过但运行时插值失败」。
    /// </summary>
    [GeneratedRegex(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    public static string Format(string template, params object?[] arguments)
    {
        if (string.IsNullOrEmpty(template) || arguments is null || arguments.Length == 0)
        {
            return template;
        }

        // 如果第一个参数是匿名对象或字典，且模板包含命名占位符（如 {userName}）
        if (arguments.Length == 1 && arguments[0] is not null && arguments[0] is not string && ! arguments[0]!.GetType().IsPrimitive)
        {
            var arg = arguments[0]!;
            if (arg is IReadOnlyDictionary<string, object?> roDict)
            {
                return PlaceholderRegex().Replace(template, m =>
                {
                    var key = m.Groups[1].Value;
                    return roDict.TryGetValue(key, out var val) ? val?.ToString() ?? string.Empty : m.Value;
                });
            }

            if (arg is IDictionary<string, string> strDict)
            {
                return PlaceholderRegex().Replace(template, m =>
                {
                    var key = m.Groups[1].Value;
                    return strDict.TryGetValue(key, out var val) ? val : m.Value;
                });
            }

            if (PlaceholderRegex().IsMatch(template))
            {
                var props = arg.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .ToDictionary(p => p.Name, p => p.GetValue(arg), StringComparer.OrdinalIgnoreCase);

                var replaced = PlaceholderRegex().Replace(template, m =>
                {
                    var key = m.Groups[1].Value;
                    return props.TryGetValue(key, out var val) ? val?.ToString() ?? string.Empty : m.Value;
                });

                return replaced;
            }
        }

        // 如果模板包含命名占位符但传入了位置参数数组，按命名占位符出现顺序依次替换
        if (PlaceholderRegex().IsMatch(template))
        {
            var orderedNames = new List<string>();
            foreach (Match m in PlaceholderRegex().Matches(template))
            {
                var name = m.Groups[1].Value;
                if (!orderedNames.Contains(name, StringComparer.Ordinal))
                {
                    orderedNames.Add(name);
                }
            }

            return PlaceholderRegex().Replace(template, m =>
            {
                var name = m.Groups[1].Value;

                // 纯数字 token 是位置参数：按数字取值，不能按出现顺序（"{1} {0}" 顺序取值会串位）
                var idx = int.TryParse(name, out var position) && position >= 0
                    ? position
                    : orderedNames.IndexOf(name);

                return idx >= 0 && idx < arguments.Length
                    ? arguments[idx]?.ToString() ?? string.Empty
                    : m.Value;
            });
        }

        try
        {
            return string.Format(template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
