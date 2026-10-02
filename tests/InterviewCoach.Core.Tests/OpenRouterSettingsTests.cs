using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

public class OpenRouterSettingsTests
{
    [Fact]
    public void OpenRouter_has_its_own_models_because_its_names_differ_from_Anthropics()
    {
        var s = new AppSettings { Provider = LlmProvider.OpenRouter };

        Assert.All(Enum.GetValues<LlmRole>(), r => Assert.Equal("anthropic/claude-sonnet-5.5", s.ModelFor(r)));
        Assert.All(Enum.GetValues<LlmRole>(), r => Assert.Equal("claude-sonnet-5-5", s.ModelFor(LlmProvider.Anthropic, r)));
    }

    [Fact]
    public void The_models_in_use_follow_the_provider_and_each_provider_keeps_its_own()
    {
        var s = new AppSettings();
        s.SetModel(LlmProvider.OpenRouter, LlmRole.Coach, "deepseek/deepseek-v4-flash");
        s.SetModel(LlmProvider.Anthropic, LlmRole.Coach, "claude-opus-5-5");

        s.Provider = LlmProvider.OpenRouter;
        Assert.Equal("deepseek/deepseek-v4-flash", s.ModelFor(LlmRole.Coach));

        s.Provider = LlmProvider.Anthropic;
        Assert.Equal("claude-opus-5-5", s.ModelFor(LlmRole.Coach));

        s.Provider = LlmProvider.OpenAiCompatible;
        Assert.Equal("claude-opus-5-5", s.ModelFor(LlmRole.Coach)); // OpenAI-compatible shares the Anthropic set
    }

    [Theory]
    [InlineData(LlmRole.Planner)]
    [InlineData(LlmRole.Interviewer)]
    [InlineData(LlmRole.QuestionGenerator)]
    [InlineData(LlmRole.Coach)]
    [InlineData(LlmRole.Debrief)]
    public void Every_role_can_be_set_and_read_back_for_every_provider(LlmRole role)
    {
        var s = new AppSettings();
        foreach (var provider in new[] { LlmProvider.Anthropic, LlmProvider.OpenRouter })
        {
            s.SetModel(provider, role, $"{provider}-{role}");
            Assert.Equal($"{provider}-{role}", s.ModelFor(provider, role));
        }
    }

    [Fact]
    public void The_OpenRouter_key_falls_back_to_the_environment_when_none_is_stored()
    {
        const string name = "OPENROUTER_API_KEY";
        var before = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "sk-or-from-env");
            Assert.Equal("sk-or-from-env", new AppSettings().EffectiveOpenRouterKey);
            Assert.Equal("stored", new AppSettings { OpenRouterApiKey = "stored" }.EffectiveOpenRouterKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    [Fact]
    public void Prices_and_context_are_described_in_plain_words()
    {
        var paid = new OpenRouterModel("a/b", "A: B", 1_000_000, 2m, 10m);
        var cheap = new OpenRouterModel("a/c", "A: C", 163_840, 0.028m, 0.056m);
        var free = new OpenRouterModel("a/d", "A: D", 32_768, 0m, 0m);
        var unlisted = new OpenRouterModel("a/e", "A: E", 0, null, null);

        Assert.Equal("$2.00 in / $10.00 out per 1M tokens  ·  1M context", paid.Details);
        Assert.Equal("$0.028 in / $0.056 out per 1M tokens  ·  163K context", cheap.Details);
        Assert.Equal("Free  ·  32K context", free.Details);
        Assert.Equal("Price not listed", unlisted.Details);
        Assert.True(free.IsFree);
        Assert.False(paid.IsFree);
    }

    [Fact]
    public void Models_without_a_price_rank_after_every_priced_model()
    {
        var ranked = new[]
        {
            new OpenRouterModel("u", "U", 0, null, null),
            new OpenRouterModel("p", "P", 0, 2m, 10m),
            new OpenRouterModel("f", "F", 0, 0m, 0m),
        }.OrderBy(m => m.CostRank).Select(m => m.Id);

        Assert.Equal(["f", "p", "u"], ranked);
    }
}
