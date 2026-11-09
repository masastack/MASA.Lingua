namespace MASA.Lingua.Core.Domain;

/// <summary>
/// 词条译文的读取口径（「某个语种显示什么文本」）。矩阵单元格渲染、单格编辑的「有没有改动」判断、规则校验的输入
/// 都必须用同一份实现：三处一旦口径不同，就会出现「矩阵上显示有译文、校验却认为缺失」这类无从解释的不一致。
/// </summary>
public static class EntryTranslations
{
    /// <summary>该语种是否为项目源语种。</summary>
    public static bool IsSource(string culture, string defaultCulture) =>
        string.Equals(culture, defaultCulture, StringComparison.OrdinalIgnoreCase);

    /// <summary>取该语种的译文行；没有则返回 null。</summary>
    public static TranslationValue? Find(TranslationEntry entry, string culture)
    {
        // 译文列表每词条只有几项（语种数）：线性查找足够，也避免为每行建字典
        foreach (var value in entry.Translations)
        {
            if (string.Equals(value.Culture, culture, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>该语种要显示 / 参与校验的文本：有译文行用译文；源语种没有译文行时回退到源文案；其余为空串。</summary>
    public static string Text(TranslationEntry entry, string culture, string defaultCulture) =>
        Find(entry, culture) is { } value
            ? value.Text
            : IsSource(culture, defaultCulture) ? entry.SourceText : string.Empty;
}
