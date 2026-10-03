using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public enum NoticeLevel { Info, Warning, Error }

/// <summary>A message shown in a bar above the page: what happened, and optionally a button that does something about it.</summary>
public sealed record AppNotice(string Text, NoticeLevel Level, string? ActionLabel = null, Action? Action = null);

public partial class MainViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private readonly SettingsViewModel _settings;
    private readonly LibraryViewModel? _library;
    private readonly ConceptsViewModel? _concepts;
    private readonly HistoryViewModel? _history;
    private object _sessionOrigin;
    private object? _debriefBack;
    private readonly ISettingsStore _store;
    private AppNotice? _transient;
    private bool _setupNoticeDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsSettingsSelected), nameof(IsLibrarySelected), nameof(IsConceptsSelected), nameof(IsHistorySelected), nameof(Notice), nameof(HasNotice))]
    private object _currentPage;

    [ObservableProperty] private bool _showShortcuts;

    public MainViewModel(
        HomeViewModel home, SettingsViewModel settings, LearnViewModel learn, ISettingsStore store, LibraryViewModel? library = null,
        PracticeViewModel? practice = null, ConceptsViewModel? concepts = null, MockViewModel? mock = null, HistoryViewModel? history = null)
    {
        _concepts = concepts;
        _history = history;
        _home = home;
        _settings = settings;
        _library = library;
        _store = store;
        _currentPage = home;
        _sessionOrigin = home;
        store.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDemoMode));
            OnPropertyChanged(nameof(Notice));
            OnPropertyChanged(nameof(HasNotice));
        };

        home.LearnRequested += request =>
        {
            _sessionOrigin = _home;
            learn.Begin(request.Profile, request.Types, request.AnswerWords, request.Technologies, request.Employment);
            CurrentPage = learn;
        };
        // A session returns to the page that started it: Home, or Concepts.
        learn.ExitRequested += () => CurrentPage = _sessionOrigin;
        if (practice is not null)
        {
            home.PracticeRequested += request =>
            {
                _sessionOrigin = _home;
                practice.Begin(request);
                CurrentPage = practice;
            };
            learn.TryItMyselfRequested += (request, question) =>
            {
                practice.BeginFrom(request, question);
                CurrentPage = practice;
            };
            practice.ExitRequested += () => CurrentPage = _sessionOrigin;
        }
        if (mock is not null)
        {
            home.MockRequested += request =>
            {
                _sessionOrigin = _home;
                mock.Begin(request);
                CurrentPage = mock;
            };
            void Resume(MockSessionRequest request, MockRecord record)
            {
                _sessionOrigin = _home;
                mock.Resume(request, record);
                CurrentPage = mock;
            }
            home.MockResumeRequested += Resume;
            if (history is not null) history.ResumeRequested += Resume;
            mock.ExitRequested += () => CurrentPage = _home;
            mock.DebriefReady += debrief => ShowDebrief(debrief, _home);
        }
        if (history is not null)
        {
            history.OpenRequested += debrief => ShowDebrief(debrief, history);
            if (library is not null) history.LibraryRequested += ShowLibrary;
        }
        if (concepts is not null)
        {
            concepts.LearnRequested += request =>
            {
                _sessionOrigin = concepts;
                learn.Begin(request.Profile, request.Types, request.AnswerWords, request.Technologies, request.Employment);
                CurrentPage = learn;
            };
            if (practice is not null)
                concepts.PracticeRequested += request =>
                {
                    _sessionOrigin = concepts;
                    practice.Begin(request);
                    CurrentPage = practice;
                };
        }
        if (library is not null)
        {
            home.LibraryRequested += ShowLibrary;
            library.ExitRequested += () => CurrentPage = _home;
        }

        // A debrief is shown after an interview (Back goes Home) or opened from History (Back goes there). Practising one of its
        // questions returns to it afterwards.
        void ShowDebrief(DebriefViewModel debrief, object back)
        {
            debrief.ExitRequested += () => CurrentPage = back;
            if (practice is not null)
                debrief.PracticeRequested += (request, item) =>
                {
                    _sessionOrigin = debrief; // leaving Practice returns to the debrief
                    practice.BeginFrom(request, item);
                    CurrentPage = practice;
                };
            _debriefBack = back;
            CurrentPage = debrief;
        }
    }

    // Leaving a page for another one stops what it was doing: a mock interview ends its voice and its microphone (what was said is kept, so it
    // can be resumed), and Practice stops talking. Home and History look for what changed while they were away.
    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        if (ReferenceEquals(oldValue, newValue)) return;
        ShowShortcuts = false;
        if (oldValue is MockViewModel mock && newValue is not DebriefViewModel) mock.Abandon();
        else if (oldValue is PracticeViewModel practice) practice.Leave();

        if (newValue is HomeViewModel home) _ = home.RefreshMockStatusAsync();
        else if (newValue is HistoryViewModel history) _ = history.LoadAsync();
    }

    public bool IsDemoMode => _store.Current.DemoMode;
    // A running session is part of the Home flow, so Home stays highlighted in the sidebar while one is open.
    public bool IsHomeSelected => !IsSettingsSelected && !IsLibrarySelected && !IsConceptsSelected && !IsHistorySelected;
    public bool IsConceptsSelected => _concepts is not null && ReferenceEquals(CurrentPage, _concepts);
    public bool HasConcepts => _concepts is not null;
    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, _settings);
    public bool IsLibrarySelected => _library is not null && ReferenceEquals(CurrentPage, _library);
    public bool HasLibrary => _library is not null;
    public bool IsHistorySelected => _history is not null && (ReferenceEquals(CurrentPage, _history) || (CurrentPage is DebriefViewModel && ReferenceEquals(_debriefBack, _history)));
    public bool HasHistory => _history is not null;

    // ---- the bar above the page

    /// <summary>What to tell the user above the page, or null: a problem that was reported, else a missing key (not on Settings itself).</summary>
    public AppNotice? Notice => _transient ?? SetupNotice();

    public bool HasNotice => Notice is not null;

    private AppNotice? SetupNotice()
    {
        var s = _store.Current;
        if (_setupNoticeDismissed || s.DemoMode || s.HasLlmCredentials || IsSettingsSelected) return null;
        var provider = s.Provider switch
        {
            LlmProvider.Anthropic => "Anthropic",
            LlmProvider.OpenRouter => "OpenRouter",
            _ => "OpenAI-compatible",
        };
        return new AppNotice(
            $"No {provider} key yet. Add one in Settings to start a session, or turn on Demo mode to look around without one.",
            NoticeLevel.Warning, "Open Settings", () => ShowSettings());
    }

    /// <summary>Shows a problem above the page instead of in a dialog. It stays until it is dismissed.</summary>
    public void ReportError(string message)
    {
        _transient = new AppNotice(message, NoticeLevel.Error);
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    [RelayCommand]
    private void DismissNotice()
    {
        if (_transient is not null) _transient = null;
        else _setupNoticeDismissed = true;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    [RelayCommand]
    private void RunNoticeAction() => Notice?.Action?.Invoke();

    // ---- keyboard shortcuts

    [RelayCommand]
    private void ToggleShortcuts() => ShowShortcuts = !ShowShortcuts;

    [RelayCommand]
    private void CloseShortcuts() => ShowShortcuts = false;

    // ---- navigation

    [RelayCommand]
    private void ShowHome() => CurrentPage = _home;

    [RelayCommand]
    private void ShowLibrary()
    {
        if (_library is null) return;
        CurrentPage = _library;
        _ = _library.LoadAsync(); // always read it fresh: new questions were added by the last session
    }

    [RelayCommand]
    private void ShowHistory()
    {
        if (_history is null) return;
        CurrentPage = _history; // OnCurrentPageChanged reads the list fresh
    }

    [RelayCommand]
    private void ShowConcepts()
    {
        if (_concepts is null) return;
        _concepts.SuggestRole(_home.JobRole); // the saved profile's role, when nothing was chosen here yet
        CurrentPage = _concepts;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        _settings.Load(); // discard unsaved edits from a previous visit
        CurrentPage = _settings;
    }
}
