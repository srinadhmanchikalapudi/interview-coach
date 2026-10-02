namespace InterviewCoach.Core.Models;

/// <summary>The model suggested for one role, and why in a sentence a user can act on.</summary>
public sealed record RoleModel(LlmRole Role, string ModelId, string Why);

/// <summary>A set of models for all five roles plus the thinking effort that goes with it. <see cref="Tested"/> says whether it was tried in this app.</summary>
public sealed record RecommendedSetup(string Title, string Summary, bool Tested, ThinkingEffort Thinking, IReadOnlyList<RoleModel> Models);

/// <summary>
/// Suggested models for each role, per provider. The first setup of a provider is the one to start with.
/// "Tested" setups use the models the prompts were written and measured against; the others are cheaper guesses that
/// have only been checked against the provider's published list (price, thinking support), not against this app's replies.
/// </summary>
public static class ModelRecommendations
{
    private const string GeneratorWhy = "Writes one short question, so a small, fast model is enough.";
    private const string CoachWhy = "Writes the answer you read and practise from, so quality matters most here.";
    private const string PlannerWhy = "Plans a whole interview once per session (Mock Interview, coming later).";
    private const string InterviewerWhy = "Speaks live in Mock Interview (coming later), so speed matters.";
    private const string DebriefWhy = "Reads the whole interview and writes the review (Mock Interview, coming later).";

    private static RecommendedSetup Setup(string title, string summary, bool tested, string strong, string fast) =>
        new(title, summary, tested, ThinkingEffort.Low,
        [
            new(LlmRole.QuestionGenerator, fast, GeneratorWhy),
            new(LlmRole.Coach, strong, CoachWhy),
            new(LlmRole.Planner, strong, PlannerWhy),
            new(LlmRole.Interviewer, fast, InterviewerWhy),
            new(LlmRole.Debrief, strong, DebriefWhy),
        ]);

    private static readonly RecommendedSetup AnthropicSetup = Setup(
        "Recommended",
        "A small, fast model writes the questions and a strong one writes the coaching, with Thinking effort set to Low. This is the mix the app was built and measured with: coach answers took about 10 seconds at Low against about 21 at the default. Point at a row to see why.",
        tested: true, strong: "claude-sonnet-5-5", fast: "claude-haiku-4-5-20251001");

    private static readonly RecommendedSetup OpenRouterSetup = Setup(
        "Recommended",
        "The same models the app was built and measured with, through OpenRouter: a small, fast one for questions and a strong one for coaching. Claude Sonnet 5.5 always thinks on OpenRouter and defaults to high effort, which is slow, so this also sets Thinking effort to Low. Point at a row to see why.",
        tested: true, strong: "anthropic/claude-sonnet-5.5", fast: "anthropic/claude-haiku-4.5");

    private static readonly RecommendedSetup OpenRouterCheaper = Setup(
        "Lower cost (not tried yet)",
        "Much cheaper models that support low thinking effort. They have not been tried with this app's questions and coaching, so check a few answers (and use Test connection) before relying on them. If answers look thin or a reply is rejected, go back to Recommended.",
        tested: false, strong: "openai/gpt-5-mini", fast: "google/gemini-3.5-flash-lite");

    /// <summary>Setups worth offering for a provider. Empty when model names cannot be known in advance (OpenAI-compatible servers).</summary>
    public static IReadOnlyList<RecommendedSetup> For(LlmProvider provider) => provider switch
    {
        LlmProvider.Anthropic => [AnthropicSetup],
        LlmProvider.OpenRouter => [OpenRouterSetup, OpenRouterCheaper],
        _ => [],
    };
}
