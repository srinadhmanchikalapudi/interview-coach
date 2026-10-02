using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InterviewCoach.Infrastructure.Llm;

/// <summary>
/// OpenRouter controls how hard a model thinks with its own request field, <c>"reasoning": {"effort": "low"}</c>, not with
/// OpenAI's <c>reasoning_effort</c>. The OpenAI client has no setting for it, so this adds the field to each chat request.
/// Several models on OpenRouter think by default (some always, at high effort), which is most of the wait for a short reply.
/// </summary>
internal sealed class OpenRouterReasoningPolicy(string effort) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddReasoning(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        AddReasoning(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void AddReasoning(PipelineMessage message)
    {
        if (message.Request.Content is not { } content) return;
        try
        {
            using var buffer = new MemoryStream();
            content.WriteTo(buffer);
            if (JsonNode.Parse(buffer.ToArray()) is not JsonObject body) return;
            body["reasoning"] = new JsonObject { ["effort"] = effort };
            message.Request.Content = BinaryContent.Create(BinaryData.FromString(body.ToJsonString()));
        }
        catch (JsonException)
        {
            // Not a JSON body: send it untouched rather than break the call.
        }
    }
}
