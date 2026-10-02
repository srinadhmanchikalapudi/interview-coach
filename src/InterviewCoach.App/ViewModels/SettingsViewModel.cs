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
    private readonly IOpenRouterCatalog? _catalog;

    // Both sets of per-role models (the Anthropic/OpenAI one and the OpenRouter one) are held here, so switching the
    // provider on screen swaps the boxes without losing what was typed for the other provider.
    private readonly AppSettings _models = new();
    private bool _loading;
    private IReadOnlyList<OpenRouterModel> _catalogModels = [];

    public SettingsViewModel(ISettingsStore store, ILlmService llm, TechBank? bank = null, IDialogService? dialogs = null, IOpenRouterCatalog? catalog = null)
    {
        _store = store;
        _llm = llm;
        _bank = bank;
        _dialogs = dialogs;
        _catalog = catalog;
        Load();
    }

    public IReadOnlyList<Choice<LlmProvider>> Providers { get; } =
    [
        new(LlmProvider.Anthropic, "Anthropic"),
        new(LlmProvider.OpenAiCompatible, "OpenAI-compatible (OpenAI, Azure OpenAI, Ollama, LM Studio)"),
        new(LlmProvider.OpenRouter, "OpenRouter (one key, hundreds of models)"),
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

    private static readonly string[] AnthropicSuggestions =
        ["claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-haiku-4-5-20251001"];

    // A few well-known OpenRouter models to start from. The full list, with prices, is in "Choose a model" below.
    private static readonly string[] OpenRouterSuggestions =
        ["anthropic/claude-sonnet-5.5", "anthropic/claude-haiku-4.5", "google/gemini-3.5-flash-lite", "deepseek/deepseek-v4-flash"];

    public IReadOnlyList<string> ModelSuggestions => IsOpenRouter ? OpenRouterSuggestions : AnthropicSuggestions;

    // LLM
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAnthropic), nameof(IsOpenAi), nameof(IsOpenRouter), nameof(ModelSuggestions))] private LlmProvider _provider;
    [ObservableProperty] private string _anthropicApiKey = "";
    [ObservableProperty] private string _openAiApiKey = "";
    [ObservableProperty] private string _openAiBaseUrl = "";
    [ObservableProperty] private string _openRouterApiKey = "";
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

    // OpenRouter model browser
    [ObservableProperty] private string _modelFilter = "";
    [ObservableProperty] private bool _cheapestFirst;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UseForQuestionGeneratorCommand), nameof(UseForCoachCommand), nameof(UseForAllRolesCommand))]
    private OpenRouterModel? _selectedCatalogModel;
    [ObservableProperty] private string _catalogStatus = "";
    [ObservableProperty] private bool _isLoadingCatalog;

    // Status
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isTesting;

    public ObservableCollection<TestResultItem> TestResults { get; } = [];

    public bool IsAnthropic => Provider == LlmProvider.Anthropic;
    public bool IsOpenAi => Provider == LlmProvider.OpenAiCompatible;
    public bool IsOpenRouter => Provider == LlmProvider.OpenRouter;

    /// <summary>The models shown in the browser: the loaded catalog after the search box and the sort choice.</summary>
    public ObservableCollection<OpenRouterModel> CatalogModels { get; } = [];
    public bool HasCatalog => _catalogModels.Count > 0;

    public string AnthropicKeyHint => EnvHint(AnthropicApiKey, "ANTHROPIC_API_KEY");
    public string OpenAiKeyHint => EnvHint(OpenAiApiKey, "OPENAI_API_KEY");
    public string OpenRouterKeyHint => EnvHint(OpenRouterApiKey, "OPENROUTER_API_KEY");
    public string AzureKeyHint => EnvHint(AzureSpeechKey, "AZURE_SPEECH_KEY");

    partial void OnAnthropicApiKeyChanged(string value) => OnPropertyChanged(nameof(AnthropicKeyHint));
    partial void OnOpenAiApiKeyChanged(string value) => OnPropertyChanged(nameof(OpenAiKeyHint));
    partial void OnOpenRouterApiKeyChanged(string value) => OnPropertyChanged(nameof(OpenRouterKeyHint));
    partial void OnModelFilterChanged(string value) => ApplyCatalogFilter();
    partial void OnCheapestFirstChanged(bool value) => ApplyCatalogFilter();

    partial void OnProviderChanged(LlmProvider oldValue, LlmProvider newValue)
    {
        if (_loading) return;
        StoreVisibleModels(oldValue);
        ShowModelsFor(newValue);
    }
    partial void OnAzureSpeechKeyChanged(string value) => OnPropertyChanged(nameof(AzureKeyHint));

    /// <summary>Resets the form from the saved settings.</summary>
    public void Load()
    {
        var s = _store.Current;
        _loading = true;
        try
        {
            foreach (var provider in new[] { LlmProvider.Anthropic, LlmProvider.OpenRouter })
                foreach (var role in Enum.GetValues<LlmRole>())
                    _models.SetModel(provider, role, s.ModelFor(provider, role));
            Provider = s.Provider;
            ShowModelsFor(s.Provider);
        }
        finally
        {
            _loading = false;
        }
        AnthropicApiKey = s.AnthropicApiKey ?? "";
        OpenAiApiKey = s.OpenAiApiKey ?? "";
        OpenAiBaseUrl = s.OpenAiBaseUrl ?? "";
        OpenRouterApiKey = s.OpenRouterApiKey ?? "";
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
        s.OpenRouterApiKey = NullIfBlank(OpenRouterApiKey);
        StoreVisibleModels(Provider);
        foreach (var provider in new[] { LlmProvider.Anthropic, LlmProvider.OpenRouter })
            foreach (var role in Enum.GetValues<LlmRole>())
                s.SetModel(provider, role, _models.ModelFor(provider, role).Trim());
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

    // The five boxes always show the models of the provider on screen.
    private void StoreVisibleModels(LlmProvider provider)
    {
        _models.SetModel(provider, LlmRole.Planner, PlannerModel);
        _models.SetModel(provider, LlmRole.Interviewer, InterviewerModel);
        _models.SetModel(provider, LlmRole.QuestionGenerator, QuestionGeneratorModel);
        _models.SetModel(provider, LlmRole.Coach, CoachModel);
        _models.SetModel(provider, LlmRole.Debrief, DebriefModel);
    }

    private void ShowModelsFor(LlmProvider provider)
    {
        PlannerModel = _models.ModelFor(provider, LlmRole.Planner);
        InterviewerModel = _models.ModelFor(provider, LlmRole.Interviewer);
        QuestionGeneratorModel = _models.ModelFor(provider, LlmRole.QuestionGenerator);
        CoachModel = _models.ModelFor(provider, LlmRole.Coach);
        DebriefModel = _models.ModelFor(provider, LlmRole.Debrief);
    }

    // ---- OpenRouter model browser

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        if (_catalog is null)
        {
            CatalogStatus = "The model list is not available in this build.";
            return;
        }

        IsLoadingCatalog = true;
        CatalogStatus = "Loading the model list from OpenRouter…";
        try
        {
            _catalogModels = await _catalog.GetModelsAsync(refresh: true);
            OnPropertyChanged(nameof(HasCatalog));
            ApplyCatalogFilter();
        }
        catch (LlmException ex)
        {
            CatalogStatus = ex.Message;
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    private void ApplyCatalogFilter()
    {
        var words = ModelFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shown = _catalogModels.Where(m => words.All(w =>
            m.Id.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        shown = CheapestFirst
            ? shown.OrderBy(m => m.CostRank).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            : shown.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase);

        var keep = SelectedCatalogModel?.Id;
        CatalogModels.Clear();
        foreach (var m in shown) CatalogModels.Add(m);
        SelectedCatalogModel = CatalogModels.FirstOrDefault(m => m.Id == keep);

        if (_catalogModels.Count > 0)
            CatalogStatus = CatalogModels.Count == _catalogModels.Count
                ? $"{CatalogModels.Count} models. Prices are what OpenRouter charges, per million tokens."
                : $"{CatalogModels.Count} of {_catalogModels.Count} models match.";
    }

    private bool HasSelectedModel() => SelectedCatalogModel is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedModel))]
    private void UseForQuestionGenerator() => Use("Question generator", m => QuestionGeneratorModel = m);

    [RelayCommand(CanExecute = nameof(HasSelectedModel))]
    private void UseForCoach() => Use("Coach", m => CoachModel = m);

    [RelayCommand(CanExecute = nameof(HasSelectedModel))]
    private void UseForAllRoles() => Use("Every role", m =>
    {
        PlannerModel = InterviewerModel = QuestionGeneratorModel = CoachModel = DebriefModel = m;
    });

    private void Use(string what, Action<string> assign)
    {
        if (SelectedCatalogModel is not { } model) return;
        assign(model.Id);
        StatusMessage = $"{what} will use {model.Id}. Click Save to keep it, or Test connection to try it first.";
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
