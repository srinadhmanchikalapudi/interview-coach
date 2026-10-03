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

/// <summary>One question of the interview on the debrief: the exchange, and the Coach's view of it once that is written.</summary>
public sealed partial class ThreadCardViewModel : ObservableObject
{
    private readonly MockThread _thread;
    private readonly Action<ThreadCardViewModel> _practice;
    private readonly Action<ThreadCardViewModel> _retry;
    private readonly Action<ThreadCardViewModel, FollowUp> _followUp;

    public ThreadCardViewModel(
        int number, MockThread thread, Action<ThreadCardViewModel> practice, Action<ThreadCardViewModel> retry, Action<ThreadCardViewModel, FollowUp> followUp)
    {
        Number = number;
        _thread = thread;
        _practice = practice;
        _retry = retry;
        _followUp = followUp;
        Turns = thread.Turns.Select(t => new ConversationLine(t.Speaker == MockSpeaker.Interviewer ? "Interviewer" : "You", t.Text, t.Speaker == MockSpeaker.Interviewer)).ToList();
        PracticeCommand = new RelayCommand(() => _practice(this));
        RetryCommand = new RelayCommand(() => _retry(this), () => IsFailed);
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

    [ObservableProperty] private CoachOutputViewModel? _coach;

    public IRelayCommand PracticeCommand { get; }
    public IRelayCommand RetryCommand { get; }

    /// <summary>Called when the engine changed: takes up the Coach's reply once it has arrived.</summary>
    public void Refresh()
    {
        if (_thread.Status == ThreadStatus.Done && _thread.Coach is { } coach && Coach is null)
            Coach = new CoachOutputViewModel(coach, f => _followUp(this, f), null, "Click one to practise it.");
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsCoaching));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(Error));
        RetryCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// The debrief screen (spec 4.3): the overall summary with a hire-signal badge, ratings for the focus areas, strengths, top fixes, what to
/// practise next, and one card per question with the Coach's view of the exchange. The cards fill in as their Coach calls finish.
/// </summary>
public sealed partial class DebriefViewModel : ObservableObject
{
    private readonly MockEngine _engine;
    private readonly MockSessionRequest _request;
    private readonly IDialogService? _dialogs;
    private readonly Func<DateTime> _now;
    private readonly DateTime _when;

    public DebriefViewModel(MockEngine engine, MockSessionRequest request, IDialogService? dialogs = null, Func<DateTime>? now = null)
    {
        _engine = engine;
        _request = request;
        _dialogs = dialogs;
        _now = now ?? (() => DateTime.UtcNow);
        _when = _now();

        var debrief = engine.Debrief ?? new DebriefDto();
        Summary = debrief.OverallSummary;
        SignalLabel = HireSignals.Label(debrief.HireSignal);
        Tone = HireSignals.Tone(debrief.HireSignal);
        Ratings = debrief.FocusAreaRatings.Select(r => new RatingRow(r.Name, HireSignals.RatingText(r.Rating), r.Evidence, r.Rating is >= 1 and <= 4, r.Rating ?? 0)).ToList();
        Strengths = debrief.Strengths;
        Fixes = debrief.TopFixes.Select(f => new FixRow(f.Fix, f.Example, f.HowToPractice)).ToList();
        PracticeNext = debrief.PracticeNext;
        Threads = new ObservableCollection<ThreadCardViewModel>(
            engine.Threads.Select((t, i) => new ThreadCardViewModel(i + 1, t, OnPractice, card => _ = RetryThreadAsync(card), OnFollowUp)));
        foreach (var card in Threads) card.Refresh();
        engine.Changed += OnEngineChanged;
    }

    /// <summary>Raised when the user wants to practise one of the questions: Practice opens on it (spec 4.3).</summary>
    public event Action<LearnSessionRequest, LearnItem>? PracticeRequested;

    /// <summary>Raised when the user goes back Home.</summary>
    public event Action? ExitRequested;

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

    public string RoleLine
    {
        get
        {
            var spent = _engine.ElapsedSeconds;
            return $"{_request.Profile.JobRole} · {_request.Round.Label()} · {_request.DurationMinutes} min planned · {spent / 60}:{spent % 60:00} spent";
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

    private Task RetryThreadAsync(ThreadCardViewModel card) => _engine.RetryThreadAsync(card.Thread);

    private static string TypeIdOf(MockThread thread)
        => QuestionTypes.All.Cast<QuestionType?>().FirstOrDefault(t => t!.Value.Id().Equals(thread.Phase?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id() ?? "";

    private LearnSessionRequest PracticeRequest() => new(_request.Profile, [], null, [], _request.Employment);

    private void OnPractice(ThreadCardViewModel card)
        => PracticeRequested?.Invoke(PracticeRequest(), new LearnItem { Question = card.Question, QuestionType = TypeIdOf(card.Thread), Source = "mock" });

    // A follow-up the Coach suggested becomes the next question in Practice, as it does there.
    private void OnFollowUp(ThreadCardViewModel card, FollowUp followUp)
        => PracticeRequested?.Invoke(PracticeRequest(), new LearnItem
        {
            Question = followUp.Question, QuestionType = TypeIdOf(card.Thread), Source = "mock", IsFollowUp = true, ParentQuestion = card.Question, Hint = followUp.Hint,
        });

    /// <summary>Writes the debrief to a Markdown file the user chooses.</summary>
    [RelayCommand]
    private void Export()
    {
        if (_engine.Debrief is not { } debrief) return;
        var name = $"mock-debrief-{_when:yyyy-MM-dd-HHmm}.md";
        var path = _dialogs?.PickSaveFile("Export debrief", "Markdown (*.md)|*.md|All files (*.*)|*.*", name);
        if (path is null) return;

        try
        {
            var markdown = DebriefMarkdown.Render(
                _request.Profile.JobRole, _request.Round, _request.DurationMinutes, _when.ToLocalTime(), _engine.ElapsedSeconds, debrief, _engine.Threads);
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
