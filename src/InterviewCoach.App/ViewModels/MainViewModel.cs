using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;

namespace InterviewCoach.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly HomeViewModel _home;
    private readonly SettingsViewModel _settings;
    private readonly ISettingsStore _store;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomeSelected), nameof(IsSettingsSelected))]
    private object _currentPage;

    public MainViewModel(HomeViewModel home, SettingsViewModel settings, LearnViewModel learn, ISettingsStore store)
    {
        _home = home;
        _settings = settings;
        _store = store;
        _currentPage = home;
        store.Changed += (_, _) => OnPropertyChanged(nameof(IsDemoMode));

        home.LearnRequested += request =>
        {
            learn.Begin(request.Profile, request.Types, request.AnswerWords, request.Technologies, request.Employment);
            CurrentPage = learn;
        };
        learn.ExitRequested += () => CurrentPage = _home;
    }

    public bool IsDemoMode => _store.Current.DemoMode;
    // A running session is part of the Home flow, so Home stays highlighted in the sidebar while one is open.
    public bool IsHomeSelected => !IsSettingsSelected;
    public bool IsSettingsSelected => ReferenceEquals(CurrentPage, _settings);

    [RelayCommand]
    private void ShowHome() => CurrentPage = _home;

    [RelayCommand]
    private void ShowSettings()
    {
        _settings.Load(); // discard unsaved edits from a previous visit
        CurrentPage = _settings;
    }
}
