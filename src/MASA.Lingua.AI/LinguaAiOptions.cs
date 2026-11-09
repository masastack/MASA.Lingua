namespace MASA.Lingua.AI;

public sealed class LinguaAiOptions
{
    public const string SectionName = "Lingua:AI";

    /// <summary>单次请求默认最多携带的词条数。</summary>
    public const int DefaultBatchSize = 20;

    /// <summary>
    /// OpenAI 兼容（Chat Completions）服务的 Endpoint，例如 https://api.openai.com/v1、https://api.deepseek.com/v1，
    /// 或本地 Ollama：http://localhost:11434/v1
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// 模型名称（如 gpt-4o-mini、deepseek-chat、qwen-plus；本地服务以其 /v1/models 里列出的名字为准）。
    /// 名字对不上时服务会返回 404，界面会直接显示该错误。
    /// </summary>
    public string ModelId { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// 单次请求最多携带的词条数（每个目标语种按此分块请求）。本地大模型较慢时调小，可避免单次请求超时。
    /// </summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>默认同时在途的模型请求数。</summary>
    public const int DefaultMaxConcurrency = 2;

    /// <summary>
    /// 同时在途的模型请求数上限（整个站点共享，含多个管理员同时操作）。批量补全按「语种 × 块」拆成多个请求，
    /// 有限并发可以把耗时压到接近 1/N；设为 1 即完全串行。
    /// 本地单卡模型（如默认并行度为 1 的 Ollama）会把并发请求排队、排队时间也算进请求超时：此时请保持 1，或同时调小 BatchSize。
    /// 云端 API 遇到 429（限流）时请调小。
    /// </summary>
    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;

    /// <summary>
    /// 单次模型请求的网络超时（秒）。显式配置以覆盖 SDK 的默认 100 秒：模型端「黑洞」（连接挂起不响应）时，
    /// 没有超时管理员就要干等 100 秒才看到失败；本地大模型批量翻译又可能很慢，因此默认放宽到 180 秒，可按部署调。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 180;
}
