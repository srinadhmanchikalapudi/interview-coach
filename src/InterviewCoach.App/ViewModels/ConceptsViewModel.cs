using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

/// <summary>One choice in the Difficulty control.</summary>
public partial class DifficultyOption(Difficulty difficulty, Action<DifficultyOption> selected) : ObservableObject
{
    public Difficulty Difficulty { get; } = difficulty;
    public string Label { get; } = difficulty.Label();

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>
/// Technology concepts: pick a job role, tick the technologies it uses (or type your own), choose Beginner, Medium or Advanced, and get
/// concept questions about those technologies. There is no resume or job description. The technologies of a role are asked of the model
/// once and saved, so the same role costs nothing the next time.
/// </summary>
public partial class ConceptsViewModel : ObservableObject
{
    private const int MaxOtherNameLength = 60;

    private readonly TechBank? _bank;
    private readonly ISettingsStore? _settings;

    private int _load;
    private string _loadedRoleKey = "";
    private bool _syncingDifficulty;
    private HashSet<string> _carried = new(StringComparer.OrdinalIgnoreCase); // ticks kept across a change of role
    private bool _ready; // nothing is saved while the remembered choices are being loaded

    public ConceptsViewModel(TechBank? bank = null, ISettingsStore? settings = null)
    {
        _bank = bank;
        _settings = settings;

        DifficultyOptions = new ObservableCollection<DifficultyOption>(Difficulties.All.Select(d => new DifficultyOption(d, OnDifficultySelected)));
        Role = settings?.Current.ConceptRole ?? "";
        Difficulty = settings?.Current.ConceptDifficulty ?? Difficulty.Medium;
        SyncDifficultyOptions(); // the property may not have changed from its default, so mark the chosen chip explicitly
        _ready = true;
    }

    /// <summary>Raised by Start in Learn mode: a made-up profile for the role, technical-concept questions, and the technologies ticked.</summary>
    public event Action<LearnSessionRequest>? LearnRequested;

    /// <summary>Raised by Start in Practice mode with the same details.</summary>
    public event Action<LearnSessionRequest>? PracticeRequested;

    /// <summary>The technology bank is what remembers the role lists and holds the questions, so the page needs it.</summary>
    public bool IsAvailable => _bank is not null;

