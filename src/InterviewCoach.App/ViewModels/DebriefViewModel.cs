using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public record RatingRow(string Name, string RatingText, string Evidence, bool Covered, int Rating);

public record FixRow(string Fix, string Example, string HowToPractice)
{
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
    public bool HasHowToPractice => !string.IsNullOrWhiteSpace(HowToPractice);
}

/// <summary>
/// Everything a debrief screen shows, from a live interview or from a stored one. <see cref="Engine"/> is set for a live interview: its
/// coaching may still be arriving and a failed card can be retried. <see cref="Profile"/> is null when the stored interview's profile no
/// longer exists, in which case its questions cannot be practised.
/// </summary>
public sealed record DebriefSource(
    DebriefDto Debrief, IReadOnlyList<MockThread> Threads, string JobRole, RoundType Round, int DurationMinutes, int ElapsedSeconds, DateTime When,
    CandidateProfile? Profile, EmploymentType Employment, MockEngine? Engine = null);

/// <summary>One question of the interview on the debrief: the exchange, and the Coach's view of it once that is written.</summary>
public sealed partial class ThreadCardViewModel : ObservableObject
{
    private readonly MockThread _thread;
    private readonly Action<ThreadCardViewModel> _practice;
    private readonly Action<ThreadCardViewModel> _retry;
    private readonly Action<ThreadCardViewModel, FollowUp> _followUp;
    private readonly bool _canPractise;
    private readonly bool _canRetry;

    public ThreadCardViewModel(
        int number, MockThread thread, Action<ThreadCardViewModel> practice, Action<ThreadCardViewModel> retry, Action<ThreadCardViewModel, FollowUp> followUp,
        bool canPractise = true, bool canRetry = true)
    {
        Number = number;
        _thread = thread;
        _practice = practice;
        _retry = retry;
        _followUp = followUp;
        _canPractise = canPractise;
        _canRetry = canRetry;
        Turns = thread.Turns.Select(t => new ConversationLine(t.Speaker == MockSpeaker.Interviewer ? "Interviewer" : "You", t.Text, t.Speaker == MockSpeaker.Interviewer)).ToList();
        PracticeCommand = new RelayCommand(() => _practice(this), () => _canPractise);
        RetryCommand = new RelayCommand(() => _retry(this), () => IsFailed && _canRetry);
    }

    public int Number { get; }
    public MockThread Thread => _thread;
    public string Question => _thread.Question;
    public string Title => $"{Number}. {_thread.Question}";
    public string TypeLabel => QuestionTypes.LabelFor(_thread.Phase);
    public bool HasTypeLabel => TypeLabel.Length > 0;
    public IReadOnlyList<ConversationLine> Turns { get; }

    public ThreadStatus Status => _thread.Status;
    public bool IsCoaching => Status == ThreadStatus.Coaching;
    public bool IsFailed => Status == ThreadStatus.Failed;
    public bool IsNotAnswered => Status == ThreadStatus.NotAnswered;
    public string? Error => _thread.Error;

    /// <summary>False for a stored interview whose profile is gone: Practice needs a profile to coach against.</summary>
    public bool CanPractise => _canPractise;

    /// <summary>False for a stored interview: only a live one can ask the Coach again.</summary>
    public bool CanRetry => _canRetry;

    [ObservableProperty] private CoachOutputViewModel? _coach;

    public IRelayCommand PracticeCommand { get; }
    public IRelayCommand RetryCommand { get; }

    /// <summary>Called when the engine changed: takes up the Coach's reply once it has arrived.</summary>
    public void Refresh()
    {
        if (_thread.Status == ThreadStatus.Done && _thread.Coach is { } coach && Coach is null)
        {
            // Without a profile there is nothing to practise against, so the follow-ups are not offered as buttons that go nowhere.
            var shown = _canPractise ? coach : new CoachOutput
            {
                WhatTheyreTesting = coach.WhatTheyreTesting, Feedback = coach.Feedback, ModelAnswer = coach.ModelAnswer, Shape = coach.Shape,
                Delivery = coach.Delivery, FollowUps = [],
            };
            Coach = new CoachOutputViewModel(shown, f => _followUp(this, f), null, "Click one to practise it.");
        }
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsCoaching));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(Error));
        RetryCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// The debrief screen (spec 4.3): the overall summary with a hire-signal badge, ratings for the focus areas, strengths, top fixes, what to
/// practise next, and one card per question with the Coach's view of the exchange. For a live interview the cards fill in as their Coach
/// calls finish; for a stored one (from History) everything is already there.
/// </summary>
public sealed partial class DebriefViewModel : ObservableObject
{
    private readonly DebriefSource _source;
    private readonly IDialogService? _dialogs;

    public DebriefViewModel(MockEngine engine, MockSessionRequest request, IDialogService? dialogs = null, Func<DateTime>? now = null)
        : this(new DebriefSource(
            engine.Debrief ?? new DebriefDto(), engine.Threads, request.Profile.JobRole, request.Round, request.DurationMinutes, engine.ElapsedSeconds,
            (now ?? (() => DateTime.UtcNow))(), request.Profile, request.Employment, engine), dialogs)
    {
    }

