using System.Text.Json.Serialization;

namespace InterviewCoach.Core.Models;

public enum LlmProvider { Anthropic, OpenAiCompatible }

/// <summary>How hard models that think by default are asked to think. Lower is faster; ModelDefault sends nothing.</summary>
public enum ThinkingEffort { ModelDefault, Low, Medium, High }
public enum SttProvider { Azure, OpenAi }
public enum TtsProvider { Azure, OpenAi, Windows }

public class AppSettings
{
    public const string DefaultModel = "claude-sonnet-5-5";

    // LLM
    public LlmProvider Provider { get; set; } = LlmProvider.Anthropic;
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? OpenAiBaseUrl { get; set; }
    public string PlannerModel { get; set; } = DefaultModel;
    public string InterviewerModel { get; set; } = DefaultModel;
    public string QuestionGeneratorModel { get; set; } = DefaultModel;
    public string CoachModel { get; set; } = DefaultModel;
    public string DebriefModel { get; set; } = DefaultModel;
    public ThinkingEffort ThinkingEffort { get; set; } = ThinkingEffort.ModelDefault;
    /// <summary>Mark the resume and job description part of each prompt for Anthropic prompt caching (about a tenth of the price on repeat calls).</summary>
    public bool PromptCaching { get; set; } = true;
    /// <summary>The kind of job being practiced for. It is remembered between runs because it rarely changes from one session to the next.</summary>
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    /// <summary>Serve technical-concept questions from the saved technology bank, with general answers that cost nothing to reuse.</summary>
    public bool ReuseGeneralAnswers { get; set; } = true;

    // Speech
    public SttProvider SpeechToText { get; set; } = SttProvider.Azure;
    public TtsProvider TextToSpeech { get; set; } = TtsProvider.Azure;
    public string? AzureSpeechKey { get; set; }
    public string? AzureSpeechRegion { get; set; }
    public string? Voice { get; set; }
    public double SpeakingRate { get; set; } = 1.0;
    public string? MicrophoneDeviceId { get; set; }

    // Behavior
    public bool AutoListen { get; set; } = true;
    public bool SilenceAutoSubmit { get; set; }
    public int SilenceSeconds { get; set; } = 6;
    public bool ShowQuestionTextDefault { get; set; } = true;
    public bool DemoMode { get; set; }
    public bool DebugLogging { get; set; }

    public string ModelFor(LlmRole role) => role switch
    {
        LlmRole.Planner => PlannerModel,
        LlmRole.Interviewer => InterviewerModel,
        LlmRole.QuestionGenerator => QuestionGeneratorModel,
        LlmRole.Coach => CoachModel,
        LlmRole.Debrief => DebriefModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    // The stored value wins; otherwise fall back to the environment variable.
    [JsonIgnore] public string? EffectiveAnthropicKey => Pick(AnthropicApiKey, "ANTHROPIC_API_KEY");
    [JsonIgnore] public string? EffectiveOpenAiKey => Pick(OpenAiApiKey, "OPENAI_API_KEY");
    [JsonIgnore] public string? EffectiveAzureSpeechKey => Pick(AzureSpeechKey, "AZURE_SPEECH_KEY");
    [JsonIgnore] public string? EffectiveAzureSpeechRegion => Pick(AzureSpeechRegion, "AZURE_SPEECH_REGION");

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static string? Pick(string? stored, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
