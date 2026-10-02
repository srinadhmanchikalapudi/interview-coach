using System.ClientModel;
using System.Collections.Concurrent;
using Anthropic;
using InterviewCoach.Core.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace InterviewCoach.Infrastructure.Llm;

public interface IChatClientFactory
{
    IChatClient Create(AppSettings settings, string modelId);
}

/// <summary>Builds (and caches) an IChatClient for the configured provider and model.</summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly ConcurrentDictionary<string, IChatClient> _cache = new();

    public IChatClient Create(AppSettings settings, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new LlmException("No model ID is set for this role. Check Settings.");

        return settings.Provider switch
        {
            LlmProvider.Anthropic => CreateAnthropic(settings, modelId),
            LlmProvider.OpenAiCompatible => CreateOpenAi(settings, modelId),
            _ => throw new LlmException($"Unknown provider {settings.Provider}."),
        };
    }

    private IChatClient CreateAnthropic(AppSettings settings, string modelId)
    {
        var key = settings.EffectiveAnthropicKey
            ?? throw new LlmException("No Anthropic API key. Add one in Settings or set the ANTHROPIC_API_KEY environment variable.");
        return _cache.GetOrAdd($"anthropic|{key}|{modelId}", _ =>
        {
            var client = new AnthropicClient { ApiKey = key };
            return client.AsIChatClient(modelId);
        });
    }

    private IChatClient CreateOpenAi(AppSettings settings, string modelId)
    {
        var key = settings.EffectiveOpenAiKey;
        var baseUrl = settings.OpenAiBaseUrl?.Trim();
        // Local servers (Ollama, LM Studio) don't need a key, but the client wants a non-empty one.
        if (key is null && string.IsNullOrEmpty(baseUrl))
            throw new LlmException("No OpenAI API key. Add one in Settings or set OPENAI_API_KEY, or give a base URL for a local server.");

        return _cache.GetOrAdd($"openai|{key}|{baseUrl}|{modelId}", _ =>
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrEmpty(baseUrl))
            {
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
                    throw new LlmException($"'{baseUrl}' is not a valid base URL.");
                options.Endpoint = endpoint;
            }
            var client = new OpenAIClient(new ApiKeyCredential(key ?? "not-needed"), options);
            return client.GetChatClient(modelId).AsIChatClient();
        });
    }
}
