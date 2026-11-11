using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Localization;

namespace MASA.Lingua.Core.Serialization;

/// <summary>
/// 扁平与嵌套 JSON 双向转换器：
/// 1. 支持将现有 Vue / .NET 项目中的任意深度嵌套 JSON 或扁平 JSON 解析为 (Namespace, Key, Text) 三元组；
/// 2. 支持将词条集合导出为嵌套树状 JSON（适配 Vue / 前端本地 JSON）或扁平 JSON（适配 .NET IStringLocalizer）。
/// </summary>
public static class JsonBundleConverter
{
    private static readonly JsonWriterOptions IndentedWriterOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 词条 key 的最大点分段数（命名空间段 + Key 段合计）。
    /// <para>
    /// 为什么要有上限：发布的嵌套 JSON 由 <see cref="ToNestedJsonUtf8Bytes"/> 按 key 的点分段逐级建树，
    /// <see cref="WriteDictionary"/> 递归写出 —— 分段数没有上限时，一条带几千个点的 key（导入扁平 JSON 时
    /// 完全可能：{"a.b.c.d...": "x"}）就会让发布线程栈溢出。栈溢出是 .NET 里无法捕获的进程级死亡
    ///（无异常、无日志），必须在数据入口拦住。正常 i18n 结构 3~4 段，20 段已是极端宽松。
    /// </para>
    /// </summary>
    public const int MaxKeySegments = 20;

    /// <summary>命名空间编码 + key 的合计点分段数超过 <see cref="MaxKeySegments"/> 时抛出，把爆栈风险拦在数据入口。</summary>
    public static void EnsureKeySegments(string namespaceCode, string key)
    {
        var count = CountSegments(namespaceCode) + CountSegments(key);
        if (count <= MaxKeySegments)
        {
            return;
        }

        var display = string.IsNullOrWhiteSpace(namespaceCode) ? key : $"{namespaceCode}.{key}";
        throw new LinguaValidationException(
            $"词条 Key 层级过深（{count} 段，上限 {MaxKeySegments}）：\"{display}\"。" +
            "请改用更浅的命名空间 / Key 结构（正常 i18n 结构不超过 4 段）。");
    }