    // ---- the job role and its technologies

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanShowTechnologies))] private string _role = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _loadFailed;

    public ObservableCollection<TechnologyOption> Technologies { get; } = [];
    public bool HasTechnologies => Technologies.Count > 0;
    public bool CanShowTechnologies => IsAvailable && TechBank.RoleKey(Role).Length > 0 && !IsLoading;

    // "Other": technologies the list does not have, typed in by hand.
    [ObservableProperty] private bool _otherTechnologyChecked;
    [ObservableProperty] private string _otherTechnologies = "";

    public IReadOnlyList<string> OtherTechnologyNames => OtherTechnologies
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => n.Length > MaxOtherNameLength ? n[..MaxOtherNameLength].TrimEnd() : n)
        .DistinctBy(n => n.ToLowerInvariant())
        .Take(TechBank.MaxTechnologies)
        .ToList();

    /// <summary>Every technology to ask about: the ticked ones plus any typed under Other, without duplicates.</summary>
    public IReadOnlyList<string> SelectedTechnologies
    {
        get
        {
            var names = Technologies.Where(o => o.IsChecked).Select(o => o.Name).ToList();
            if (OtherTechnologyChecked) names.AddRange(OtherTechnologyNames);
            return names.DistinctBy(n => n.ToLowerInvariant()).ToList();
        }
    }

    partial void OnRoleChanged(string value)
    {
        // A different role has a different list: the old one is dropped (what was ticked stays ticked if the new list has it too).
        if (_loadedRoleKey.Length > 0 && TechBank.RoleKey(value) != _loadedRoleKey)
        {
            _loadedRoleKey = "";
            _carried.UnionWith(Technologies.Where(o => o.IsChecked).Select(o => o.Name));
            SetTechnologies([], TechBank.RoleKey(value).Length == 0 ? "" : "Click Show technologies to see what this role uses.");
        }
        RefreshStartState();
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanShowTechnologies));
        ShowTechnologiesCommand.NotifyCanExecuteChanged();
        RefreshTechnologiesCommand.NotifyCanExecuteChanged();
        RefreshStartState();
    }

    partial void OnOtherTechnologiesChanged(string value)
    {
        // Typing a name means it is wanted: tick Other for the user.
        if (!OtherTechnologyChecked && !string.IsNullOrWhiteSpace(value)) OtherTechnologyChecked = true;
        RefreshStartState();
    }

    partial void OnOtherTechnologyCheckedChanged(bool value) => RefreshStartState();

    private void OnTechnologyOptionChanged(TechnologyOption _) => RefreshStartState();

    /// <summary>
    /// Called when the page opens. Fills the role from the saved profile when nothing was chosen yet, then shows the technologies already
    /// saved for the role. Never calls the model: that happens only when the user clicks Show technologies.
    /// </summary>
    public void SuggestRole(string? role)
    {
        if (TechBank.RoleKey(Role).Length == 0 && !string.IsNullOrWhiteSpace(role)) Role = role.Trim();
        _ = LoadSavedAsync();
    }

    private async Task LoadSavedAsync()
    {
        if (_bank is null || TechBank.RoleKey(Role).Length == 0 || HasTechnologies || IsLoading) return;
        var role = Role;
        IReadOnlyList<string>? saved;
        try
        {
            saved = await _bank.GetSavedRoleTechnologiesAsync(role);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        {
            return; // the saved list is a convenience; the button still works
        }
        if (saved is null || HasTechnologies || IsLoading || TechBank.RoleKey(Role) != TechBank.RoleKey(role)) return;
        _loadedRoleKey = TechBank.RoleKey(role);
        SetTechnologies(saved, $"Saved list for {role.Trim()}. No model call was needed.");
    }

    /// <summary>Shows the technologies of the role: the saved list if there is one, otherwise the model is asked once and the answer saved.</summary>
    [RelayCommand(CanExecute = nameof(CanShowTechnologies))]
    private Task ShowTechnologiesAsync() => FetchAsync(refresh: false);

    /// <summary>Asks the model again and replaces the saved list, for a list that looks wrong or old.</summary>
    [RelayCommand(CanExecute = nameof(CanShowTechnologies))]
    private Task RefreshTechnologiesAsync() => FetchAsync(refresh: true);

    private async Task FetchAsync(bool refresh)
    {
        if (_bank is null) return;
        var role = Role.Trim();
        if (TechBank.RoleKey(role).Length == 0) return;

        var load = ++_load;
        LoadFailed = false;
        IsLoading = true;
        Status = refresh ? $"Asking again for the technologies of a {role}…" : $"Finding the technologies of a {role}…";
        try
        {
            var saved = refresh ? null : await _bank.GetSavedRoleTechnologiesAsync(role);
            var found = saved ?? await _bank.GetRoleTechnologiesAsync(role, refresh);
            if (load != _load || TechBank.RoleKey(Role) != TechBank.RoleKey(role)) return; // a newer request, or another role, replaced this one
            _loadedRoleKey = TechBank.RoleKey(role);
            SetTechnologies(found, found.Count == 0
                ? $"No technologies came back for \"{role}\". Try a more specific role, or type technologies under Other."
                : saved is not null ? $"Saved list for {role}. No model call was needed." : $"Fetched for {role} and saved, so it will not be fetched again.");
            Remember();
        }
        catch (LlmException ex)
        {
            if (load != _load) return;
            LoadFailed = true;
            SetTechnologies([], $"Could not get the technologies: {ex.Message}");
        }
        finally
        {
            if (load == _load) IsLoading = false;
        }
    }

    private void SetTechnologies(IReadOnlyList<string> names, string status)
    {
        var keep = Technologies.Where(o => o.IsChecked).Select(o => o.Name).Concat(_carried).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count > 0) _carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Technologies.Clear();
        foreach (var name in names)
            Technologies.Add(new TechnologyOption(name, OnTechnologyOptionChanged) { IsChecked = keep.Contains(name) });
        Status = status;
        OnPropertyChanged(nameof(HasTechnologies));
        RefreshStartState();
    }

    [RelayCommand]
    private void SelectAllTechnologies()
    {
        foreach (var option in Technologies) option.IsChecked = true;
    }

    [RelayCommand]
    private void ClearTechnologies()
    {
        foreach (var option in Technologies) option.IsChecked = false;
    }

    // ---- difficulty

    public ObservableCollection<DifficultyOption> DifficultyOptions { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DifficultyDescription))] private Difficulty _difficulty;

    public string DifficultyDescription => Difficulty.Description();

    private void OnDifficultySelected(DifficultyOption option)
    {
        if (_syncingDifficulty) return;
        Difficulty = option.Difficulty;
    }

    private void SyncDifficultyOptions()
    {
        _syncingDifficulty = true;
        try
        {
            foreach (var option in DifficultyOptions) option.IsSelected = option.Difficulty == Difficulty;
        }
        finally { _syncingDifficulty = false; }
    }

    partial void OnDifficultyChanged(Difficulty value)
    {
        SyncDifficultyOptions();
        Remember();
    }

    // ---- the session

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLearnMode), nameof(IsPracticeMode), nameof(StartLabel))]
    private SessionMode _mode = SessionMode.Learn;

    public bool IsLearnMode => Mode == SessionMode.Learn;
    public bool IsPracticeMode => Mode == SessionMode.Practice;
    public string StartLabel => IsPracticeMode ? "Start Practice" : "Start Learn";

    [RelayCommand]
    private void SelectLearn() => Mode = SessionMode.Learn;

    [RelayCommand]
    private void SelectPractice() => Mode = SessionMode.Practice;

    public IReadOnlyList<Choice<AnswerLengthChoice>> AnswerLengthChoices { get; } =
    [
        new(AnswerLengthChoice.InterviewerNorm, "Interviewer norm (by question type)"),
        new(AnswerLengthChoice.Short, "Short (about 80 words)"),
        new(AnswerLengthChoice.Medium, "Medium (about 150 words)"),
        new(AnswerLengthChoice.Long, "Long (about 250 words)"),
    ];

    [ObservableProperty] private AnswerLengthChoice _answerLengthChoice = AnswerLengthChoice.InterviewerNorm;

    public int? AnswerWords => AnswerLengthChoice switch
    {
        AnswerLengthChoice.Short => 80,
        AnswerLengthChoice.Medium => 150,
        AnswerLengthChoice.Long => 250,
        _ => null,
    };

    public bool CanStart => IsAvailable && !IsLoading && SelectedTechnologies.Count > 0;

    public string StartHint =>
        !IsAvailable ? "Technology concepts need the saved question bank, which is not available in this build."
        : IsLoading ? "Finding the technologies…"
        : SelectedTechnologies.Count == 0 && OtherTechnologyChecked ? "Type a technology next to Other, or untick it."
        : SelectedTechnologies.Count == 0 ? "Tick at least one technology, or type one next to Other."
        : "";

    private void RefreshStartState()
    {
        OnPropertyChanged(nameof(SelectedTechnologies));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartHint));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        Remember();
        var profile = ConceptSession.Profile(Role, Difficulty);
        var request = new LearnSessionRequest(profile, [QuestionType.TechnicalConcept], AnswerWords, SelectedTechnologies, EmploymentType.FullTime);
        if (Mode == SessionMode.Practice) PracticeRequested?.Invoke(request);
        else LearnRequested?.Invoke(request);
    }

    // The role and difficulty are remembered between runs, as the role type is on Home.
    private void Remember()
    {
        if (_settings is null || !_ready) return;
        var role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim();
        if (_settings.Current.ConceptRole == role && _settings.Current.ConceptDifficulty == Difficulty) return;
        var updated = _settings.Current.Clone();
        updated.ConceptRole = role;
        updated.ConceptDifficulty = Difficulty;
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a choice that is not remembered is not worth an error */ }
    }
}
