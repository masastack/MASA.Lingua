using Masa.BuildingBlocks.Globalization.I18n;
using MASA.Lingua.SDK.Abstractions;

namespace MASA.Lingua.SDK.MasaI18n;

/// <summary>
/// MASA Framework I18n 资源贡献者：把 Lingua 的热更新内存字典（FrozenDictionary）接入 MASA 的
/// <c>II18n</c> / <c>I18n.T()</c> 查询链路。
///
/// MASA 的 <c>I18n&lt;T&gt;</c> 每次取词都会实时调用 contributor（无结果缓存），因此
/// Lingua 发布同步（轮询 / SSE）更新内存字典后，下一笔请求立即读到新文案，无需重启。
///
/// 取词顺序：
/// 1. Lingua 内存 store（含语种回退链：目标语种 → 中性语种 zh → 默认语种，回退链与默认语种由平台 manifest 决定）；
/// 2. （可选）注册 <see cref="AddLinguaMasaI18n"/> 时扫描到的本地 {culture}.json 兜底 —— Lingua 服务不可达
///    且内存无此 key 时仍能翻译，保证离线可启动。
/// </summary>
public sealed class LinguaI18nResourceContributor : II18nResourceContributor
{
    private readonly ILinguaMemoryStore _store;
    private readonly II18nResourceContributor? _localFallback;

    public string CultureName { get; }

    public LinguaI18nResourceContributor(
        string cultureName,
        ILinguaMemoryStore store,
        II18nResourceContributor? localFallback = null)
    {
        CultureName = cultureName;
        _store = store;
        _localFallback = localFallback;
    }

    public string? GetOrNull(string name)
    {
        // store 内部已做 ':' 与 '.' 分隔符归一，以及 common 命名空间短 Key 兜底，
        // MASA 现有的扁平 Key（如 "RoleNotExist"）可以直接命中。
        if (_store.TryGetValue(CultureName, name, out var value))
        {
            return value;
        }

        return _localFallback?.GetOrNull(name);
    }
}
