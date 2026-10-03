using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

/// <summary>One line of the conversation list.</summary>
public record ConversationLine(string Speaker, string Text, bool IsInterviewer);

/// <summary>
/// The Mock Interview screen (spec 4.2): the interviewer's line (spoken, and shown unless audio only), an answer box with a microphone, a
/// status pill and the time, End interview, and the conversation so far. No feedback of any kind appears here: coaching starts once
/// the round has ended, on the debrief.
/// </summary>
public partial class MockViewModel : ObservableObject
{
    public const string EmptyAnswerMessage = "Say or type something first. 'I don't know' is a fine answer too.";
    public const string AudioOnlyText = "(audio only)";

    private readonly ILlmService _llm;
    private readonly IPromptLibrary _prompts;
    private readonly ISettingsStore _settings;
    private readonly ISpeechFactory? _speech;
    private readonly IDialogService? _dialogs;
    private readonly IMockHistory? _history;
    private readonly Func<DateTime> _now;
    private readonly Speaker? _speaker;

    private MockEngine? _engine;
    private MockSessionRequest? _request;
    private int _spokenTurns;
    private bool _debriefRaised;
    private DateTime? _answerStartedAt;

    public MockViewModel(
        ILlmService llm, IPromptLibrary prompts, ISettingsStore settings, ISpeechFactory? speech = null, IDialogService? dialogs = null,
        IMockHistory? history = null, Func<DateTime>? now = null)
    {
        _llm = llm;
        _prompts = prompts;
        _settings = settings;
        _speech = speech;
        _dialogs = dialogs;
        _history = history;
        _now = now ?? (() => DateTime.UtcNow);
        _speaker = speech is null ? null : new Speaker(speech);
        if (_speaker is not null) _speaker.Changed += () => OnPropertyChanged(nameof(IsSpeaking));

        Composer = new AnswerComposer(speech, settings, _now, () => _engine is { } e && (e.Phase == MockPhase.CandidateAnswering || (e.Phase == MockPhase.InterviewerSpeaking && !e.IsClosing)));
        Composer.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(AnswerComposer.IsListening)) OnPropertyChanged(nameof(StatusText)); // Listening or Your turn
        };
        Composer.TextChanged += value =>
        {
            // The answer timer starts at the first keystroke or the first use of the microphone (spec 4.6).
            if (_answerStartedAt is null && value.Length > 0 && _engine?.Phase == MockPhase.CandidateAnswering) _answerStartedAt = _now();
            if (EmptyMessage.Length > 0 && value.Trim().Length > 0) EmptyMessage = "";
        };
        Composer.MicStarted += () =>
        {
            _speaker?.Stop(); // talking over the interviewer ends their line: barge-in
            _answerStartedAt ??= _now();
        };
        Composer.SubmitRequested += () => _ = SubmitCommand.ExecuteAsync(null);
    }

    /// <summary>Raised once, when the debrief has been written, with the screen to show.</summary>
    public event Action<DebriefViewModel>? DebriefReady;

    /// <summary>Raised when the user leaves the screen without a debrief (a failed start that is given up on).</summary>
    public event Action? ExitRequested;

    /// <summary>The answer box and its microphone (shared with Practice).</summary>
    public AnswerComposer Composer { get; }

    public ObservableCollection<ConversationLine> Conversation { get; } = [];

    [ObservableProperty] private string _emptyMessage = "";
    [ObservableProperty] private string? _unexpectedError;
    [ObservableProperty] private bool _showQuestionText = true;
    [ObservableProperty] private string _speechNotice = "";

    // ---- starting

    /// <summary>Starts a mock interview for a saved profile: plans the round, then the interviewer speaks first.</summary>
    public void Begin(MockSessionRequest request)
        => Start(request, engine => engine.StartAsync(request.Profile, request.Round, request.DurationMinutes, request.Employment));

    /// <summary>Picks a stored interview up where it stopped, or writes the debrief of one that ended without it.</summary>
    public void Resume(MockSessionRequest request, MockRecord record)
        => Start(request, engine => engine.ResumeAsync(request.Profile, record));

    private void Start(MockSessionRequest request, Func<MockEngine, Task> begin)
    {
        Abandon();
        _request = request;
        _engine = new MockEngine(_llm, _prompts, _now, _history);
        _engine.Changed += OnEngineChanged;
        _spokenTurns = 0;
        _debriefRaised = false;
        _answerStartedAt = null;
        UnexpectedError = null;
        EmptyMessage = "";
        SpeechNotice = "";
        Conversation.Clear();
        Composer.SetText("");
        ShowQuestionText = request.ShowQuestionText;
        Refresh();
        var engine = _engine;
        _ = RunAsync(() => begin(engine));
    }

    // ---- what is on screen

    public MockPhase Phase => _engine?.Phase ?? MockPhase.Setup;
    public string RoleLine => _request is null ? "" : $"{_request.Profile.JobRole} · {_request.Round.Label()} · {_request.DurationMinutes} min";

    public bool IsPlanning => Phase == MockPhase.Planning;
    public bool IsAnswering => Phase == MockPhase.CandidateAnswering;
    public bool IsClosing => _engine?.IsClosing == true;
    public bool IsWrappingUp => Phase is MockPhase.Ending or MockPhase.Debriefing;
    public bool IsFailed => Phase == MockPhase.Failed || UnexpectedError is not null;
    public string ErrorText => UnexpectedError ?? _engine?.Error ?? "";

    /// <summary>True when the failure is in the debrief, so the interview itself is over and nothing is lost.</summary>
    public bool FailedAfterInterview => Phase == MockPhase.Failed && _engine?.HasEnded == true;

    public bool CanEnd => _engine?.CanEnd == true;
    public bool CanSubmit => IsAnswering;

    /// <summary>The answer box is live while it is the candidate's turn, and while the interviewer is still speaking (talking over them is allowed).</summary>
    public bool CanAnswerNow => IsAnswering || (Phase == MockPhase.InterviewerSpeaking && !IsClosing);

    /// <summary>The pill at the top: Speaking, Listening, Thinking or Your turn.</summary>
    public string StatusText => Phase switch
    {
        MockPhase.Planning => "Preparing",
        MockPhase.InterviewerThinking => "Thinking",
        MockPhase.InterviewerSpeaking => IsSpeaking ? "Speaking" : "Reading",
        MockPhase.CandidateAnswering => Composer.IsListening ? "Listening" : "Your turn",
        MockPhase.Ending or MockPhase.Debriefing => "Wrapping up",
        MockPhase.Done => "Finished",
        MockPhase.Failed => "Problem",
        _ => "",
    };

    public bool IsSpeaking => _speaker?.IsSpeaking == true;

    public int ElapsedSeconds => _engine?.ElapsedSeconds ?? 0;
    public string ElapsedText => $"{Format(ElapsedSeconds)} / {Format((_request?.DurationMinutes ?? 0) * 60)}";
    public bool IsOvertime => _engine?.IsOvertime == true;

    /// <summary>True once the round is over its length: the interviewer will wrap up soon.</summary>
    public string OvertimeText => IsOvertime ? "Over time: the interviewer will close the round soon." : "";

    // The interviewer's words: shown unless audio only. Audio only needs a voice that works, so without one the text is shown anyway.
    private bool CanHearInterviewer => _speaker is not null && _speaker.Readiness.IsReady;
    public bool IsLineHidden => !ShowQuestionText && CanHearInterviewer;
    public string InterviewerLine => Phase == MockPhase.Planning || _engine is null || _engine.Turns.Count == 0
        ? ""
        : IsLineHidden ? AudioOnlyText : _engine.CurrentLine;
    public bool HasInterviewerLine => InterviewerLine.Length > 0;
    public bool CanRepeat => _speaker is not null && _engine is { Phase: MockPhase.CandidateAnswering or MockPhase.InterviewerSpeaking } && _engine.Turns.Count > 0;
    public bool HasSpeech => Composer.HasSpeech;

    public string AnswerText
    {
        get => Composer.AnswerText;
        set => Composer.AnswerText = value;
    }

    public string WordCountText => Composer.WordCountText;
    public bool HasEmptyMessage => EmptyMessage.Length > 0;
    public string AnswerTimerText => _answerStartedAt is { } start ? Format(Math.Max(0, (int)(_now() - start).TotalSeconds)) : "0:00";
    public bool HasSpeechNotice => SpeechNotice.Length > 0;

    partial void OnEmptyMessageChanged(string value) => OnPropertyChanged(nameof(HasEmptyMessage));
    partial void OnUnexpectedErrorChanged(string? value) => Refresh();
    partial void OnSpeechNoticeChanged(string value) => OnPropertyChanged(nameof(HasSpeechNotice));
    partial void OnShowQuestionTextChanged(bool value) => Refresh();

    /// <summary>Called about once a second by the view: the clock, the answer timer, and the pause check for auto-submit.</summary>
    public void Tick()
    {
        OnPropertyChanged(nameof(ElapsedSeconds));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(IsOvertime));
        OnPropertyChanged(nameof(OvertimeText));
        OnPropertyChanged(nameof(AnswerTimerText));
        Composer.CheckSilence();
    }

    // ---- commands

    /// <summary>Sends the answer. An empty answer is not sent: the box says what to do instead.</summary>
    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (_engine is null || !IsAnswering) return;
        await Composer.StopListeningAsync(); // the last words are put into the box before the answer is read
        _speaker?.Stop();
        if (_engine is null || !IsAnswering) return;

        var text = AnswerText.Trim();
        if (text.Length == 0)
        {
            EmptyMessage = EmptyAnswerMessage;
            return;
        }

        var seconds = _answerStartedAt is { } start ? Math.Max(0, (int)(_now() - start).TotalSeconds) : 0;
        var method = Composer.InputMethod;
        _answerStartedAt = null;
        Composer.SetText(""); // the answer is on its way; it stays in the conversation and in the engine if the next turn fails
        await RunAsync(() => _engine.SubmitAnswerAsync(text, method, seconds));
    }

    /// <summary>Ends the interview after a confirmation. No closing line is spoken; the debrief starts at once.</summary>
    [RelayCommand(CanExecute = nameof(CanEnd))]
    private async Task EndInterviewAsync()
    {
        if (_engine is null || !CanEnd) return;
        if (_dialogs is not null && !_dialogs.Confirm("End the interview", "End the interview now? You will get your debrief for what has been covered so far."))
            return;
        if (_engine is null || !_engine.CanEnd) return; // it ended on its own while the question was open

        _speaker?.Stop();
        await Composer.StopListeningAsync();
        await RunAsync(() => _engine.EndNowAsync());
    }

    /// <summary>Says the interviewer's last line again.</summary>
    [RelayCommand(CanExecute = nameof(CanRepeat))]
    private async Task RepeatAsync()
    {
        if (_speaker is null || _engine is null) return;
        var ready = _speaker.Readiness;
        if (!ready.IsReady)
        {
            SpeechNotice = ready.Message;
            return;
        }
        var outcome = await _speaker.SpeakAsync(_engine.CurrentLine);
        if (outcome.Error is { } error) SpeechNotice = error + " The text is on screen.";
    }

    /// <summary>After a failed step: tries it again. The conversation so far is kept.</summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        UnexpectedError = null;
        return RunAsync(() => _engine!.RetryAsync());
    }

    [RelayCommand]
    private void Exit()
    {
        Abandon();
        ExitRequested?.Invoke();
    }

    /// <summary>Leaving the screen for good (another page was opened): everything stops, the microphone closes and nothing keeps talking.</summary>
    public void Abandon()
    {
        _engine?.Cancel();
        _speaker?.Stop();
        Composer.CloseMicrophone();
    }

    // ---- reacting to the engine

    private void OnEngineChanged() => Refresh();

    private void Refresh()
    {
        if (_engine is null) return;

        // Every interviewer turn is added to the conversation once; the newest is spoken.
        foreach (var turn in _engine.Turns.Skip(Conversation.Count))
            Conversation.Add(new ConversationLine(turn.Speaker == MockSpeaker.Interviewer ? "Interviewer" : "You", turn.Text, turn.Speaker == MockSpeaker.Interviewer));

        OnPropertyChanged(string.Empty);
        EndInterviewCommand.NotifyCanExecuteChanged();
        RepeatCommand.NotifyCanExecuteChanged();

        if (_engine.Phase == MockPhase.InterviewerSpeaking && _engine.Turns.Count > _spokenTurns)
        {
            _spokenTurns = _engine.Turns.Count;
            _ = SpeakThenListenAsync();
        }

        if (_engine.Phase == MockPhase.Done && !_debriefRaised)
        {
            _debriefRaised = true;
            Composer.CloseMicrophone();
            _speaker?.Stop();
            DebriefReady?.Invoke(new DebriefViewModel(_engine, _request!, _dialogs, _now));
        }
    }

    // The interviewer's line is spoken; when it ends (or the candidate talks over it), it is the candidate's turn, and the microphone
    // opens by itself when auto-listen is on and dictation is set up. The closing line is spoken too, and then the round ends.
    private async Task SpeakThenListenAsync()
    {
        var engine = _engine;
        if (engine is null) return;
        var turnCount = engine.Turns.Count;
        var closing = engine.IsClosing;

        try
        {
            if (_speaker is not null && _speaker.Readiness.IsReady)
            {
                var outcome = await _speaker.SpeakAsync(engine.CurrentLine);
                if (outcome.Error is { } error) SpeechNotice = error + " The text is on screen.";
            }
            else if (_speaker is not null)
            {
                SpeechNotice = _speaker.Readiness.Message;
            }

            if (!ReferenceEquals(engine, _engine) || engine.Phase != MockPhase.InterviewerSpeaking || engine.Turns.Count != turnCount) return;
            await engine.FinishedSpeakingAsync();
        }
        catch (Exception ex)
        {
            UnexpectedError = ex.Message;
            return;
        }

        if (!closing && ReferenceEquals(engine, _engine) && engine.Phase == MockPhase.CandidateAnswering && _settings.Current.AutoListen
            && !Composer.IsListening && AnswerText.Trim().Length == 0)
            await Composer.StartListeningAsync(manual: false);
    }

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
        }
    }

    private static string Format(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
}
