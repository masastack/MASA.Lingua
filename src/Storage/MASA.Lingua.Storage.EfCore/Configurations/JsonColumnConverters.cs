using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MASA.Lingua.Storage.EfCore.Configurations;

/// <summary>
/// 术语库字典列的 JSON 转换器。
/// <para>
/// 为什么只有字典需要手写转换器：<c>List&lt;string&gt;</c> 由 EF Core 原生 primitive collection 映射，
/// <c>PublishPolicy</c> 由 owned JSON 映射，两者都不再需要转换器；而 EF 的 owned JSON 不支持
/// <see cref="Dictionary{TKey,TValue}"/>，所以术语的三个「语种 → 译法」字典仍走值转换 + jsonb 列。
/// </para>
/// </summary>
internal static class JsonColumnConverters
{
    public static readonly ValueConverter<Dictionary<string, string>, string> TargetTranslationsConverter = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => string.IsNullOrWhiteSpace(v)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null)
                    ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase));

    public static readonly ValueComparer<Dictionary<string, string>> TargetTranslationsComparer = new(
        (a, b) => a != null && b != null && a.Count == b.Count && !a.Except(b).Any(),
        d => d.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
        d => new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase));

    public static readonly ValueConverter<Dictionary<string, List<string>>, string> WordListsConverter = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => string.IsNullOrWhiteSpace(v)
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(
                JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (JsonSerializerOptions?)null)
                    ?? new Dictionary<string, List<string>>(),
                StringComparer.OrdinalIgnoreCase));

    public static readonly ValueComparer<Dictionary<string, List<string>>> WordListsComparer = new(
        (a, b) => WordListsEquals(a, b),
        d => d.Aggregate(0, (hash, kv) => HashCode.Combine(hash, kv.Key.GetHashCode(), kv.Value.Count)),
        d => d.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase));

    private static bool WordListsEquals(Dictionary<string, List<string>>? a, Dictionary<string, List<string>>? b)
    {
        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, list) in a)
        {
            if (!b.TryGetValue(key, out var other) || !list.SequenceEqual(other))
            {
                return false;
            }
        }

        return true;
    }
}
