using System.Text.Json;

namespace MASA.Lingua.Core.Serialization;

/// <summary>
/// 平台对外协议（manifest / bundle / SSE 事件）的统一序列化选项：
/// Web 默认命名策略（camelCase、属性名大小写不敏感）。服务端端点、SDK 与测试共用同一份实例，
/// 避免同一份协议在不同位置各配一套选项。
/// </summary>
public static class LinguaJson
{
    /// <summary>协议 JSON 选项（只读共享，首次使用后由 System.Text.Json 冻结）。</summary>
    public static JsonSerializerOptions Protocol { get; } = new(JsonSerializerDefaults.Web);
}
