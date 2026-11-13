using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;

namespace MASA.Lingua.Core.Compilation;

/// <summary>
/// 发布用译文图层的构建（原为仓储私有逻辑，上移到 Core）：
/// 按回退链把共享命名空间与项目私有命名空间叠成图层链，交给 <see cref="BundleCompiler"/> 合并编译。
/// 抽成纯函数是为了让发布器在同一次数据加载里复用（草稿 diff 与正式发布必须走同一口径），仓储只负责取数。
/// </summary>
public static class TranslationLayerBuilder
{
    /// <summary>
    /// 构建指定项目与目标语种的合并图层（SharedFallback -> SharedTarget -> ProjectFallback -> ProjectTarget）。
    /// <paramref name="sharedNamespaces"/> 传全部共享命名空间即可，内部按项目的 SharedNamespaceCodes 过滤；
    /// <paramref name="contentMode"/> 非 null 时按状态过滤「会上线」的译文，其余回退到默认语种（null = 草稿视图，用于导出与对比）。
    /// </summary>
    public static IReadOnlyList<TranslationLayer> Build(
        Project project,
        IReadOnlyList<TranslationNamespace> sharedNamespaces,
        string targetCulture,
        PublishContentMode? contentMode = null)
    {
        var shared = sharedNamespaces
            .Where(n => project.SharedNamespaceCodes.Contains(n.Code, LinguaKeys.Comparer))
            .ToList();

        var fallbackChain = BundleCompiler.BuildFallbackChain(targetCulture, project.DefaultCulture);
        var layers = new List<TranslationLayer>();

        // 1. 先按回退链叠加公共共享命名空间 (Shared Fallback -> Shared Target)
        foreach (var culture in fallbackChain)
        {
            layers.Add(new TranslationLayer($"Shared:{culture}", ExtractLayerItems(shared, culture, project.DefaultCulture, contentMode)));
        }

        // 2. 再按回退链叠加项目私有命名空间，支持同名 Key 自动 Override 公共词条
        foreach (var culture in fallbackChain)
        {
            layers.Add(new TranslationLayer($"Project:{culture}", ExtractLayerItems(project.Namespaces, culture, project.DefaultCulture, contentMode)));
        }

        return layers;
    }

    private static List<TranslationItem> ExtractLayerItems(
        IEnumerable<TranslationNamespace> namespaces,
        string culture,
        string defaultCulture,
        PublishContentMode? contentMode = null)
    {
        var list = new List<TranslationItem>();
        bool isDefault = string.Equals(culture, defaultCulture, StringComparison.OrdinalIgnoreCase);
        var filter = contentMode is null ? null : new PublishPolicy { Content = contentMode.Value };

        foreach (var ns in namespaces)
        {
            foreach (var entry in ns.Entries)
            {
                var tr = entry.Translations.FirstOrDefault(
                    t => string.Equals(t.Culture, culture, StringComparison.OrdinalIgnoreCase));

                // 发布视图：默认语种始终发布；其他语种只发布状态达标的译文，其余回退到默认语种文案
                if (tr is not null && filter is not null && !isDefault && !filter.Includes(tr.Status))
                {
                    continue;
                }

                if (tr is not null && !string.IsNullOrWhiteSpace(tr.Text))
                {
                    list.Add(new TranslationItem(ns.Code, entry.Key, tr.Text));
                }
                else if (isDefault)
                {
                    // 源文案未知（只有非默认语种译文的词条）时回退到 key，而不是发空字符串
                    var fallback = string.IsNullOrWhiteSpace(entry.SourceText) ? entry.Key : entry.SourceText;
                    list.Add(new TranslationItem(ns.Code, entry.Key, fallback));
                }
            }
        }

        return list;
    }
}
