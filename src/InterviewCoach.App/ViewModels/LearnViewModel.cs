using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

/// <summary>Learn screen (spec 4.4): a question with its Coach output shown straight away.</summary>
public partial class LearnViewModel : ObservableObject
{
    private readonly ILlmService _llm;
    private readonly IPromptLibrary _prompts;
    private readonly ISettingsStore _settings;
    private readonly TechBank? _bank;
    private readonly Func<double>? _random;

    private LearnEngine? _engine;
    private CoachOutput? _coachSource;
    private LearnItem? _shownItem;

    [ObservableProperty] private CoachOutputViewModel? _coach;
    [ObservableProperty] private string? _unexpectedError;

    public LearnViewModel(ILlmService llm, IPromptLibrary prompts, ISettingsStore settings, TechBank? bank = null, Func<double>? random = null)
    {
        _llm = llm;
        _prompts = prompts;
        _settings = settings;
        _bank = bank;
        _random = random;
    }

    /// <summary>Raised when the user asks to go back to the Home screen.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when a different question (or follow-up) becomes the one on screen; the view scrolls back to the top.</summary>
    public event Action? QuestionChanged;

    public LearnItem? Item => _engine?.Current;
    public bool HasQuestion => Item is not null;
    public string Question => Item?.Question ?? "";
    public string TypeLabel => QuestionTypes.LabelFor(Item?.QuestionType);
    public string Focus => Item?.Focus ?? "";
    public bool HasTags => TypeLabel.Length > 0 || Focus.Length > 0;
    public bool IsFollowUp => Item?.IsFollowUp == true;
    public string ParentQuestion => Item?.ParentQuestion ?? "";
    public string Hint => Item?.Hint ?? "";
    public bool HasHint => IsFollowUp && Hint.Length > 0;

    public string Technology => Item?.Technology ?? "";
    public bool HasTechnology => Technology.Length > 0;

    /// <summary>True when the answer on screen was written without the resume (and so can be reused). Offers "Tailor to my resume".</summary>
    public bool IsGenericAnswer => Item is { IsGeneric: true, Coach: not null };
    public string GenericAnswerNote => HasTechnology
        ? $"This is a general answer about {Technology}, written without your resume so it can be saved and reused. Wherever your own experience belongs there is a [bracketed placeholder]."
        : "This is a general answer, written without your resume so it can be saved and reused. Wherever your own experience belongs there is a [bracketed placeholder].";

    public bool IsGenerating => _engine?.Phase == LearnPhase.GeneratingQuestion;
    public bool IsLoadingAnswer => _engine?.Phase == LearnPhase.LoadingAnswer;
    public bool IsFailed => _engine?.Phase == LearnPhase.Failed || UnexpectedError is not null;
    public string ErrorText => UnexpectedError ?? _engine?.Error ?? "";
    public bool CanGoBack => _engine?.CanGoBack == true;

    /// <summary>Next is available any time except while a question is being chosen; skipping ahead abandons a pending answer.</summary>
    public bool CanNext => _engine is not null && !IsGenerating;

    /// <summary>Starts a new Learn session for a saved profile and shows the first question.</summary>
    public void Begin(
        CandidateProfile profile, IReadOnlyCollection<QuestionType> allowedTypes, int? answerWords = null,
        IReadOnlyList<string>? technologies = null, EmploymentType employment = EmploymentType.FullTime)
    {
        _engine?.Cancel();
        // The technology bank is optional: with it off, every question is written by the model from the resume and job description.
        _engine = new LearnEngine(_llm, _prompts, _settings.Current.ReuseGeneralAnswers ? _bank : null, _random) { PrefetchNext = true };
        _engine.Changed += Refresh;
        UnexpectedError = null;
        _coachSource = null;
        Coach = null;
        Refresh();
        _ = RunAsync(() => _engine.StartAsync(profile, allowedTypes, answerWords, technologies, employment));
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => RunAsync(() => _engine!.NextAsync());

    [RelayCommand]
    private Task RetryAsync()
    {
        UnexpectedError = null;
        // Unexpected errors (not LLM failures) have no engine step to repeat, so those start a fresh question.
        return RunAsync(() => _engine!.Phase == LearnPhase.Failed ? _engine.RetryAsync() : _engine.NextAsync());
    }

    /// <summary>Rewrites a general answer from the resume and job description. Costs a normal answer's worth of tokens.</summary>
    [RelayCommand(CanExecute = nameof(IsGenericAnswer))]
    private Task PersonalizeAsync() => RunAsync(() => _engine!.PersonalizeAsync());

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => _engine?.Back();

    [RelayCommand]
    private void Exit()
    {
        _engine?.Cancel();
        ExitRequested?.Invoke();
    }

    private Task OpenFollowUpAsync(FollowUp followUp) => RunAsync(() => _engine!.OpenFollowUpAsync(followUp));

    // LlmExceptions are handled inside the engine (Failed phase + Retry). Anything else is a bug or an environment
    // problem, so show it in the same banner instead of letting an unobserved task swallow it.
    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            UnexpectedError = ex.Message;
            Refresh();
        }
    }

    private void Refresh()
    {
        var item = _engine?.Current;
        if (!ReferenceEquals(item, _shownItem))
        {
            _shownItem = item;
            QuestionChanged?.Invoke();
        }

        var coach = _engine?.Current?.Coach;
        if (!ReferenceEquals(coach, _coachSource))
        {
            _coachSource = coach;
            Coach = coach is null ? null : new CoachOutputViewModel(coach, f => _ = OpenFollowUpAsync(f), _engine?.Current?.QuestionType);
        }

        OnPropertyChanged(string.Empty); // everything above is derived from the engine; refresh all bindings
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        PersonalizeCommand.NotifyCanExecuteChanged();
    }
}
