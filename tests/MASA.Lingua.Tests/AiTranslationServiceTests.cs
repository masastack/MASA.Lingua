using MASA.Lingua.AI;
using MASA.Lingua.AI.Services;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Domain;
using MASA.Lingua.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MASA.Lingua.Tests;

/// <summary>
/// AI 翻译服务的行为约定：分块请求、失败一律可见（抛 AiTranslationException）、绝不用本地内容兜底。
/// </summary>
public sealed class AiTranslationServiceTests
{
    /// <summary>
    /// 默认串行（maxConcurrency = 1）：下面按「第 N 次请求拿第 N 条脚本回复」写的用例依赖确定的请求顺序；
    /// 并发行为由专门的用例（<c>Concurrent_*</c>）单独验证。
    /// </summary>
    private static AiTranslationService Create(
        IChatClient client,
        int batchSize = 20,
        string model = "test-model",
        string endpoint = "http://10.0.0.5:11434/v1",
        int maxConcurrency = 1) =>
        new(
            new DeterministicTranslationValidator(),
            NullLogger<AiTranslationService>.Instance,
            client,
            Options.Create(new LinguaAiOptions
            {
                ModelId = model,
                Endpoint = endpoint,
                BatchSize = batchSize,
                MaxConcurrency = maxConcurrency
            }));

    private static AiTranslationRequestItem Item(string key, string source) =>
        new(Guid.NewGuid(), "ns", key, source, null, null);

    /// <summary>构造一份「id → 译文」的模型回复。</summary>
    private static string Reply(IEnumerable<AiTranslationRequestItem> items, string text = "T") =>
        "{" + string.Join(",", items.Select(i => $"\"{i.EntryId}\":\"{text}\"")) + "}";

