using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;
using Microsoft.Extensions.AI;

namespace InterviewCoach.Infrastructure.Tests;

public class LlmServiceTests
{
    private sealed record Reply(string Say);

    private sealed class ScriptedChatClient(params string[] replies) : IChatClient
    {
        private readonly Queue<string> _replies = new(replies);
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public List<string> Models { get; } = [];
        public IChatClient Create(AppSettings settings, string modelId) { Models.Add(modelId); return client; }
    }

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private static LlmService Service(IChatClient client, AppSettings? settings = null, FixedFactory? factory = null)
        => new(new MemorySettings(settings ?? new AppSettings()), factory ?? new FixedFactory(client));

    [Fact]
    public async Task Returns_parsed_json_and_sends_system_prompt_first()
    {
        var client = new ScriptedChatClient("""{"say": "hello"}""");

        var reply = await Service(client).GetJsonAsync<Reply>(LlmRole.Coach, "SYSTEM", [new(ChatTurnRole.User, "Coach this.")], default);

        Assert.Equal("hello", reply.Say);
        var sent = Assert.Single(client.Requests);
        Assert.Equal(ChatRole.System, sent[0].Role);
        Assert.Equal("SYSTEM", sent[0].Text);
        Assert.Equal("Coach this.", sent[1].Text);
    }

    [Fact]
    public async Task Bad_json_triggers_exactly_one_repair_retry_with_the_bad_output_attached()
    {
        var client = new ScriptedChatClient("not json at all", """{"say": "fixed"}""");

        var reply = await Service(client).GetJsonAsync<Reply>(LlmRole.Coach, "SYSTEM", [new(ChatTurnRole.User, "go")], default);

        Assert.Equal("fixed", reply.Say);
        Assert.Equal(2, client.Requests.Count);
        var retry = client.Requests[1];
        Assert.Equal(ChatRole.Assistant, retry[^2].Role);
        Assert.Equal("not json at all", retry[^2].Text);
        Assert.Equal(ChatRole.User, retry[^1].Role);
        Assert.StartsWith("That wasn't valid JSON matching the schema. Error: ", retry[^1].Text);
        Assert.EndsWith("Reply with only the corrected JSON.", retry[^1].Text);
    }

    [Fact]
    public async Task Two_bad_replies_surface_as_LlmException()
    {
        var client = new ScriptedChatClient("nope", "still nope");

        await Assert.ThrowsAsync<LlmException>(() =>
            Service(client).GetJsonAsync<Reply>(LlmRole.Coach, "S", [new(ChatTurnRole.User, "go")], default));
        Assert.Equal(2, client.Requests.Count);
    }

    // The Anthropic adapter records a cache breakpoint as this additional property on the content block.
    private static bool IsCacheBreakpoint(AIContent content) => content.AdditionalProperties?.ContainsKey("anthropic:cache_control") == true;

    private const string PromptWithResume =
        "You are a coach.\n<job_description>\nJD text\n</job_description>\n<candidate_resume>\nResume text\n</candidate_resume>\n\n<context>\nMode: learn\n</context>\n<question>\nQ?\n</question>";

    [Fact]
    public void System_prompt_is_split_after_the_resume_with_a_cache_marker_on_the_stable_part()
    {
        var message = LlmService.BuildSystemMessage(new AppSettings(), PromptWithResume);

        Assert.Equal(ChatRole.System, message.Role);
        var parts = message.Contents.OfType<TextContent>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.EndsWith("</candidate_resume>", parts[0].Text);
        Assert.True(IsCacheBreakpoint(parts[0]));   // the resume and JD are what gets cached
        Assert.False(IsCacheBreakpoint(parts[1]));  // the part that changes every call is not
        Assert.StartsWith("\n\n<context>", parts[1].Text);
        Assert.Equal(PromptWithResume, parts[0].Text + parts[1].Text); // nothing lost or reordered
    }

    private const string CoachStylePrompt =
        "Fixed guidance line one.\nFixed guidance line two.\n\n=== THE CANDIDATE AND THE QUESTION ===\n\n" +
        "<job_description>\nJD text\n</job_description>\n\n<candidate_resume>\nResume text\n</candidate_resume>\n\n" +
        "<context>\nMode: learn\n</context>\n\n<question>\nQ?\n</question>";

    [Fact]
    public void A_coach_style_prompt_gets_two_cache_markers_one_after_the_fixed_guidance_and_one_after_the_resume()
    {
        var message = LlmService.BuildSystemMessage(new AppSettings(), CoachStylePrompt);

        var parts = message.Contents.OfType<TextContent>().ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal("Fixed guidance line one.\nFixed guidance line two.\n\n", parts[0].Text);       // fixed for everyone
        Assert.EndsWith("</candidate_resume>", parts[1].Text);                                        // fixed within a profile
        Assert.StartsWith("=== THE CANDIDATE AND THE QUESTION ===", parts[1].Text);
        Assert.StartsWith("\n\n<context>", parts[2].Text);                                            // changes every call
        Assert.True(IsCacheBreakpoint(parts[0]));
        Assert.True(IsCacheBreakpoint(parts[1]));
        Assert.False(IsCacheBreakpoint(parts[2]));
        Assert.Equal(CoachStylePrompt, string.Concat(parts.Select(p => p.Text)));                    // nothing lost or reordered
    }

