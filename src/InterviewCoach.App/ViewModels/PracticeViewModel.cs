using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.App.Services;

namespace InterviewCoach.App.ViewModels;

public enum TimerLevel { Normal, Amber, Red }

/// <summary>
/// Practice screen (spec 4.5 and 4.6, typed only): a question, an answer box with a live word count and a timer, then the
/// Coach's feedback with Try again, follow-ups and Next question. Nothing is coached until Submit.
/// </summary>
public partial class PracticeViewModel : ObservableObject
{
    /// <summary>Seconds after which the timer turns amber, and red, for behavioral and project questions (spec 4.6).</summary>
    public const int AmberSeconds = 150;
    public const int RedSeconds = 210;

    public const string EmptyAnswerMessage = "Say or type something first. 'I don't know' is a fine answer too.";
    public const string UnchangedAnswerMessage = "That is the same as your last answer. Change something, then submit, so the feedback can say what improved.";

    private readonly ILlmService _llm;
    private readonly IPromptLibrary _prompts;
    private readonly ISettingsStore _settings;
    private readonly TechBank? _bank;
    private readonly Func<double>? _random;
    private readonly Func<DateTime> _now;
    private readonly IPracticeHistory? _history;

    private PracticeEngine? _engine;
    private LearnSessionRequest? _request;
    private LearnItem? _shownItem;
    private int _shownAttempt = 1;
    private PracticeAttempt? _attemptSource;
    private string _lastAnswerText = "";
    private DateTime? _startedAt;
    private int _frozenSeconds;

    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private CoachOutputViewModel? _coach;
    [ObservableProperty] private string? _unexpectedError;

    public PracticeViewModel(
        ILlmService llm, IPromptLibrary prompts, ISettingsStore settings, TechBank? bank = null, Func<double>? random = null, Func<DateTime>? now = null,
        IPracticeHistory? history = null, ISpeechFactory? speech = null)
    {
        _history = history;
        _now = now ?? (() => DateTime.UtcNow);
        _speech = speech;
        _speaker = speech is null ? null : new Speaker(speech);
        if (_speaker is not null) _speaker.Changed += OnSpeakerChanged;
        ReadAloudCommand = new AsyncRelayCommand(ReadAloudAsync);

        // The answer box and its microphone are shared with Mock Interview. Its property names are the ones this screen binds to,
        // so its change notifications are passed on under the same names.
        Composer = new AnswerComposer(speech, settings, _now, () => IsAnswering);
        Composer.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Composer.TextChanged += OnAnswerTextChanged;
        Composer.MicStarted += () =>
        {
            StopSpeaking(); // talking over the interviewer ends the question
            // The timer starts at the first keystroke or the first use of the microphone (spec 4.6).
            if (_startedAt is null && _frozenSeconds == 0) _startedAt = _now();
        };
        Composer.SubmitRequested += () => _ = SubmitCommand.ExecuteAsync(null);
        _llm = llm;
        _prompts = prompts;
        _settings = settings;
        _bank = bank;
        _random = random;
    }

    /// <summary>Raised when the user asks to go back to the Home screen.</summary>
    public event Action? ExitRequested;

    /// <summary>Raised when a different question becomes the one on screen; the view scrolls back to the top.</summary>
    public event Action? QuestionChanged;

    // ---- starting

    /// <summary>Starts a Practice session for a saved profile and shows the first question.</summary>
    public void Begin(LearnSessionRequest request)
    {
        StartEngine(request);
        _ = RunAsync(() => _engine!.StartAsync(request.Profile, request.Types, request.AnswerWords, request.Technologies, request.Employment));
    }

    /// <summary>Starts a session on a question the user already read in Learn mode. The next one uses the same filter as that session.</summary>
    public void BeginFrom(LearnSessionRequest request, LearnItem question)
    {
        StartEngine(request);
        _engine!.StartWithQuestion(request.Profile, question, request.Types, request.AnswerWords, request.Technologies, request.Employment);
    }

    private void StartEngine(LearnSessionRequest request)
    {
        _engine?.Cancel();
        StopVoice();
        _request = request;
        // The technology bank is optional, as in Learn mode: with it off every question is written from the resume and job description.
        _engine = new PracticeEngine(_llm, _prompts, _settings.Current.ReuseGeneralAnswers ? _bank : null, _random, _history);
        _engine.Changed += Refresh;
        UnexpectedError = null;
        _shownItem = null;
        _attemptSource = null;
        _shownAttempt = 1;
        _lastAnswerText = "";
        Coach = null;
        Composer.SetText("");
        EmptyMessage = "";
        SpeechMessage = "";
        ResetTimer();
        Refresh();
    }

    // ---- what is on screen

