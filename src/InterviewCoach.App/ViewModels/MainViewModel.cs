using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;

namespace InterviewCoach.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private readonly SettingsViewModel _settings;
    private readonly LibraryViewModel? _library;
    private readonly ISettingsStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsSettingsSelected), nameof(IsLibrarySelected))]
    private object _currentPage;

    public MainViewModel(
        HomeViewModel home, SettingsViewModel settings, LearnViewModel learn, ISettingsStore store, LibraryViewModel? library = null,
        PracticeViewModel? practice = null)
    {
        _home = home;
        _settings = settings;
        _library = library;
        _store = store;
        _currentPage = home;
        store.Changed += (_, _) => OnPropertyChanged(nameof(IsDemoMode));

        home.LearnRequested += request =>
        {
            learn.Begin(request.Profile, request.Types, request.AnswerWords, request.Technologies, request.Employment);
            CurrentPage = learn;
        };
        learn.ExitRequested += () => CurrentPage = _home;
        if (practice is not null)
        {
            home.PracticeRequested += request =>
            {
                practice.Begin(request);
                CurrentPage = practice;
            };
            learn.TryItMyselfRequested += (request, question) =>
            {
                practice.BeginFrom(request, question);
                CurrentPage = practice;
            };
            practice.ExitRequested += () => CurrentPage = _home;
        }
        if (library is not null)
        {
            home.LibraryRequested += ShowLibrary;
            library.ExitRequested += () => CurrentPage = _home;
        }
    }

    public bool IsDemoMode => _store.Current.DemoMode;
    // A running session is part of the Home flow, so Home stays highlighted in the sidebar while one is open.
    public bool IsHomeSelected => !IsSettingsSelected && !IsLibrarySelected;
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
    private void ShowSettings()
    {
        _settings.Load(); // discard unsaved edits from a previous visit
        CurrentPage = _settings;
    }
}