    [Fact]
    public void Splitting_never_creates_an_empty_block_even_when_the_resume_ends_the_prompt()
    {
        const string endsWithResume = "Guidance.\n\n=== THE CANDIDATE AND THE QUESTION ===\n<candidate_resume>\nR\n</candidate_resume>";

        var message = LlmService.BuildSystemMessage(new AppSettings(), endsWithResume);

        var parts = message.Contents.OfType<TextContent>().ToList();
        Assert.All(parts, p => Assert.False(string.IsNullOrEmpty(p.Text)));
        Assert.Equal(endsWithResume, string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void Caching_is_skipped_when_switched_off_for_other_providers_and_for_prompts_without_a_resume()
    {
        var off = LlmService.BuildSystemMessage(new AppSettings { PromptCaching = false }, PromptWithResume);
        var openAi = LlmService.BuildSystemMessage(new AppSettings { Provider = LlmProvider.OpenAiCompatible }, PromptWithResume);
        var noResume = LlmService.BuildSystemMessage(new AppSettings(), "This is a connectivity check.");

        foreach (var message in new[] { off, openAi, noResume })
        {
            var single = Assert.Single(message.Contents.OfType<TextContent>());
            Assert.False(IsCacheBreakpoint(single));
        }
        Assert.Equal(PromptWithResume, off.Text);
    }

    [Fact]
    public async Task The_split_system_message_is_what_the_model_client_receives()
    {
        var client = new ScriptedChatClient("""{"say": "ok"}""");

        await Service(client).GetJsonAsync<Reply>(LlmRole.Coach, PromptWithResume, [new(ChatTurnRole.User, "Coach this.")], default);

        var system = client.Requests.Single()[0];
        Assert.Equal(2, system.Contents.OfType<TextContent>().Count());
        Assert.Equal(PromptWithResume, system.Text);
    }

    [Fact]
    public void Caching_is_on_by_default()
    {
        Assert.True(new AppSettings().PromptCaching);
    }

    [Fact]
    public void Thinking_effort_is_not_sent_unless_chosen()
    {
        var options = LlmService.OptionsFor(new AppSettings(), "claude-sonnet-5-5");

        Assert.Null(options.Reasoning);
        Assert.NotNull(options.MaxOutputTokens);
    }

    [Theory]
    [InlineData(ThinkingEffort.Low, ReasoningEffort.Low)]
    [InlineData(ThinkingEffort.Medium, ReasoningEffort.Medium)]
    [InlineData(ThinkingEffort.High, ReasoningEffort.High)]
    public void Chosen_thinking_effort_is_sent_for_non_haiku_anthropic_models(ThinkingEffort chosen, ReasoningEffort expected)
    {
        var options = LlmService.OptionsFor(new AppSettings { ThinkingEffort = chosen }, "claude-sonnet-5-5");

        Assert.Equal(expected, options.Reasoning!.Effort);
    }

    [Fact]
    public void Thinking_effort_is_never_sent_to_haiku_or_other_providers()
    {
        var low = new AppSettings { ThinkingEffort = ThinkingEffort.Low };
        Assert.Null(LlmService.OptionsFor(low, "claude-haiku-4-5-20251001").Reasoning);
        Assert.Null(LlmService.OptionsFor(new AppSettings { ThinkingEffort = ThinkingEffort.Low, Provider = LlmProvider.OpenAiCompatible }, "gpt-x").Reasoning);
    }

    [Fact]
    public async Task Each_role_uses_its_own_model()
    {
        var settings = new AppSettings { InterviewerModel = "fast-model", CoachModel = "strong-model" };
        var client = new ScriptedChatClient("""{"say": "a"}""", """{"say": "b"}""");
        var factory = new FixedFactory(client);
        var service = Service(client, settings, factory);

        await service.GetJsonAsync<Reply>(LlmRole.Interviewer, "S", [new(ChatTurnRole.User, "x")], default);
        await service.GetJsonAsync<Reply>(LlmRole.Coach, "S", [new(ChatTurnRole.User, "x")], default);

        Assert.Equal(["fast-model", "strong-model"], factory.Models);
    }

    [Fact]
    public async Task Test_connection_checks_each_distinct_model_once()
    {
        var settings = new AppSettings { InterviewerModel = "fast-model" }; // the other four roles share the default
        var client = new ScriptedChatClient("""{"ok": true}""", """{"ok": true}""");

        var results = await Service(client, settings).TestConnectionAsync(settings, default);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Contains(results, r => r.ModelId == "fast-model");
    }

    [Fact]
    public async Task Test_connection_reports_failures_instead_of_throwing()
    {
        var factory = new ThrowingFactory();

        var results = await new LlmService(new MemorySettings(new AppSettings()), factory).TestConnectionAsync(new AppSettings(), default);

        var failure = Assert.Single(results);
        Assert.False(failure.Success);
        Assert.Contains("No Anthropic API key", failure.Message);
    }

    private sealed class ThrowingFactory : IChatClientFactory
    {
        public IChatClient Create(AppSettings settings, string modelId) => throw new LlmException("No Anthropic API key.");
    }
}
