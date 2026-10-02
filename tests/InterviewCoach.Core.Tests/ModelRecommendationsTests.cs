using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

public class ModelRecommendationsTests
{
    [Theory]
    [InlineData(LlmProvider.Anthropic)]
    [InlineData(LlmProvider.OpenRouter)]
    public void Every_setup_names_a_model_and_a_reason_for_each_of_the_five_roles_exactly_once(LlmProvider provider)
    {
        Assert.NotEmpty(ModelRecommendations.For(provider));
        foreach (var setup in ModelRecommendations.For(provider))
        {
            Assert.Equal(Enum.GetValues<LlmRole>().Order(), setup.Models.Select(m => m.Role).Order());
            Assert.All(setup.Models, m =>
            {
                Assert.False(string.IsNullOrWhiteSpace(m.ModelId));
                Assert.False(string.IsNullOrWhiteSpace(m.Why));
            });
            Assert.False(string.IsNullOrWhiteSpace(setup.Title));
            Assert.False(string.IsNullOrWhiteSpace(setup.Summary));
        }
    }

    [Fact]
    public void OpenAI_compatible_servers_get_no_suggestions_because_their_model_names_are_unknown()
    {
        Assert.Empty(ModelRecommendations.For(LlmProvider.OpenAiCompatible));
    }

    [Fact]
    public void The_first_setup_is_the_one_that_was_tried_with_the_app()
    {
        Assert.True(ModelRecommendations.For(LlmProvider.Anthropic)[0].Tested);
        Assert.True(ModelRecommendations.For(LlmProvider.OpenRouter)[0].Tested);
    }

    [Fact]
    public void Anything_not_tried_with_the_app_is_labelled_as_such()
    {
        var cheaper = ModelRecommendations.For(LlmProvider.OpenRouter).Where(s => !s.Tested).ToList();

        Assert.NotEmpty(cheaper);
        Assert.All(cheaper, s => Assert.Contains("not tried", s.Title + s.Summary, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Questions_use_a_fast_model_and_coaching_uses_the_strong_one()
    {
        foreach (var provider in new[] { LlmProvider.Anthropic, LlmProvider.OpenRouter })
        {
            var setup = ModelRecommendations.For(provider)[0];
            string Of(LlmRole r) => setup.Models.Single(m => m.Role == r).ModelId;

            Assert.Contains("haiku", Of(LlmRole.QuestionGenerator));
            Assert.Contains("sonnet", Of(LlmRole.Coach));
            Assert.Equal(Of(LlmRole.QuestionGenerator), Of(LlmRole.Interviewer)); // both need speed
            Assert.Equal(Of(LlmRole.Coach), Of(LlmRole.Debrief));
        }
    }

    [Fact]
    public void OpenRouter_ids_use_the_maker_slash_model_form_and_Anthropic_ids_do_not()
    {
        foreach (var setup in ModelRecommendations.For(LlmProvider.OpenRouter))
            Assert.All(setup.Models, m => Assert.Matches(@"^[a-z0-9.\-]+/[a-z0-9.\-:]+$", m.ModelId));
        foreach (var setup in ModelRecommendations.For(LlmProvider.Anthropic))
            Assert.All(setup.Models, m => Assert.DoesNotContain("/", m.ModelId));
    }

    [Fact]
    public void Every_setup_asks_for_low_thinking_effort_because_these_models_think_by_default()
    {
        foreach (var provider in new[] { LlmProvider.Anthropic, LlmProvider.OpenRouter })
            Assert.All(ModelRecommendations.For(provider), s => Assert.Equal(ThinkingEffort.Low, s.Thinking));
    }
}