    [Fact]
    public async Task Empty_Input_Should_Return_Empty_Without_Calling_The_Model()
    {
        var client = new ScriptedChatClient("{}");
        var service = Create(client);

        Assert.Empty(await service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [], []));
        Assert.Empty(client.Prompts);
    }

    [Fact]
    public async Task Request_Failure_Should_Throw_With_Model_Endpoint_And_Reason_And_Not_Retry_Or_Fall_Back()
    {
        var client = new ScriptedChatClient(new HttpRequestException("model 'gemma4:31b-mlx' not found"));
        var service = Create(client, model: "gemma4:31b-mlx", endpoint: "http://10.0.0.5:11434/v1");

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US", "ja-JP"], [Item("save", "保存")], []));

        // 一眼能看出是哪套配置、什么原因
        Assert.Contains("gemma4:31b-mlx @ 10.0.0.5:11434", ex.Message);
        Assert.Contains("not found", ex.Message);
        Assert.Empty(ex.PartialResults);

        // 失败后不重试、不继续后面的语种
        Assert.Single(client.Prompts);
    }

    [Fact]
    public async Task Timeout_Should_Be_Reported_As_Timeout_With_BatchSize_Hint()
    {
        var client = new ScriptedChatClient(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var service = Create(client);

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [Item("save", "保存")], []));

        Assert.Contains("超时", ex.Message);
        Assert.Contains("Lingua:AI:BatchSize", ex.Message);
    }

    [Fact]
    public async Task Retry_Exhausted_Failure_Should_Name_The_Reason_Once_Instead_Of_Once_Per_Attempt()
    {
        // OpenAI SDK 重试耗尽后抛 AggregateException，其 Message 会把每次失败的原因重复拼接一遍
        var attempts = Enumerable.Range(0, 4)
            .Select(_ => new HttpRequestException("由于目标计算机积极拒绝，无法连接。"))
            .ToArray();
        var client = new ScriptedChatClient(new AggregateException("Retry failed after 4 tries.", attempts));
        var service = Create(client, model: "qwen3.8:27b-mlx", endpoint: "http://10.0.0.5:11434/v1");

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [Item("save", "保存")], []));

        Assert.Contains("qwen3.8:27b-mlx @ 10.0.0.5:11434", ex.Message);
        Assert.Contains("连续尝试 4 次均失败", ex.Message);
        Assert.Equal(2, ex.Message.Split("积极拒绝").Length); // 原因只出现一次
        Assert.Empty(ex.PartialResults);
    }

    [Fact]
    public async Task Items_Should_Be_Chunked_By_BatchSize_And_Each_Request_Only_Carries_Its_Own_Items()
    {
        var items = Enumerable.Range(0, 5).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ScriptedChatClient(
            Reply(items.Take(2)),
            Reply(items.Skip(2).Take(2)),
            Reply(items.Skip(4)));
        var service = Create(client, batchSize: 2);

        var results = await service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, []);

        Assert.Equal(items.Select(i => i.EntryId), results.Select(r => r.EntryId));
        Assert.Equal(3, client.Prompts.Count);

        var chunks = new[] { items[..2], items[2..4], items[4..] };
        for (var c = 0; c < chunks.Length; c++)
        {
            foreach (var item in items)
            {
                var inThisChunk = chunks[c].Contains(item);
                Assert.Equal(inThisChunk, client.Prompts[c].Contains(item.EntryId.ToString()));
            }
        }
    }

    [Fact]
    public async Task Chunking_Should_Apply_Per_Target_Culture()
    {
        var items = Enumerable.Range(0, 3).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ScriptedChatClient(
            Reply(items.Take(2), "EN"), Reply(items.Skip(2), "EN"),
            Reply(items.Take(2), "JA"), Reply(items.Skip(2), "JA"));
        var service = Create(client, batchSize: 2);

        var results = await service.TranslateBatchAsync("p", "zh-CN", ["en-US", "ja-JP"], items, []);

        Assert.Equal(4, client.Prompts.Count);
        Assert.Equal(3, results.Count(r => r.TargetCulture == "en-US" && r.TranslatedText == "EN"));
        Assert.Equal(3, results.Count(r => r.TargetCulture == "ja-JP" && r.TranslatedText == "JA"));
    }

    [Fact]
    public async Task Later_Chunk_Failure_Should_Keep_Earlier_Chunks_As_PartialResults_And_Stop()
    {
        var items = Enumerable.Range(0, 4).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ScriptedChatClient(Reply(items.Take(2)), new HttpRequestException("503 service busy"));
        var service = Create(client, batchSize: 2);

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, []));

        Assert.Contains("503 service busy", ex.Message);
        Assert.Equal(items.Take(2).Select(i => i.EntryId), ex.PartialResults.Select(r => r.EntryId));
        Assert.Equal(2, client.Prompts.Count);
    }

    // ------------------------------------------------------------------
    // 有限并发（Lingua:AI:MaxConcurrency）
    // ------------------------------------------------------------------

    /// <summary>可控的聊天客户端：每次请求登记一个待决任务，测试决定何时完成 / 失败；请求的取消令牌被取消时任务随之取消。</summary>
    private sealed class ControlledChatClient : IChatClient
    {
        public sealed record Call(string Prompt, TaskCompletionSource<string> Reply, CancellationToken Token);

        private readonly List<Call> _calls = [];
        private int _inFlight;
        private int _peak;

        public int PeakInFlight => Volatile.Read(ref _peak);

        public IReadOnlyList<Call> Calls
        {
            get { lock (_calls) { return [.. _calls]; } }
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_calls)
            {
                _calls.Add(new Call(string.Join("\n", messages.Select(m => m.Text)), tcs, cancellationToken));
            }

            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }

            using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            try
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, await tcs.Task));
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待条件超时");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Concurrent_Chunks_Should_Be_Limited_To_MaxConcurrency_And_Results_Keep_Declared_Order()
    {
        var items = Enumerable.Range(0, 12).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ControlledChatClient();
        var service = Create(client, batchSize: 2, maxConcurrency: 3); // 6 块，最多 3 个在途

        var run = service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, []);

        await WaitUntilAsync(() => client.Calls.Count == 3);
        await Task.Delay(50); // 给「第 4 个请求被错误地放行」留出发生的时间
        Assert.Equal(3, client.Calls.Count);

        // 按请求到达的顺序逐个完成（新名额放行后才会出现后面的请求）；不论完成先后，汇总都必须按声明顺序
        var served = 0;
        while (served < 6)
        {
            await WaitUntilAsync(() => client.Calls.Count > served);
            var call = client.Calls[served];
            var ids = System.Text.RegularExpressions.Regex.Matches(call.Prompt, "[0-9a-fA-F]{8}-[0-9a-fA-F-]{27}")
                .Select(m => m.Value).Distinct();
            call.Reply.TrySetResult("{" + string.Join(",", ids.Select(id => $"\"{id}\":\"T\"")) + "}");
            served++;
        }

        var results = await run;
        Assert.Equal(items.Select(i => i.EntryId), results.Select(r => r.EntryId));
        Assert.Equal(3, client.PeakInFlight);
    }

    [Fact]
    public async Task Concurrent_Failure_Should_Cancel_InFlight_Stop_Launching_And_Keep_Completed_Chunks()
    {
        var items = Enumerable.Range(0, 4).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ControlledChatClient();
        var service = Create(client, batchSize: 1, maxConcurrency: 2); // 4 块，同时 2 个在途

        var run = service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, []);

        await WaitUntilAsync(() => client.Calls.Count == 2);

        // 第 0 块成功 → 空出一个名额，第 2 块开始请求
        client.Calls[0].Reply.TrySetResult($"{{\"{items[0].EntryId}\":\"T0\"}}");
        await WaitUntilAsync(() => client.Calls.Count == 3);

        // 第 1 块失败：第 2 块（在途）被取消，第 3 块不会再发起
        client.Calls[1].Reply.TrySetException(new HttpRequestException("503 service busy"));

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() => run);
        Assert.Contains("503 service busy", ex.Message);
        Assert.Equal([items[0].EntryId], ex.PartialResults.Select(r => r.EntryId));
        Assert.Equal(3, client.Calls.Count);
        Assert.True(client.Calls[2].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_Cancellation_Should_Surface_As_OperationCanceled_Not_As_Ai_Failure()
    {
        var items = Enumerable.Range(0, 4).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ControlledChatClient();
        var service = Create(client, batchSize: 1, maxConcurrency: 2);
        using var cts = new CancellationTokenSource();

        var run = service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, [], cts.Token);
        await WaitUntilAsync(() => client.Calls.Count == 2);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, client.Calls.Count); // 取消后不再发起新请求
    }

    [Fact]
    public async Task Items_Missing_From_Reply_Should_Be_Reported_And_Never_Padded()
    {
        var items = Enumerable.Range(0, 3).Select(i => Item($"k{i}", $"文案{i}")).ToArray();
        var client = new ScriptedChatClient(Reply(items.Take(2)));
        var service = Create(client);

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US"], items, []));

        Assert.Contains("漏返了 1 条", ex.Message);
        Assert.Equal(items.Take(2).Select(i => i.EntryId), ex.PartialResults.Select(r => r.EntryId));
    }

    [Theory]
    [InlineData("抱歉，我无法完成这个请求。", "没有 JSON 对象")]
    [InlineData("{\"not-a-guid\": \"x\"}", "无法解析")]
    [InlineData("{\"00000000-0000-0000-0000-000000000000\": \"x\"}", "找不到任何词条的译文")]
    public async Task Unusable_Reply_Should_Throw_A_Readable_Error(string reply, string expectedFragment)
    {
        var client = new ScriptedChatClient(reply);
        var service = Create(client);

        var ex = await Assert.ThrowsAsync<AiTranslationException>(() =>
            service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [Item("save", "保存")], []));

        Assert.Contains(expectedFragment, ex.Message);
        Assert.Contains("test-model", ex.Message);
        Assert.Empty(ex.PartialResults);
    }

    [Fact]
    public async Task Reasoning_Block_With_Braces_Should_Be_Stripped_Before_Parsing()
    {
        var item = Item("count", "保存 {count} 项");
        var json = "{\"" + item.EntryId + "\": \"Save {count} items\"}";
        var reply = "<think>placeholder {count} must stay; answer looks like {\"id\": \"text\"}</think>\n```json\n" + json + "\n```";
        var service = Create(new ScriptedChatClient(reply));

        var result = Assert.Single(await service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [item], []));

        Assert.Equal("Save {count} items", result.TranslatedText);
        Assert.DoesNotContain(result.ValidationIssues, i => i.IsBlocking);
    }

    [Fact]
    public async Task Repair_Failure_Should_Keep_The_Models_Original_Translation_Flagged_Not_Throw()
    {
        var item = Item("goto", "进入工作台");
        var glossary = new List<GlossaryTerm>
        {
            new()
            {
                SourceTerm = "工作台",
                TargetTranslations = new(StringComparer.OrdinalIgnoreCase) { ["en-US"] = "Workspace" }
            }
        };

        // 首次译文没用标准术语 → 触发修复重试；修复请求本身失败
        var client = new ScriptedChatClient(
            "{\"" + item.EntryId + "\": \"Go to Dashboard\"}",
            new HttpRequestException("repair endpoint down"));
        var service = Create(client);

        var result = Assert.Single(await service.TranslateBatchAsync("p", "zh-CN", ["en-US"], [item], glossary));

        Assert.Equal("Go to Dashboard", result.TranslatedText);
        Assert.Contains(result.ValidationIssues, i => i.IsBlocking && i.RuleCode == "GLOSSARY_TERM_MISMATCH");
        Assert.Equal(2, client.Prompts.Count);
    }

    private static ServiceProvider BuildAiServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLinguaAi(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddLinguaAi_Should_Register_The_Configured_Model_With_Default_BatchSize()
    {
        using var provider = BuildAiServices(new()
        {
            ["Lingua:AI:Endpoint"] = "http://10.0.0.5:11434/v1",
            ["Lingua:AI:ModelId"] = "qwen3.8:27b-mlx"
        });

        Assert.NotNull(provider.GetService<IChatClient>());

        var options = provider.GetRequiredService<IOptions<LinguaAiOptions>>().Value;
        Assert.Equal("qwen3.8:27b-mlx", options.ModelId);
        Assert.Equal(LinguaAiOptions.DefaultBatchSize, options.BatchSize);
    }

    [Fact]
    public void AddLinguaAi_With_Invalid_Endpoint_Should_Fail_Fast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var provider = BuildAiServices(new() { ["Lingua:AI:Endpoint"] = "not-a-url" });
        });

        Assert.Contains("Lingua:AI:Endpoint", ex.Message);
    }
}
