using System.Globalization;
using MASA.Lingua.SDK.Abstractions;
using MASA.Lingua.Core.Formatting;
using Microsoft.Extensions.Localization;

namespace MASA.Lingua.SDK.AspNetCore;

/// <summary>
/// ASP.NET Core 与 Blazor Server 标准 IStringLocalizer 薄适配器：
/// 业务代码保持 100% 原生写法（@inject IStringLocalizer L -> L["order.submit"] 或 L["common:welcomeUser", new { userName = "Alice" }]），
/// 底层从 {culture}.json 构建的 FrozenDictionary 无锁读取。
/// </summary>
public sealed class LinguaStringLocalizer(
    ILinguaMemoryStore store,
    IMissingKeyReporter? reporter = null,
    string? namespacePrefix = null) : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            var fullKey = BuildFullKey(name);
            var culture = CultureInfo.CurrentUICulture.Name;
            if (store.TryGetValue(culture, fullKey, out var value))
            {
                return new LocalizedString(name, value, resourceNotFound: false);
            }

            reporter?.ReportMissingKey(culture, fullKey);
            return new LocalizedString(name, fullKey, resourceNotFound: true);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var localized = this[name];
            var formatted = LinguaInterpolation.Format(localized.Value, arguments);
            return new LocalizedString(name, formatted, localized.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        store.GetAll(CultureInfo.CurrentUICulture.Name, namespacePrefix)
             .Select(kv => new LocalizedString(kv.Key, kv.Value, resourceNotFound: false));

    private string BuildFullKey(string name)
    {
        if (string.IsNullOrWhiteSpace(namespacePrefix) || name.Contains('.') || name.Contains(':'))
        {
            return name;
        }
        return $"{namespacePrefix}.{name}";
    }
}

public sealed class LinguaStringLocalizer<TResourceName>(
    ILinguaMemoryStore store,
    IMissingKeyReporter? reporter = null) : IStringLocalizer<TResourceName>
{
    private readonly LinguaStringLocalizer _inner = new(
        store,
        reporter,
        ExtractNamespacePrefix(typeof(TResourceName)));

    public LocalizedString this[string name] => _inner[name];
    public LocalizedString this[string name, params object[] arguments] => _inner[name, arguments];
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => _inner.GetAllStrings(includeParentCultures);

    private static string? ExtractNamespacePrefix(Type resourceType)
    {
        var typeName = resourceType.Name;
        if (typeName.EndsWith("Controller", StringComparison.Ordinal))
        {
            typeName = typeName[..^"Controller".Length];
        }
        else if (typeName.EndsWith("Page", StringComparison.Ordinal))
        {
            typeName = typeName[..^"Page".Length];
        }
        return typeName.ToLowerInvariant();
    }
}

public sealed class LinguaStringLocalizerFactory(
    ILinguaMemoryStore store,
    IMissingKeyReporter? reporter = null) : IStringLocalizerFactory
{
    public IStringLocalizer Create(Type resourceSource) =>
        new LinguaStringLocalizer(store, reporter, resourceSource.Name.ToLowerInvariant());

    public IStringLocalizer Create(string baseName, string location) =>
        new LinguaStringLocalizer(store, reporter, baseName);
}
