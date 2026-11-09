using System.ClientModel;
using MASA.Lingua.AI.Services;
using MASA.Lingua.Core.Abstractions;
using MASA.Lingua.Core.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace MASA.Lingua.AI;

public static class LinguaAiExtensions
{
    // OpenAI 兼容 SDK 要求非空 Key；内部/本地服务不校验鉴权，固定占位值即可
    private const string PlaceholderApiKey = "no-auth";

    public static IServiceCollection AddLinguaAi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LinguaAiOptions>(configuration.GetSection(LinguaAiOptions.SectionName));
        services.AddSingleton<DeterministicTranslationValidator>();

        var options = configuration.GetSection(LinguaAiOptions.SectionName).Get<LinguaAiOptions>() ?? new LinguaAiOptions();
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"{LinguaAiOptions.SectionName}:Endpoint 不是合法的绝对地址：{options.Endpoint}");
        }

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            // 显式网络超时：不设则落到 SDK/HttpClient 默认 100s —— 模型端黑洞（连接挂起不响应）时
            // 管理员要干等 100 秒才看到失败。本地大模型批量翻译可能很慢，默认放宽到 180s（可配置）。
            NetworkTimeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
        };
        var openAiClient = new OpenAIClient(new ApiKeyCredential(PlaceholderApiKey), clientOptions);
        services.AddSingleton<IChatClient>(openAiClient.GetChatClient(options.ModelId).AsIChatClient());

        services.AddSingleton<IAiTranslationService, AiTranslationService>();
        return services;
    }
}