    private static int CountSegments(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? 0
            : value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>
    /// 解析任意结构的现有项目 JSON 文件（自动识别嵌套对象或扁平键值）。
    /// 无命名空间的顶层扁平 key 归属「项目根」（空命名空间，发布时为 JSON 根级裸 key）。
    /// </summary>
    /// <param name="utf8Json">原始 JSON 字节</param>
    /// <param name="defaultNamespace">顶层扁平 key 的归属命名空间；空字符串表示项目根</param>
    public static IReadOnlyList<(string Namespace, string Key, string Value)> ParseExistingJson(
        ReadOnlySpan<byte> utf8Json,
        string defaultNamespace = "")
    {
        var results = new List<(string Namespace, string Key, string Value)>();
        using var doc = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var topProp in doc.RootElement.EnumerateObject())
        {
            if (topProp.Value.ValueKind == JsonValueKind.Object)
            {
                // 顶层属性为对象时，将顶层属性名作为 Namespace，递归展开内部路径
                var ns = NormalizeSegment(topProp.Name, defaultNamespace);
                FlattenElement(topProp.Value, prefix: string.Empty, ns, results);
            }
            else if (topProp.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                var rawKey = topProp.Name;
                var val = topProp.Value.ToString();

                // 检查扁平 Key 是否自带命名空间前缀（例如 "order:submit" 或 "order.submit"）
                var splitIndex = rawKey.IndexOfAny([':', '.']);
                if (splitIndex > 0 && splitIndex < rawKey.Length - 1)
                {
                    var ns = rawKey[..splitIndex];
                    var key = rawKey[(splitIndex + 1)..].Replace(':', '.');
                    EnsureKeySegments(ns, key);
                    results.Add((ns, key, val));
                }
                else
                {
                    EnsureKeySegments(defaultNamespace, rawKey);
                    results.Add((defaultNamespace, rawKey, val));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 将任意结构的 JSON（嵌套或扁平）展平为客户端内存查询字典，同时支持 "." 与 ":" 两种分隔符查找
    /// </summary>
    public static Dictionary<string, string> FlattenToLookupDictionary(ReadOnlySpan<byte> utf8Json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        // 顶层扁平 key 即项目根（发布产物里的根级裸 key），与导入语义一致
        var items = ParseExistingJson(utf8Json, "");

        foreach (var (ns, key, value) in items)
        {
            var dotKey = string.IsNullOrEmpty(ns) ? key : $"{ns}.{key}";
            var colonKey = string.IsNullOrEmpty(ns) ? key.Replace('.', ':') : $"{ns}:{key.Replace('.', ':')}";
            var mixedKey = string.IsNullOrEmpty(ns) ? key : $"{ns}:{key}";

            dict[dotKey] = value;
            dict[colonKey] = value;
            dict[mixedKey] = value;
        }

        return dict;
    }

    /// <summary>
    /// 将扁平的 (Namespace, Key, Value) 词条集合构建为嵌套树状 UTF-8 JSON 字节（与 Vue 项目的 locales/{culture}.json 完全一致）
    /// </summary>
    public static byte[] ToNestedJsonUtf8Bytes(IEnumerable<(string Namespace, string Key, string Value)> entries)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);

        foreach (var (ns, key, value) in entries)
        {
            var fullPath = string.IsNullOrWhiteSpace(ns) ? key : $"{ns}.{key.Replace(':', '.')}";
            var segments = fullPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            IDictionary<string, object> current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (!current.TryGetValue(seg, out var child) || child is not IDictionary<string, object> childDict)
                {
                    childDict = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    current[seg] = childDict;
                }
                current = childDict;
            }

            current[segments[^1]] = value;
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            WriteDictionary(writer, root);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 将词条集合构建为扁平键值 UTF-8 JSON 字节
    /// </summary>
    public static byte[] ToFlatJsonUtf8Bytes(
        IEnumerable<(string Namespace, string Key, string Value)> entries,
        char separator = '.')
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ns, key, value) in entries)
        {
            var normalizedKey = key.Replace(':', separator).Replace('.', separator);
            var fullKey = string.IsNullOrWhiteSpace(ns)
                ? normalizedKey
                : $"{ns}{separator}{normalizedKey}";
            sorted[fullKey] = value;
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            writer.WriteStartObject();
            foreach (var kv in sorted)
            {
                writer.WriteString(kv.Key, kv.Value);
            }
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void FlattenElement(
        JsonElement element,
        string prefix,
        string ns,
        List<(string Namespace, string Key, string Value)> output)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var currentKey = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenElement(prop.Value, currentKey, ns, output);
            }
            else if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                EnsureKeySegments(ns, currentKey);
                output.Add((ns, currentKey, prop.Value.ToString()));
            }
        }
    }

    private static void WriteDictionary(Utf8JsonWriter writer, IDictionary<string, object> dict)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in dict)
        {
            writer.WritePropertyName(k);
            if (v is IDictionary<string, object> childDict)
            {
                WriteDictionary(writer, childDict);
            }
            else
            {
                writer.WriteStringValue(v?.ToString() ?? string.Empty);
            }
        }
        writer.WriteEndObject();
    }

    private static string NormalizeSegment(string segment, string fallback) =>
        string.IsNullOrWhiteSpace(segment) ? fallback : segment.Trim();

    /// <summary>
    /// 发布 JSON 中的一处键路径冲突：同一个位置（<see cref="JsonPath"/>）既被词条当作「值」占用，
    /// 又被其他词条当作「父级对象」占用（或被两个不同词条同时当作值——完全重复的路径）。
    /// 嵌套 JSON 一个属性只能是字符串或对象之一：这类冲突发布时只会保留一侧，另一侧的文案会从发布文件中静默丢失。
    /// </summary>
    public sealed record KeyPathConflict(
        string JsonPath,
        IReadOnlyList<(string Namespace, string Key)> ValueClaims,
        IReadOnlyList<(string Namespace, string Key)> BranchClaims);

    /// <summary>
    /// 检测词条集合在嵌套发布 JSON 中的键路径冲突，判定规则与 <see cref="ToNestedJsonUtf8Bytes"/> 的建树逻辑完全一致：
    /// 根命名空间（空命名空间）的 key 直接成为 JSON 根级属性，非空命名空间成为一层对象；
    /// key 里允许出现点（导入的扁平 key），与命名空间段拼接后逐级建树。
    /// <para>
    /// 典型冲突：词条「Confirm」（项目根）与命名空间「Confirm」——发布 JSON 的 "Confirm" 位置
    /// 无法既是字符串又是对象。该检测让这种「静默丢内容」在发布门禁处显式暴露，而不是发布后悄悄消失。
    /// </para>
    /// <para>传入的词条必须先按发布口径合并（同 (Namespace, Key) 去重，如共享层被项目层覆盖），
    /// 检测结果与实际发布内容的冲突情况一致。同 (Namespace, Key) 的重复项不会被误报为冲突。</para>
    /// </summary>
    public static IReadOnlyList<KeyPathConflict> DetectPathConflicts(IEnumerable<(string Namespace, string Key)> entries)
    {
        var root = new ConflictNode();
        var seen = new HashSet<(string Namespace, string Key)>();
        foreach (var entry in entries)
        {
            // 同 (Namespace, Key) 只算一条（发布前已按图层合并去重；这里再兜一层，重复输入不会误报为冲突）
            if (!seen.Add(entry))
            {
                continue;
            }

            // 与 ToNestedJsonUtf8Bytes 相同的路径计算与分段规则（空命名空间不带点拼接）
            var fullPath = string.IsNullOrWhiteSpace(entry.Namespace)
                ? entry.Key
                : $"{entry.Namespace}.{entry.Key.Replace(':', '.')}";
            var segments = fullPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var current = root;
            foreach (var segment in segments[..^1])
            {
                current = current.Descend(segment);
                current.ParentClaims.Add(entry);
            }

            current.Descend(segments[^1]).ValueClaims.Add(entry);
        }

        var conflicts = new List<KeyPathConflict>();
        CollectConflicts(root, "", conflicts);
        return conflicts;
    }

    private sealed class ConflictNode
    {
        public Dictionary<string, ConflictNode> Children { get; } = new(StringComparer.Ordinal);

        /// <summary>把这个位置当作「值」的词条（路径终点落在这里）。</summary>
        public List<(string Namespace, string Key)> ValueClaims { get; } = [];

        /// <summary>路径经过这里、要求这个位置是「父级对象」的词条。</summary>
        public List<(string Namespace, string Key)> ParentClaims { get; } = [];

        public ConflictNode Descend(string segment)
        {
            if (!Children.TryGetValue(segment, out var child))
            {
                Children[segment] = child = new ConflictNode();
            }

            return child;
        }
    }

    private static void CollectConflicts(ConflictNode node, string path, List<KeyPathConflict> conflicts)
    {
        foreach (var (segment, child) in node.Children)
        {
            var childPath = path.Length == 0 ? segment : $"{path}.{segment}";
            // 冲突判定：同一位置有两个以上的「值」占用（完全重复路径），或既是「值」又是「父级对象」
            if (child.ValueClaims.Count > 1 ||
                (child.ValueClaims.Count > 0 && child.ParentClaims.Count > 0))
            {
                conflicts.Add(new KeyPathConflict(childPath, child.ValueClaims, child.ParentClaims));
            }

            CollectConflicts(child, childPath, conflicts);
        }
    }
}