    public LearnItem? Item => _engine?.Current;
    public bool HasQuestion => Item is not null;
    public string Question => Item?.Question ?? "";
    public string TypeLabel => QuestionTypes.LabelFor(Item?.QuestionType);
    public string Focus => Item?.Focus ?? "";
    public string Technology => Item?.Technology ?? "";
    public bool HasTechnology => Technology.Length > 0;
    public bool HasTags => TypeLabel.Length > 0 || Focus.Length > 0;
    public bool IsFollowUp => Item?.IsFollowUp == true;
    public string ParentQuestion => Item?.ParentQuestion ?? "";

    public bool IsGenerating => _engine?.Phase == PracticePhase.GeneratingQuestion;
    public bool IsAnswering => _engine?.Phase == PracticePhase.Answering;
    public bool IsCoaching => _engine?.Phase == PracticePhase.Coaching;
    public bool IsFeedback => _engine?.Phase == PracticePhase.ShowingFeedback;
    public bool IsFailed => _engine?.Phase == PracticePhase.Failed || UnexpectedError is not null;
    public string ErrorText => UnexpectedError ?? _engine?.Error ?? "";

    /// <summary>True when the Coach call failed after an answer was sent: the answer is shown with Retry and Edit.</summary>
    public bool FailedAfterAnswer => _engine is { Phase: PracticePhase.Failed, PendingAnswer: not null } && Item is not null;

    public int AttemptNumber => _engine?.AttemptNumber ?? 1;
    public string AttemptLabel => AttemptNumber > 1 ? $"Attempt {AttemptNumber}" : "";
    public bool HasAttemptLabel => AttemptNumber > 1;
    public bool CanUsePreviousAnswer => IsAnswering && AttemptNumber > 1 && _lastAnswerText.Length > 0;

    /// <summary>The answer that was sent, shown above the feedback and while the Coach is reading it.</summary>
    public bool ShowSubmittedAnswer => IsCoaching || IsFeedback || FailedAfterAnswer;
    public string SubmittedAnswer => _engine?.LastAttempt?.AnswerText ?? _engine?.PendingAnswer ?? "";
    public string SubmittedMeta
    {
        get
        {
            if (_engine?.LastAttempt is { } a) return Meta(a.WordCount, a.DurationSeconds);
            return _engine?.PendingAnswer is { } text ? Meta(AnswerLength.CountWords(text), _frozenSeconds) : "";
        }
    }

    public bool CanNext => _engine is not null && !IsGenerating;
    public bool CanTryAgain => _engine?.CanTryAgain == true;

    // ---- the answer box

    /// <summary>The answer box and its microphone (shared with Mock Interview).</summary>
    public AnswerComposer Composer { get; }

    public string AnswerText
    {
        get => Composer.AnswerText;
        set => Composer.AnswerText = value;
    }

    public int WordCount => Composer.WordCount;
    public string WordCountText => Composer.WordCountText;
    public bool HasEmptyMessage => EmptyMessage.Length > 0;

    public int ElapsedSeconds => _startedAt is { } start ? Math.Max(0, (int)(_now() - start).TotalSeconds) : _frozenSeconds;
    public string TimerText => FormatTime(ElapsedSeconds);

    /// <summary>Amber past 2:30 and red past 3:30, but only for behavioral and project questions (spec 4.6).</summary>
    public TimerLevel TimerLevel
    {
        get
        {
            if (Item?.QuestionType is not ("behavioral" or "resume_deep_dive")) return TimerLevel.Normal;
            return ElapsedSeconds >= RedSeconds ? TimerLevel.Red : ElapsedSeconds >= AmberSeconds ? TimerLevel.Amber : TimerLevel.Normal;
        }
    }

    public bool IsTimerAmber => TimerLevel == TimerLevel.Amber;
    public bool IsTimerRed => TimerLevel == TimerLevel.Red;

