using Microsoft.Extensions.AI;

namespace MASA.Lingua.Tests;

/// <summary>
/// 脚本化的假 <see cref="IChatClient"/>：按调用顺序依次执行预设步骤（超出后重复最后一步）。
/// 步骤是 string 就当作模型回复；是 Exception 就在该次调用时失败（模拟服务不可用、模型不存在、超时等）。
/// Prompts 记录每次调用收到的提示词，用来断言术语注入、分块与修复重试。
/// </summary>
internal sealed class ScriptedChatClient(params object[] script) : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
        var step = script[Math.Min(Prompts.Count - 1, script.Length - 1)];

        return step is Exception ex
            ? Task.FromException<ChatResponse>(ex)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, (string)step)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