    public DebriefViewModel(DebriefSource source, IDialogService? dialogs = null)
    {
        _source = source;
        _dialogs = dialogs;

        var debrief = source.Debrief;
        Summary = debrief.OverallSummary;
        SignalLabel = HireSignals.Label(debrief.HireSignal);
        Tone = HireSignals.Tone(debrief.HireSignal);
        Ratings = debrief.FocusAreaRatings.Select(r => new RatingRow(r.Name, HireSignals.RatingText(r.Rating), r.Evidence, r.Rating is >= 1 and <= 4, r.Rating ?? 0)).ToList();
        Strengths = debrief.Strengths;
        Fixes = debrief.TopFixes.Select(f => new FixRow(f.Fix, f.Example, f.HowToPractice)).ToList();
        PracticeNext = debrief.PracticeNext;
        Threads = new ObservableCollection<ThreadCardViewModel>(
            source.Threads.Select((t, i) => new ThreadCardViewModel(
                i + 1, t, OnPractice, card => _ = RetryThreadAsync(card), OnFollowUp, canPractise: source.Profile is not null, canRetry: source.Engine is not null)));
        foreach (var card in Threads) card.Refresh();
        if (source.Engine is not null) source.Engine.Changed += OnEngineChanged;
    }

    /// <summary>Raised when the user wants to practise one of the questions: Practice opens on it (spec 4.3).</summary>
    public event Action<LearnSessionRequest, LearnItem>? PracticeRequested;

    /// <summary>Raised when the user goes back (Home after an interview, History when it was opened from there).</summary>
    public event Action? ExitRequested;

    /// <summary>The text of the Back button.</summary>
    [ObservableProperty] private string _backLabel = "Back to Home";

    public string Summary { get; }
    public string SignalLabel { get; }
    public SignalTone Tone { get; }
    public bool IsPositive => Tone == SignalTone.Positive;
    public bool IsNegative => Tone == SignalTone.Negative;
    public bool IsNeutral => Tone == SignalTone.Neutral;

    public IReadOnlyList<RatingRow> Ratings { get; }
    public IReadOnlyList<string> Strengths { get; }
    public IReadOnlyList<FixRow> Fixes { get; }
    public IReadOnlyList<string> PracticeNext { get; }
    public ObservableCollection<ThreadCardViewModel> Threads { get; }

    public bool HasRatings => Ratings.Count > 0;
    public bool HasStrengths => Strengths.Count > 0;
    public bool HasFixes => Fixes.Count > 0;
    public bool HasPracticeNext => PracticeNext.Count > 0;
    public bool HasThreads => Threads.Count > 0;

    /// <summary>True for a stored interview whose profile has been deleted, so its questions cannot be practised.</summary>
    public bool ProfileMissing => _source.Profile is null;

    public string DateText => _source.When.ToLocalTime().ToString("d MMMM yyyy, HH:mm");

    public string RoleLine
    {
        get
        {
            var spent = _source.Engine?.ElapsedSeconds ?? _source.ElapsedSeconds;
            return $"{_source.JobRole} · {_source.Round.Label()} · {_source.DurationMinutes} min planned · {spent / 60}:{spent % 60:00} spent";
        }
    }

    /// <summary>True while some questions are still being coached, so the cards are not all filled in yet.</summary>
    public bool IsStillCoaching => Threads.Any(t => t.IsCoaching);

    [ObservableProperty] private string _exportStatus = "";

    partial void OnExportStatusChanged(string value) => OnPropertyChanged(nameof(HasExportStatus));

    public bool HasExportStatus => ExportStatus.Length > 0;

    private void OnEngineChanged()
    {
        foreach (var card in Threads) card.Refresh();
        OnPropertyChanged(nameof(IsStillCoaching));
    }

    private Task RetryThreadAsync(ThreadCardViewModel card) => _source.Engine?.RetryThreadAsync(card.Thread) ?? Task.CompletedTask;

    private static string TypeIdOf(MockThread thread)
        => QuestionTypes.All.Cast<QuestionType?>().FirstOrDefault(t => t!.Value.Id().Equals(thread.Phase?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id() ?? "";

    private LearnSessionRequest? PracticeRequest() => _source.Profile is null ? null : new(_source.Profile, [], null, [], _source.Employment);

    private void OnPractice(ThreadCardViewModel card)
    {
        if (PracticeRequest() is not { } request) return;
        PracticeRequested?.Invoke(request, new LearnItem { Question = card.Question, QuestionType = TypeIdOf(card.Thread), Source = "mock" });
    }

    // A follow-up the Coach suggested becomes the next question in Practice, as it does there.
    private void OnFollowUp(ThreadCardViewModel card, FollowUp followUp)
    {
        if (PracticeRequest() is not { } request) return;
        PracticeRequested?.Invoke(request, new LearnItem
        {
            Question = followUp.Question, QuestionType = TypeIdOf(card.Thread), Source = "mock", IsFollowUp = true, ParentQuestion = card.Question, Hint = followUp.Hint,
        });
    }

    /// <summary>Writes the debrief to a Markdown file the user chooses.</summary>
    [RelayCommand]
    private void Export()
    {
        var name = $"mock-debrief-{_source.When.ToLocalTime():yyyy-MM-dd-HHmm}.md";
        var path = _dialogs?.PickSaveFile("Export debrief", "Markdown (*.md)|*.md|All files (*.*)|*.*", name);
        if (path is null) return;

        try
        {
            var markdown = DebriefMarkdown.Render(
                _source.JobRole, _source.Round, _source.DurationMinutes, _source.When.ToLocalTime(), _source.Engine?.ElapsedSeconds ?? _source.ElapsedSeconds,
                _source.Debrief, _source.Threads);
            File.WriteAllText(path, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ExportStatus = IsStillCoaching
                ? $"Saved to {path}. Some questions were still being coached, so they say so in the file."
                : $"Saved to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportStatus = $"Could not save the file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Back() => ExitRequested?.Invoke();
}
