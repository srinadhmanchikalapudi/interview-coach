using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public record Choice<T>(T Value, string Label);

public record TestResultItem(bool Success, string Message)
{
    public string Icon => Success ? "✓" : "✗";
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly ILlmService _llm;
    private readonly TechBank? _bank;
    private readonly IDialogService? _dialogs;

    public SettingsViewModel(ISettingsStore store, ILlmService llm, TechBank? bank = null, IDialogService? dialogs = null)
    {
        _store = store;
        _llm = llm;
        _bank = bank;
        _dialogs = dialogs;
        Load();
    }

    public IReadOnlyList<Choice<LlmProvider>> Providers { get; } =
    [
        new(LlmProvider.Anthropic, "Anthropic"),
        new(LlmProvider.OpenAiCompatible, "OpenAI-compatible (OpenAI, Azure OpenAI, Ollama, LM Studio)"),
    ];

    public IReadOnlyList<Choice<SttProvider>> SttProviders { get; } =
    [
        new(SttProvider.Azure, "Azure AI Speech (live transcripts)"),
        new(SttProvider.OpenAi, "OpenAI (record, then transcribe)"),
    ];

    public IReadOnlyList<Choice<TtsProvider>> TtsProviders { get; } =
    [
        new(TtsProvider.Azure, "Azure AI Speech (neural voices)"),
        new(TtsProvider.OpenAi, "OpenAI"),
        new(TtsProvider.Windows, "Windows offline voice (no key)"),
    ];

    public IReadOnlyList<Choice<ThinkingEffort>> ThinkingEfforts { get; } =
    [
        new(ThinkingEffort.ModelDefault, "Model default"),
        new(ThinkingEffort.Low, "Low (fastest)"),
        new(ThinkingEffort.Medium, "Medium"),
        new(ThinkingEffort.High, "High (slowest)"),
    ];

    public IReadOnlyList<string> ModelSuggestions { get; } =
        ["claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001"];