    /// <summary>Called about once a second by the view while the answer box is open, so the timer text and colour keep up.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(ElapsedSeconds));
        OnPropertyChanged(nameof(TimerText));
        OnPropertyChanged(nameof(TimerLevel));
        OnPropertyChanged(nameof(IsTimerAmber));
        OnPropertyChanged(nameof(IsTimerRed));
        Composer.CheckSilence();
    }

    private void OnAnswerTextChanged(string value)
    {
        // The timer starts at the first keystroke (spec 4.6).
        if (_startedAt is null && IsAnswering && _frozenSeconds == 0 && value.Length > 0) _startedAt = _now();
        if (EmptyMessage.Length > 0 && value.Trim().Length > 0) EmptyMessage = "";
        Tick();
    }

    partial void OnEmptyMessageChanged(string value) => OnPropertyChanged(nameof(HasEmptyMessage));

    // ---- commands

    /// <summary>Sends the answer to the Coach. An empty answer is not sent: the box says what to do instead.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (_engine is null || !IsAnswering) return;
        await Composer.StopListeningAsync(); // the last words are put into the box before the answer is read
        StopSpeaking();
        if (_engine is null || !IsAnswering) return;
        var seconds = ElapsedSeconds;
        var method = Composer.InputMethod;
        var result = await RunAsync(() => _engine.SubmitAsync(AnswerText, method, seconds));
        if (result == SubmitResult.Empty)
        {
            EmptyMessage = EmptyAnswerMessage;
            return;
        }
        if (result == SubmitResult.Unchanged)
        {
            EmptyMessage = UnchangedAnswerMessage;
            return;
        }
        if (result == SubmitResult.Sent) FreezeTimer(seconds);
    }

    [RelayCommand(CanExecute = nameof(CanTryAgain))]
    private void TryAgain()
    {
        _engine?.TryAgain();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextAsync() => RunAsync(() => _engine!.NextAsync());

    [RelayCommand]
    private Task RetryAsync()
    {
        UnexpectedError = null;
        return RunAsync(() => _engine!.Phase == PracticePhase.Failed ? _engine.RetryAsync() : _engine.NextAsync());
    }

    /// <summary>After a failed Coach call: back to the box to change the answer before sending it again. The text is still there.</summary>
    [RelayCommand]
    private void EditAnswer()
    {
        _engine?.EditAnswer();
    }

    /// <summary>On a second or later try, puts the last answer back in the box as a starting point.</summary>
    [RelayCommand(CanExecute = nameof(CanUsePreviousAnswer))]
    private void UsePreviousAnswer()
    {
        Composer.SetText(_lastAnswerText, typed: true);
    }

    [RelayCommand]
    private void Exit()
    {
        _engine?.Cancel();
        StopVoice();
        ExitRequested?.Invoke();
    }

    private void OpenFollowUp(FollowUp followUp) => _engine?.AnswerFollowUp(followUp);

    // ---- plumbing

    // LlmExceptions are handled inside the engine (Failed phase + Retry). Anything else is a bug or an environment problem,
    // so show it in the same banner instead of letting an unobserved task swallow it.
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

    private async Task<SubmitResult?> RunAsync(Func<Task<SubmitResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            UnexpectedError = ex.Message;
            Refresh();
            return null;
        }
    }

    private void Refresh()
    {
        var item = _engine?.Current;
        var attempt = _engine?.AttemptNumber ?? 1;

        if (!ReferenceEquals(item, _shownItem))
        {
            // A new question (or a follow-up): an empty box and a fresh timer.
            _shownItem = item;
            _shownAttempt = attempt;
            _lastAnswerText = "";
            Composer.SetText("");
            EmptyMessage = "";
            ResetTimer();
            StopSpeaking();
            QuestionChanged?.Invoke();
            if (item is not null && IsAnswering && _settings.Current.SpeakQuestions) _ = SpeakQuestionAsync(automatic: true);
        }
        else if (attempt != _shownAttempt)
        {
            // Trying the same question again: an empty box, with the last answer one click away.
            _shownAttempt = attempt;
            Composer.SetText("");
            EmptyMessage = "";
            ResetTimer();
        }

        var latest = _engine?.LastAttempt;
        if (!ReferenceEquals(latest, _attemptSource))
        {
            _attemptSource = latest;
            if (latest is not null) _lastAnswerText = latest.AnswerText;
            Coach = latest is null
                ? null
                : new CoachOutputViewModel(latest.Coach, OpenFollowUp, _engine?.Current?.QuestionType, "Click one to answer it.",
                    _speech is null ? null : ReadAloudCommand);
        }

        if (!IsAnswering && IsListening) Composer.CloseMicrophone();
        if (_readingAnswer && !IsFeedback) StopSpeaking();

        OnPropertyChanged(string.Empty); // everything above is derived from the engine; refresh all bindings
        NextCommand.NotifyCanExecuteChanged();
        TryAgainCommand.NotifyCanExecuteChanged();
        UsePreviousAnswerCommand.NotifyCanExecuteChanged();
    }

    private void ResetTimer()
    {
        _startedAt = null;
        _frozenSeconds = 0;
    }

    private void FreezeTimer(int seconds)
    {
        _startedAt = null;
        _frozenSeconds = seconds;
    }

    private static string FormatTime(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    private static string Meta(int words, int seconds)
        => seconds > 0 ? $"{words} {(words == 1 ? "word" : "words")}  ·  {FormatTime(seconds)}" : $"{words} {(words == 1 ? "word" : "words")}";
}
