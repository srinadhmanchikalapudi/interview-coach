using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;

namespace InterviewCoach.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private readonly SettingsViewModel _settings;
    private readonly LibraryViewModel? _library;
    private readonly ConceptsViewModel? _concepts;
    private object _sessionOrigin;
    private readonly ISettingsStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsSettingsSelected), nameof(IsLibrarySelected), nameof(IsConceptsSelected))]
    private object _currentPage;

    public MainViewModel(
        HomeViewModel home, SettingsViewModel settings, LearnViewModel learn, ISettingsStore store, LibraryViewModel? library = null,
        PracticeViewModel? practice = null, ConceptsViewModel? concepts = null, MockViewModel? mock = null)
    {
        _concepts = concepts;
        _home = home;
        _settings = settings;
        _library = library;
        _store = store;
        _currentPage = home;
        _sessionOrigin = home;
        store.Changed += (_, _) => OnPropertyChanged(nameof(IsDemoMode));

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
            mock.ExitRequested += () => CurrentPage = _home;
            mock.DebriefReady += debrief =>
            {
                debrief.ExitRequested += () => CurrentPage = _home;
                if (practice is not null)
                    debrief.PracticeRequested += (request, item) =>
                    {
                        _sessionOrigin = debrief; // leaving Practice returns to the debrief
                        practice.BeginFrom(request, item);
                        CurrentPage = practice;
                    };
                CurrentPage = debrief;
            };
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
    }

    // Leaving a page for another one stops what it was doing: a mock interview ends its voice and its microphone, and Practice stops talking.
    partial void OnCurrentPageChanged(object? oldValue, object newValue)
    {
        if (ReferenceEquals(oldValue, newValue)) return;
        if (oldValue is MockViewModel mock && newValue is not DebriefViewModel) mock.Abandon();
        else if (oldValue is PracticeViewModel practice) practice.Leave();
    }

    public bool IsDemoMode => _store.Current.DemoMode;
    // A running session is part of the Home flow, so Home stays highlighted in the sidebar while one is open.
    public bool IsHomeSelected => !IsSettingsSelected && !IsLibrarySelected && !IsConceptsSelected;
    public bool IsConceptsSelected => _concepts is not null && ReferenceEquals(CurrentPage, _concepts);
    public bool HasConcepts => _concepts is not null;
    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, _settings);
    public bool IsLibrarySelected => _library is not null && ReferenceEquals(CurrentPage, _library);
    public bool HasLibrary => _library is not null;

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