    // LLM
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAnthropic), nameof(IsOpenAi))] private LlmProvider _provider;
    [ObservableProperty] private string _anthropicApiKey = "";
    [ObservableProperty] private string _openAiApiKey = "";
    [ObservableProperty] private string _openAiBaseUrl = "";
    [ObservableProperty] private string _plannerModel = "";
    [ObservableProperty] private string _interviewerModel = "";
    [ObservableProperty] private string _questionGeneratorModel = "";
    [ObservableProperty] private string _coachModel = "";
    [ObservableProperty] private string _debriefModel = "";
    [ObservableProperty] private ThinkingEffort _thinkingEffort;
    [ObservableProperty] private bool _promptCaching;
    [ObservableProperty] private bool _reuseGeneralAnswers;
    [ObservableProperty] private string _bankStats = "";

    // Speech
    [ObservableProperty] private SttProvider _speechToText;
    [ObservableProperty] private TtsProvider _textToSpeech;
    [ObservableProperty] private string _azureSpeechKey = "";
    [ObservableProperty] private string _azureSpeechRegion = "";
    [ObservableProperty] private double _speakingRate = 1.0;

    // Behavior
    [ObservableProperty] private bool _autoListen;
    [ObservableProperty] private bool _silenceAutoSubmit;
    [ObservableProperty] private int _silenceSeconds = 6;
    [ObservableProperty] private bool _showQuestionTextDefault;
    [ObservableProperty] private bool _demoMode;
    [ObservableProperty] private bool _debugLogging;

    // Status
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isTesting;

    public ObservableCollection<TestResultItem> TestResults { get; } = [];

    public bool IsAnthropic => Provider == LlmProvider.Anthropic;
    public bool IsOpenAi => Provider == LlmProvider.OpenAiCompatible;

    public string AnthropicKeyHint => EnvHint(AnthropicApiKey, "ANTHROPIC_API_KEY");
    public string OpenAiKeyHint => EnvHint(OpenAiApiKey, "OPENAI_API_KEY");
    public string AzureKeyHint => EnvHint(AzureSpeechKey, "AZURE_SPEECH_KEY");

    partial void OnAnthropicApiKeyChanged(string value) => OnPropertyChanged(nameof(AnthropicKeyHint));
    partial void OnOpenAiApiKeyChanged(string value) => OnPropertyChanged(nameof(OpenAiKeyHint));
    partial void OnAzureSpeechKeyChanged(string value) => OnPropertyChanged(nameof(AzureKeyHint));

    /// <summary>Resets the form from the saved settings.</summary>
    public void Load()
    {
        var s = _store.Current;
        Provider = s.Provider;
        AnthropicApiKey = s.AnthropicApiKey ?? "";
        OpenAiApiKey = s.OpenAiApiKey ?? "";
        OpenAiBaseUrl = s.OpenAiBaseUrl ?? "";
        PlannerModel = s.PlannerModel;
        InterviewerModel = s.InterviewerModel;
        QuestionGeneratorModel = s.QuestionGeneratorModel;
        CoachModel = s.CoachModel;
        DebriefModel = s.DebriefModel;
        ThinkingEffort = s.ThinkingEffort;
        PromptCaching = s.PromptCaching;
        ReuseGeneralAnswers = s.ReuseGeneralAnswers;
        SpeechToText = s.SpeechToText;
        TextToSpeech = s.TextToSpeech;
        AzureSpeechKey = s.AzureSpeechKey ?? "";
        AzureSpeechRegion = s.AzureSpeechRegion ?? "";
        SpeakingRate = s.SpeakingRate;
        AutoListen = s.AutoListen;
        SilenceAutoSubmit = s.SilenceAutoSubmit;
        SilenceSeconds = s.SilenceSeconds;
        ShowQuestionTextDefault = s.ShowQuestionTextDefault;
        DemoMode = s.DemoMode;
        DebugLogging = s.DebugLogging;
        StatusMessage = "";
        TestResults.Clear();
        _ = RefreshBankStatsAsync();
    }

    private AppSettings ToSettings()
    {
        var s = _store.Current.Clone(); // keeps fields this screen does not edit yet (voice, mic)
        s.Provider = Provider;
        s.AnthropicApiKey = NullIfBlank(AnthropicApiKey);
        s.OpenAiApiKey = NullIfBlank(OpenAiApiKey);
        s.OpenAiBaseUrl = NullIfBlank(OpenAiBaseUrl);
        s.PlannerModel = PlannerModel.Trim();
        s.InterviewerModel = InterviewerModel.Trim();
        s.QuestionGeneratorModel = QuestionGeneratorModel.Trim();
        s.CoachModel = CoachModel.Trim();
        s.DebriefModel = DebriefModel.Trim();
        s.ThinkingEffort = ThinkingEffort;
        s.PromptCaching = PromptCaching;
        s.ReuseGeneralAnswers = ReuseGeneralAnswers;
        s.SpeechToText = SpeechToText;
        s.TextToSpeech = TextToSpeech;
        s.AzureSpeechKey = NullIfBlank(AzureSpeechKey);
        s.AzureSpeechRegion = NullIfBlank(AzureSpeechRegion);
        s.SpeakingRate = SpeakingRate;
        s.AutoListen = AutoListen;
        s.SilenceAutoSubmit = SilenceAutoSubmit;
        s.SilenceSeconds = Math.Max(1, SilenceSeconds);
        s.ShowQuestionTextDefault = ShowQuestionTextDefault;
        s.DemoMode = DemoMode;
        s.DebugLogging = DebugLogging;
        return s;
    }

    public bool HasBank => _bank is not null;

    /// <summary>Updates the "N questions saved" line. Safe to call at any time; failures just leave the line blank.</summary>
    public async Task RefreshBankStatsAsync()
    {
        if (_bank is null) return;
        try
        {
            var stats = await _bank.GetStatsAsync();
            BankStats = stats.Questions == 0
                ? "Nothing saved yet. Technical-concept questions will be saved here as you use Learn mode."
                : $"{stats.Questions} technical question{(stats.Questions == 1 ? "" : "s")} and {stats.Answers} general answer{(stats.Answers == 1 ? "" : "s")} saved.";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        {
            BankStats = "";
        }
    }

    [RelayCommand]
    private async Task ClearBankAsync()
    {
        if (_bank is null) return;
        if (_dialogs is not null &&
            !_dialogs.Confirm("Clear saved questions", "Delete every saved technical question and general answer? They will be written again as you use Learn mode, which costs a few tokens each."))
            return;

        await _bank.ClearAsync();
        await RefreshBankStatsAsync();
        StatusMessage = "Saved technical questions cleared.";
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _store.Save(ToSettings());
            StatusMessage = $"Saved at {DateTime.Now:t}. API keys are encrypted for this Windows user.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not save settings: {ex.Message}";
        }
    }

    // Tests what is on screen right now, saved or not.
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestConnectionAsync(CancellationToken ct)
    {
        IsTesting = true;
        TestResults.Clear();
        StatusMessage = "Testing…";
        try
        {
            var results = await _llm.TestConnectionAsync(ToSettings(), ct);
            foreach (var r in results)
                TestResults.Add(new TestResultItem(r.Success, $"{r.ModelId}: {r.Message}"));
            StatusMessage = results.All(r => r.Success) ? "Connection works." : "Some checks failed.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Test cancelled.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EnvHint(string typed, string envVar) =>
        string.IsNullOrWhiteSpace(typed) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envVar))
            ? $"Nothing entered; using {envVar} from your environment."
            : $"Or set {envVar} in your environment.";
}
