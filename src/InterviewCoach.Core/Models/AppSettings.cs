using System.Text.Json.Serialization;

namespace InterviewCoach.Core.Models;

public enum LlmProvider { Anthropic, OpenAiCompatible, OpenRouter }

/// <summary>How hard models that think by default are asked to think. Lower is faster; ModelDefault sends nothing.</summary>
public enum ThinkingEffort { ModelDefault, Low, Medium, High }
public enum SttProvider { Azure, OpenAi }
public enum TtsProvider { Azure, OpenAi, Windows }

public class AppSettings
{
    public const string DefaultModel = "claude-sonnet-5-5";

    /// <summary>OpenRouter names models "maker/model", so it has its own default and its own set of per-role models.</summary>
    public const string DefaultOpenRouterModel = "anthropic/claude-sonnet-5.5";

    /// <summary>OpenRouter speaks the OpenAI protocol at this address, with one key for every model it offers.</summary>
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    // LLM
    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenRouterApiKey { get; set; }
    public string PlannerModel { get; set; } = DefaultModel;
    public string InterviewerModel { get; set; } = DefaultModel;
    public string QuestionGeneratorModel { get; set; } = DefaultModel;
    public string CoachModel { get; set; } = DefaultModel;
    public string DebriefModel { get; set; } = DefaultModel;

    // The same five roles when the provider is OpenRouter. They are kept apart because the model names differ.
    public string OpenRouterPlannerModel { get; set; } = DefaultOpenRouterModel;
    public string OpenRouterInterviewerModel { get; set; } = DefaultOpenRouterModel;
    public string OpenRouterQuestionGeneratorModel { get; set; } = DefaultOpenRouterModel;
    public string OpenRouterCoachModel { get; set; } = DefaultOpenRouterModel;
    public string OpenRouterDebriefModel { get; set; } = DefaultOpenRouterModel;

    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.ModelDefault;
    /// <summary>Mark the resume and job description part of each prompt for Anthropic prompt caching (about a tenth of the price on repeat calls).</summary>
    public bool PromptCaching { get; set; } = true;
    /// <summary>The kind of job being practiced for. It is remembered between runs because it rarely changes from one session to the next.</summary>
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    /// <summary>Serve technical-concept questions from the saved technology bank, with general answers that cost nothing to reuse.</summary>
    public bool ReuseGeneralAnswers { get; set; } = true;

    // Speech
    public SttProvider SpeechToText { get; set; } = SttProvider.Azure;
    // The Windows voice needs no key, so a new install can speak questions straight away; Azure and OpenAI voices are chosen in Settings.
    public TtsProvider TextToSpeech { get; set; } = TtsProvider.Windows;
    public string? AzureSpeechKey { get; set; }
    public string? AzureSpeechRegion { get; set; }
    public string? Voice { get; set; }
    public double SpeakingRate { get; set; } = 1.0;
    public string? MicrophoneDeviceId { get; set; }

    // Technology concepts (the Concepts page remembers the last role and difficulty)
    public string? ConceptRole { get; set; }
    public Difficulty ConceptDifficulty { get; set; } = Difficulty.Medium;

    // Mock Interview (the Home screen remembers the last round and length)
    public RoundType MockRoundType { get; set; } = RoundType.Mixed;
    public int MockDurationMinutes { get; set; } = RoundTypes.DefaultDuration;

    // Updates
    /// <summary>Look for a newer release on GitHub when the program starts (at most once a day). Only the version number is asked for; nothing is sent about you.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }

    // Behavior
    /// <summary>Speak each Practice question aloud when it appears (when a voice is ready).</summary>
    public bool SpeakQuestions { get; set; } = true;
    public bool AutoListen { get; set; } = true;
    public bool SilenceAutoSubmit { get; set; }
    public int SilenceSeconds { get; set; } = 6;
    public bool ShowQuestionTextDefault { get; set; } = true;
    public bool DemoMode { get; set; }
    public bool DebugLogging { get; set; }

    /// <summary>The model for a role under the provider that is currently selected.</summary>
    public string ModelFor(LlmRole role) => ModelFor(Provider, role);

    /// <summary>The model saved for a role under a given provider. Anthropic and OpenAI-compatible share one set; OpenRouter has its own.</summary>
    public string ModelFor(LlmProvider provider, LlmRole role) => (provider == LlmProvider.OpenRouter, role) switch
    {
        (false, LlmRole.Planner) => PlannerModel,
        (false, LlmRole.Interviewer) => InterviewerModel,
        (false, LlmRole.QuestionGenerator) => QuestionGeneratorModel,
        (false, LlmRole.Coach) => CoachModel,
        (false, LlmRole.Debrief) => DebriefModel,
        (true, LlmRole.Planner) => OpenRouterPlannerModel,
        (true, LlmRole.Interviewer) => OpenRouterInterviewerModel,
        (true, LlmRole.QuestionGenerator) => OpenRouterQuestionGeneratorModel,
        (true, LlmRole.Coach) => OpenRouterCoachModel,
        (true, LlmRole.Debrief) => OpenRouterDebriefModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public void SetModel(LlmProvider provider, LlmRole role, string model)
    {
        switch (provider == LlmProvider.OpenRouter, role)
        {
            case (false, LlmRole.Planner): PlannerModel = model; break;
            case (false, LlmRole.Interviewer): InterviewerModel = model; break;
            case (false, LlmRole.QuestionGenerator): QuestionGeneratorModel = model; break;
            case (false, LlmRole.Coach): CoachModel = model; break;
            case (false, LlmRole.Debrief): DebriefModel = model; break;
            case (true, LlmRole.Planner): OpenRouterPlannerModel = model; break;
            case (true, LlmRole.Interviewer): OpenRouterInterviewerModel = model; break;
            case (true, LlmRole.QuestionGenerator): OpenRouterQuestionGeneratorModel = model; break;
            case (true, LlmRole.Coach): OpenRouterCoachModel = model; break;
            case (true, LlmRole.Debrief): OpenRouterDebriefModel = model; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
    }

    // The stored value wins; otherwise fall back to the environment variable.
    [JsonIgnore] public string? EffectiveAnthropicKey => Pick(AnthropicApiKey, "ANTHROPIC_API_KEY");
    [JsonIgnore] public string? EffectiveOpenAiKey => Pick(OpenAiApiKey, "OPENAI_API_KEY");
    [JsonIgnore] public string? EffectiveOpenRouterKey => Pick(OpenRouterApiKey, "OPENROUTER_API_KEY");
    [JsonIgnore] public string? EffectiveAzureSpeechKey => Pick(AzureSpeechKey, "AZURE_SPEECH_KEY");
    [JsonIgnore] public string? EffectiveAzureSpeechRegion => Pick(AzureSpeechRegion, "AZURE_SPEECH_REGION");

    /// <summary>True when the selected language-model provider has what it needs to be called: a key, or for an OpenAI-compatible server an address (local servers need no key).</summary>
    [JsonIgnore]
    public bool HasLlmCredentials => Provider switch
    {
        LlmProvider.Anthropic => !string.IsNullOrWhiteSpace(EffectiveAnthropicKey),
        LlmProvider.OpenRouter => !string.IsNullOrWhiteSpace(EffectiveOpenRouterKey),
        _ => !string.IsNullOrWhiteSpace(EffectiveOpenAiKey) || !string.IsNullOrWhiteSpace(OpenAiBaseUrl),
    };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static string? Pick(string? stored, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
