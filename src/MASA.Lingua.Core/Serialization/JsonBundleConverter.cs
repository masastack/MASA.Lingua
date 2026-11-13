using System.Text;
using System.Text.Json;
using MASA.Lingua.Core.Domain;
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
                var rawKey = topProp.Name.Trim();
                var val = topProp.Value.ToString();

                // 检查扁平 Key 是否自带命名空间前缀（例如 "order:submit" 或 "order.submit"）
                var splitIndex = rawKey.IndexOfAny([':', '.']);
                if (splitIndex > 0 && splitIndex < rawKey.Length - 1)
                {
                    var ns = rawKey[..splitIndex].Trim();
                    var key = NormalizeKeyPath(rawKey[(splitIndex + 1)..].Replace(':', '.'));
                    AddParsed(results, ns, key, val);
                }
                else
                {
                    AddParsed(results, defaultNamespace, NormalizeKeyPath(rawKey), val);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 把词条的 (命名空间, Key) 归一化为发布 JSON 里的路径段序列 —— 嵌套 / 扁平两种发布格式与键路径冲突检测的唯一口径。
    /// <para>
    /// 规则：命名空间非空时在 key 前拼上命名空间；":" 与 "." 等价（都是层级分隔符）；
    /// 逐段去掉首尾空白，丢弃空段。三处（嵌套建树 / 扁平拼 key / 冲突检测）必须共用这一个实现：
    /// 它们各自取不同的规则时，就会出现「嵌套里是 A、扁平里是 B」的静默分叉（例如命名空间尾部带空格、
    /// 根 key 含冒号），而客户端实际消费的恰恰是扁平表示。
    /// </para>
    /// </summary>
    public static string[] ToPathSegments(string? namespaceCode, string? key)
    {
        var full = string.IsNullOrWhiteSpace(namespaceCode) ? key ?? string.Empty : $"{namespaceCode}.{key}";
        return full.Replace(':', '.').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void AddParsed(
        List<(string Namespace, string Key, string Value)> results,
        string ns,
        string key,
        string value)
    {
        // 空 key 没有可发布的路径（嵌套 / 扁平发布时本就会被丢弃），不产出无意义的词条
        if (key.Length == 0)
        {
            return;
        }

        EnsureKeySegments(ns, key);
        results.Add((ns, key, value));
    }

    /// <summary>key 内的点分段去掉首尾空白与空段（保留 ":" 原样，是否归一由发布路径口径 <see cref="ToPathSegments"/> 决定）。</summary>
    private static string NormalizeKeyPath(string key) =>
        string.Join('.', key.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// 将任意结构的 JSON（嵌套或扁平）展平为客户端内存查询字典，同时支持 "." 与 ":" 两种分隔符查找。
    /// 键忽略大小写（<see cref="LinguaKeys.Comparer"/>）：与服务端「命名空间 / Key 忽略大小写唯一」的口径一致，
    /// 取词时 <c>Order.Submit</c> 与 <c>order.submit</c> 命中同一条。
    /// </summary>
    public static Dictionary<string, string> FlattenToLookupDictionary(ReadOnlySpan<byte> utf8Json)
    {
        var dict = new Dictionary<string, string>(LinguaKeys.Comparer);
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
        // 建树时按忽略大小写合并同一位置（与「命名空间 / Key 忽略大小写唯一」一致），节点保留首次出现的写法；
        // 输出时按该写法的序号顺序排列，所以没有仅大小写不同的路径时，字节与过去完全相同（ETag 不变）。
        var root = new TreeNode(string.Empty) { Children = new Dictionary<string, TreeNode>(LinguaKeys.Comparer) };

        foreach (var (ns, key, value) in entries)
        {
            var segments = ToPathSegments(ns, key);
            if (segments.Length == 0)
            {
                continue;
            }

            var current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                current.Children!.TryGetValue(seg, out var child);
                if (child?.Children is null)
                {
                    // 此位置不存在，或此前被当作「值」占用：后写入的父级对象取代它（键路径冲突，由发布门禁提示）
                    child = new TreeNode(child?.Name ?? seg) { Children = new Dictionary<string, TreeNode>(LinguaKeys.Comparer) };
                    current.Children[seg] = child;
                }

                current = child;
            }

            var last = segments[^1];
            current.Children!.TryGetValue(last, out var existing);
            current.Children[last] = new TreeNode(existing?.Name ?? last) { Value = value };
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            WriteNode(writer, root);
        }
        return ms.ToArray();
    }

    /// <summary>嵌套发布 JSON 的建树节点：<see cref="Children"/> 非空表示对象，否则 <see cref="Value"/> 是字符串叶子。</summary>
    private sealed class TreeNode(string name)
    {
        public string Name { get; } = name;

        public string? Value { get; init; }

        public Dictionary<string, TreeNode>? Children { get; init; }
    }

    /// <summary>
    /// 将词条集合构建为扁平键值 UTF-8 JSON 字节
    /// </summary>
    public static byte[] ToFlatJsonUtf8Bytes(
        IEnumerable<(string Namespace, string Key, string Value)> entries,
        char separator = '.')
    {
        // 同一路径按忽略大小写合并（后写入的值覆盖前者，写法保留首次出现的那个），与嵌套建树的口径一致
        var merged = new Dictionary<string, (string Name, string Value)>(LinguaKeys.Comparer);
        foreach (var (ns, key, value) in entries)
        {
            // 与嵌套建树共用同一路径口径：两种发布表示对同一词条给出同一条路径，只是呈现方式不同
            var segments = ToPathSegments(ns, key);
            if (segments.Length == 0)
            {
                continue;
            }

            var path = string.Join(separator, segments);
            merged[path] = (merged.TryGetValue(path, out var existing) ? existing.Name : path, value);
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedWriterOptions))
        {
            writer.WriteStartObject();
            foreach (var item in merged.Values.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                writer.WriteString(item.Name, item.Value);
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
                AddParsed(output, ns, NormalizeKeyPath(currentKey), prop.Value.ToString());
            }
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, TreeNode node)
    {
        writer.WriteStartObject();
        foreach (var child in node.Children!.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(child.Name);
            if (child.Children is not null)
            {
                WriteNode(writer, child);
            }
            else
            {
                writer.WriteStringValue(child.Value ?? string.Empty);
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
    /// <para>路径各段忽略大小写比较（<see cref="LinguaKeys.Comparer"/>）：<c>Confirm.Title</c> 与 <c>confirm.title</c> 占用同一个位置。
    /// 同一个 (Namespace, Key)（忽略大小写，见 <see cref="LinguaKeys.EntryComparer"/>）就是同一个词条，只算一条；
    /// 写法不同但路径相同的<b>不同</b>词条（如根 Key <c>A.b</c> 与命名空间 <c>a</c> 下的 Key <c>B</c>）才是完全重复的路径冲突。</para>
    /// <para>传入的词条必须先按发布口径合并（同 (Namespace, Key) 去重，如共享层被项目层覆盖），
    /// 检测结果与实际发布内容的冲突情况一致。完全相同的 (Namespace, Key) 重复项不会被误报为冲突。</para>
    /// </summary>
    public static IReadOnlyList<KeyPathConflict> DetectPathConflicts(IEnumerable<(string Namespace, string Key)> entries)
    {
        var root = new ConflictNode();
        var seen = new HashSet<(string Namespace, string Key)>(LinguaKeys.EntryComparer);
        foreach (var entry in entries)
        {
            // 同 (Namespace, Key) 只算一条（发布前已按图层合并去重；这里再兜一层，重复输入不会误报为冲突）
            if (!seen.Add(entry))
            {
                continue;
            }

            // 与 ToNestedJsonUtf8Bytes / ToFlatJsonUtf8Bytes 共用同一路径计算与分段规则
            var segments = ToPathSegments(entry.Namespace, entry.Key);
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
        // 忽略大小写：仅大小写不同的路径是同一个位置（被两个词条占用即重复）
        public Dictionary<string, ConflictNode> Children { get; } = new(LinguaKeys.Comparer);

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
