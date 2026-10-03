using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public record ProfileListItem(int Id, string Title, string Subtitle)
{
    /// <summary>The first letter of the profile name, shown in its avatar in the list.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();
}

public enum AnswerLengthChoice { InterviewerNorm, Short, Medium, Long, Custom }

/// <summary>The kind of session Start begins.</summary>
public enum SessionMode { Learn, Practice, Mock }

/// <summary>Everything Start hands over to begin a Learn or Practice session.</summary>
public record LearnSessionRequest(
    CandidateProfile Profile,
    IReadOnlyCollection<QuestionType> Types,
    int? AnswerWords,
    IReadOnlyList<string> Technologies,
    EmploymentType Employment);

/// <summary>Everything Start hands over to begin a Mock Interview.</summary>
public record MockSessionRequest(
    CandidateProfile Profile, RoundType Round, int DurationMinutes, bool ShowQuestionText, EmploymentType Employment);

/// <summary>One choice among several shown as chips (a round type, a length). Choosing one raises the callback.</summary>
public partial class SelectOption<T>(T value, string label, Action<SelectOption<T>> selected) : ObservableObject
{
    public T Value { get; } = value;
    public string Label { get; } = label;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>One choice in the Role type control (Full-time or Contract).</summary>
public partial class EmploymentOption(EmploymentType type, Action<EmploymentOption> selected) : ObservableObject
{
    public EmploymentType Type { get; } = type;
    public string Label { get; } = type.Label();

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

public partial class TypeOption(QuestionType type, Action<TypeOption> changed) : ObservableObject
{
    public QuestionType Type { get; } = type;
    public string Label { get; } = type.Label();

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(this);
}

/// <summary>One technology found in the job description, which the user can tick to get questions about it.</summary>
public partial class TechnologyOption(string name, Action<TechnologyOption> changed) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(this);
}

/// <summary>Home screen. For now this is the profile panel (milestone 2); the mode cards arrive with the modes.</summary>
public partial class HomeViewModel : ObservableObject
{
    private static readonly HashSet<string> EditorFields =
    [
        nameof(Name), nameof(JobRole), nameof(SelectedSeniority), nameof(JobDescription), nameof(ResumeText), nameof(EditingId),
    ];

    private readonly IProfileRepository _repository;
    private readonly IDocumentTextExtractor _extractor;
    private readonly IDialogService _dialogs;
    private readonly TechBank? _bank;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    private List<CandidateProfile> _profiles = [];
    private CandidateProfile _baseline = new();
    private ProfileListItem? _selectedItem;
    private bool _reloading;

    private readonly ISettingsStore? _settings;
    private bool _syncingEmployment;

    public HomeViewModel(
        IProfileRepository repository, IDocumentTextExtractor extractor, IDialogService dialogs, TechBank? bank = null,
        ISettingsStore? settings = null)
    {
        _repository = repository;
        _extractor = extractor;
        _dialogs = dialogs;
        _bank = bank;
        _settings = settings;

        // The kind of job is remembered between runs. The question types offered depend on it.
        EmploymentOptions = new ObservableCollection<EmploymentOption>(
            EmploymentTypes.All.Select(t => new EmploymentOption(t, OnEmploymentOptionSelected)));
        EmploymentType = settings?.Current.EmploymentType ?? EmploymentType.FullTime;
        SyncEmploymentOptions(); // the property may not have changed from its default, so mark the chosen chip explicitly
        TypeOptions = new ObservableCollection<TypeOption>(
            QuestionTypes.Applicable(EmploymentType).Select(t => new TypeOption(t, OnTypeOptionChanged)));
        InitMockOptions();
    }

    // ---- Mock Interview options: the round, its length, and whether the interviewer's words are shown

    public ObservableCollection<SelectOption<RoundType>> RoundOptions { get; private set; } = [];
    public ObservableCollection<SelectOption<int>> DurationOptions { get; private set; } = [];

    [ObservableProperty] private RoundType _round = RoundType.Mixed;
    [ObservableProperty] private int _durationMinutes = RoundTypes.DefaultDuration;
    [ObservableProperty] private bool _showQuestionText = true;

    public string RoundDescription => Round.Description();

    private bool _syncingMock;

    private void InitMockOptions()
    {
        RoundOptions = new ObservableCollection<SelectOption<RoundType>>(
            RoundTypes.All.Select(r => new SelectOption<RoundType>(r, r.Label(), o => { if (!_syncingMock) Round = o.Value; })));
        DurationOptions = new ObservableCollection<SelectOption<int>>(
            RoundTypes.Durations.Select(d => new SelectOption<int>(d, $"{d} min", o => { if (!_syncingMock) DurationMinutes = o.Value; })));
        Round = _settings?.Current.MockRoundType ?? RoundType.Mixed;
        DurationMinutes = RoundTypes.Durations.Contains(_settings?.Current.MockDurationMinutes ?? 0) ? _settings!.Current.MockDurationMinutes : RoundTypes.DefaultDuration;
        ShowQuestionText = _settings?.Current.ShowQuestionTextDefault ?? true;
        SyncMockOptions();
        _mockReady = true;
    }

    private bool _mockReady;

    private void SyncMockOptions()
    {
        _syncingMock = true;
        try
        {
            foreach (var o in RoundOptions) o.IsSelected = o.Value == Round;
            foreach (var o in DurationOptions) o.IsSelected = o.Value == DurationMinutes;
        }
        finally { _syncingMock = false; }
    }

    partial void OnRoundChanged(RoundType value)
    {
        if (RoundOptions is null) return;
        SyncMockOptions();
        OnPropertyChanged(nameof(RoundDescription));
        RememberMock();
    }

    partial void OnDurationMinutesChanged(int value)
    {
        if (DurationOptions is null) return;
        SyncMockOptions();
        RememberMock();
    }

    // The last round and length are remembered between runs, as the role type is.
    private void RememberMock()
    {
        if (_settings is null || !_mockReady) return;
        if (_settings.Current.MockRoundType == Round && _settings.Current.MockDurationMinutes == DurationMinutes) return;
        var updated = _settings.Current.Clone();
        updated.MockRoundType = Round;
        updated.MockDurationMinutes = DurationMinutes;
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a choice that is not remembered is not worth an error */ }
    }

    // ---- Role type: full-time or contract

    /// <summary>The choices shown under Role type.</summary>
    public ObservableCollection<EmploymentOption> EmploymentOptions { get; }

    [ObservableProperty] private EmploymentType _employmentType;

    public string EmploymentDescription => EmploymentType.Description();

    private void OnEmploymentOptionSelected(EmploymentOption option)
    {
        if (_syncingEmployment) return;
        EmploymentType = option.Type;
    }

    private void SyncEmploymentOptions()
    {
        _syncingEmployment = true;
        try
        {
            foreach (var option in EmploymentOptions) option.IsSelected = option.Type == EmploymentType;
        }
        finally { _syncingEmployment = false; }
    }

    partial void OnEmploymentTypeChanged(EmploymentType value)
    {
        SyncEmploymentOptions();
        OnPropertyChanged(nameof(EmploymentDescription));
        RebuildTypeOptions();
        Remember(value);
    }

    /// <summary>Full-time interviews have Motivation and fit; contract ones have Availability and engagement. Other ticks stay.</summary>
    private void RebuildTypeOptions()
    {
        if (TypeOptions is null) return; // still being constructed
        var existing = TypeOptions.ToDictionary(o => o.Type);
        var wanted = QuestionTypes.Applicable(EmploymentType)
            .Select(t => existing.TryGetValue(t, out var kept) ? kept : new TypeOption(t, OnTypeOptionChanged))
            .ToList();

        _updatingTypes = true;
        try
        {
            TypeOptions.Clear();
            foreach (var option in wanted) TypeOptions.Add(option);
            AnyType = NothingSpecificPicked; // a tick on the type that just went away leaves nothing picked: back to Any
        }
        finally { _updatingTypes = false; }
        RefreshStartState();
    }

    private void Remember(EmploymentType value)
    {
        if (_settings is null || _settings.Current.EmploymentType == value) return;
        var updated = _settings.Current.Clone();
        updated.EmploymentType = value;
        try { _settings.Save(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a choice that is not remembered is not worth an error */ }
    }

    /// <summary>
    /// Raised by Start in Learn mode with the saved profile, the chosen question types (empty with no technologies means any),
    /// the requested model answer length in words (null means the Coach's own per-type targets) and the technologies picked
    /// under By technology (empty when that option is off).
    /// </summary>
    public event Action<LearnSessionRequest>? LearnRequested;

    /// <summary>Raised by Start in Practice mode with the same details as <see cref="LearnRequested"/>.</summary>
    public event Action<LearnSessionRequest>? PracticeRequested;

    /// <summary>Raised by Start in Mock Interview mode with the saved profile, the round, its length and whether to show the question text.</summary>
    public event Action<MockSessionRequest>? MockRequested;

    /// <summary>Raised when the user opens the library of questions and answers they have already seen.</summary>
    public event Action? LibraryRequested;

    [RelayCommand]
    private void OpenLibrary() => LibraryRequested?.Invoke();

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];

    // Mode options (spec 4.1). Only Learn exists so far; Practice and Mock Interview arrive in later milestones.
    public ObservableCollection<TypeOption> TypeOptions { get; }
    private bool _updatingTypes;

    [ObservableProperty] private bool _anyType = true;

    private bool NothingSpecificPicked => !TypeOptions.Any(o => o.IsChecked) && !TechnologyMode;

    partial void OnAnyTypeChanged(bool value)
    {
        if (_updatingTypes) return;
        _updatingTypes = true;
        try
        {
            if (value)
            {
                foreach (var option in TypeOptions) option.IsChecked = false;
                TechnologyMode = false;
            }
            else if (NothingSpecificPicked)
                AnyType = true; // "no types at all" is not a choice; fall back to any
        }
        finally { _updatingTypes = false; }
        RefreshStartState();
    }

    private void OnTypeOptionChanged(TypeOption _)
    {
        if (_updatingTypes) return;
        _updatingTypes = true;
        try { AnyType = NothingSpecificPicked; }
        finally { _updatingTypes = false; }
    }

    // ---- By technology: questions about technologies picked from the job description

    /// <summary>
    /// The chosen mode, Learn or Practice. The cards are bound to it (through <see cref="IsLearnMode"/> and
    /// <see cref="IsPracticeMode"/>) instead of using a radio group, because a group's state is shared between view instances and
    /// the Learn card once came back unselected whenever the Home view was created again after visiting another page.
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLearnMode), nameof(IsPracticeMode), nameof(IsMockMode), nameof(StartLabel), nameof(ShowQuestionTypes), nameof(CanStart), nameof(StartHint))]
    private SessionMode _mode = SessionMode.Learn;

    public bool IsLearnMode => Mode == SessionMode.Learn;
    public bool IsPracticeMode => Mode == SessionMode.Practice;
    public bool IsMockMode => Mode == SessionMode.Mock;
    public string StartLabel => IsMockMode ? "Start Mock Interview" : IsPracticeMode ? "Start Practice" : "Start Learn";

    /// <summary>Question types, By technology and answer length belong to Learn and Practice; a mock interview chooses its own questions.</summary>
    public bool ShowQuestionTypes => !IsMockMode;

    [RelayCommand]
    private void SelectLearn() => Mode = SessionMode.Learn;

    [RelayCommand]
    private void SelectPractice() => Mode = SessionMode.Practice;

    [RelayCommand]
    private void SelectMock() => Mode = SessionMode.Mock;

    /// <summary>The By technology option needs the technology bank, which also finds the technologies in a job description.</summary>
    public bool HasTechnologyOption => _bank is not null;

    [ObservableProperty] private bool _technologyMode;
    [ObservableProperty] private bool _isLoadingTechnologies;
    [ObservableProperty] private string _technologyStatus = "";
    [ObservableProperty] private bool _technologyLoadFailed;

    /// <summary>The technologies found in the saved profile's job description.</summary>
    public ObservableCollection<TechnologyOption> Technologies { get; } = [];

    public bool HasTechnologies => Technologies.Count > 0;

    // "Other": technologies the job description did not mention, or that were not detected, typed in by hand.
    [ObservableProperty] private bool _otherTechnologyChecked;
    [ObservableProperty] private string _otherTechnologies = "";

    private const int MaxOtherNameLength = 60;

    /// <summary>The names typed next to Other: split on commas, semicolons or new lines, trimmed, without duplicates.</summary>
    public IReadOnlyList<string> OtherTechnologyNames => OtherTechnologies
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(n => n.Length > MaxOtherNameLength ? n[..MaxOtherNameLength].TrimEnd() : n)
        .DistinctBy(n => n.ToLowerInvariant())
        .Take(TechBank.MaxTechnologies)
        .ToList();

    partial void OnOtherTechnologiesChanged(string value)
    {
        // Typing a name means it is wanted: tick Other for the user.
        if (!OtherTechnologyChecked && !string.IsNullOrWhiteSpace(value)) OtherTechnologyChecked = true;
        RefreshStartState();
    }

    partial void OnOtherTechnologyCheckedChanged(bool value) => RefreshStartState();

    /// <summary>Every technology to ask about: the detected ones that are ticked plus any typed under Other, without duplicates.</summary>
    public IReadOnlyList<string> SelectedTechnologies
    {
        get
        {
            if (!TechnologyMode) return [];
            var names = Technologies.Where(o => o.IsChecked).Select(o => o.Name).ToList();
            if (OtherTechnologyChecked) names.AddRange(OtherTechnologyNames);
            return names.DistinctBy(n => n.ToLowerInvariant()).ToList();
        }
    }

    private int _technologyLoad;

    partial void OnTechnologyModeChanged(bool value)
    {
        if (!_updatingTypes)
        {
            _updatingTypes = true;
            try { AnyType = NothingSpecificPicked; }
            finally { _updatingTypes = false; }
        }
        if (value) _ = RefreshTechnologiesAsync();
        RefreshStartState();
    }

    partial void OnIsLoadingTechnologiesChanged(bool value) => RefreshStartState();

    private void OnTechnologyOptionChanged(TechnologyOption _) => RefreshStartState();

    [RelayCommand]
    private Task ReloadTechnologiesAsync() => RefreshTechnologiesAsync();

    /// <summary>
    /// Finds the technologies in the saved profile's job description (sessions use the saved copy). Asked of the model once
    /// per distinct job description and remembered, so reopening this is free. Ticked technologies stay ticked if still present.
    /// </summary>
    private async Task RefreshTechnologiesAsync()
    {
        if (_bank is null) return;
        var load = ++_technologyLoad;
        var saved = EditingId != 0 ? _profiles.FirstOrDefault(p => p.Id == EditingId) : null;

        TechnologyLoadFailed = false;
        if (saved is null)
        {
            IsLoadingTechnologies = false;
            SetTechnologies([], "Save the profile to see the technologies in its job description.");
            return;
        }
        if (string.IsNullOrWhiteSpace(saved.JobDescription))
        {
            IsLoadingTechnologies = false;
            SetTechnologies([], "This profile has no job description yet.");
            return;
        }

        IsLoadingTechnologies = true;
        TechnologyStatus = "Reading the job description for technologies…";
        IReadOnlyList<string> found;
        try
        {
            found = await _bank.GetTechnologiesAsync(saved);
        }
        catch (LlmException ex)
        {
            if (load != _technologyLoad) return;
            IsLoadingTechnologies = false;
            TechnologyLoadFailed = true;
            SetTechnologies([], $"Could not read the job description: {ex.Message}");
            return;
        }
        if (load != _technologyLoad) return; // a newer request (another profile, say) replaced this one

        IsLoadingTechnologies = false;
        SetTechnologies(found, found.Count == 0 ? "No specific technologies were found in the job description." : "");
    }

    private void SetTechnologies(IReadOnlyList<string> names, string status)
    {
        var keep = Technologies.Where(o => o.IsChecked).Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Technologies.Clear();
        foreach (var name in names)
            Technologies.Add(new TechnologyOption(name, OnTechnologyOptionChanged) { IsChecked = keep.Contains(name) });
        TechnologyStatus = status;
        OnPropertyChanged(nameof(HasTechnologies));
        RefreshStartState();
    }

    private void RefreshStartState()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartHint));
        OnPropertyChanged(nameof(SelectedTechnologies));
        StartCommand.NotifyCanExecuteChanged();
    }

    public IReadOnlyCollection<QuestionType> AllowedTypes =>
        AnyType ? [] : TypeOptions.Where(o => o.IsChecked).Select(o => o.Type).ToList();

    // Answer length: how long the model answers should be. "Interviewer norm" lets the Coach pick by question type.
    public IReadOnlyList<Choice<AnswerLengthChoice>> AnswerLengthChoices { get; } =
    [
        new(AnswerLengthChoice.InterviewerNorm, "Interviewer norm (by question type)"),
        new(AnswerLengthChoice.Short, "Short (about 80 words)"),
        new(AnswerLengthChoice.Medium, "Medium (about 150 words)"),
        new(AnswerLengthChoice.Long, "Long (about 250 words)"),
        new(AnswerLengthChoice.Custom, "Custom word count"),
    ];

    [ObservableProperty] private AnswerLengthChoice _answerLengthChoice = AnswerLengthChoice.InterviewerNorm;
    [ObservableProperty] private string _customWords = "150";

    public bool IsCustomLength => AnswerLengthChoice == AnswerLengthChoice.Custom;

    private bool CustomWordsValid =>
        int.TryParse(CustomWords.Trim(), out var n) && n is >= AnswerLength.MinCustomWords and <= AnswerLength.MaxCustomWords;

    /// <summary>Requested model answer length in words, or null to let the Coach follow typical interviewer expectations.</summary>
    public int? AnswerWords => AnswerLengthChoice switch
    {
        AnswerLengthChoice.Short => 80,
        AnswerLengthChoice.Medium => 150,
        AnswerLengthChoice.Long => 250,
        AnswerLengthChoice.Custom when CustomWordsValid => int.Parse(CustomWords.Trim()),
        _ => null,
    };

    public string AnswerLengthHint => AnswerLengthChoice switch
    {
        AnswerLengthChoice.InterviewerNorm =>
            "Each answer is as long as an interviewer would expect for that kind of question: about 60–150 words for a concept, 120–220 for a scenario, 150–280 for behavioral and project questions.",
        AnswerLengthChoice.Custom when !CustomWordsValid =>
            $"Enter a number from {AnswerLength.MinCustomWords} to {AnswerLength.MaxCustomWords}.",
        _ => $"About {AnswerWords} words, roughly {AnswerLength.Spoken(AnswerWords!.Value)} spoken. Shorter answers also arrive sooner and cost less.",
    };

    partial void OnAnswerLengthChoiceChanged(AnswerLengthChoice value) => RefreshLengthState();
    partial void OnCustomWordsChanged(string value) => RefreshLengthState();

    private void RefreshLengthState()
    {
        OnPropertyChanged(nameof(IsCustomLength));
        OnPropertyChanged(nameof(AnswerWords));
        OnPropertyChanged(nameof(AnswerLengthHint));
        RefreshStartState();
    }

    /// <summary>A session needs a saved, complete profile: what runs is the saved copy, not unsaved edits.</summary>
    public bool CanStart => HasEditor && EditingId != 0 && !IsDirty && IsReady &&
                            (IsMockMode || ((AnswerLengthChoice != AnswerLengthChoice.Custom || CustomWordsValid) &&
                                            (!TechnologyMode || (!IsLoadingTechnologies && SelectedTechnologies.Count > 0))));

    public string StartHint =>
        !HasEditor ? "Select or create a profile first."
        : EditingId == 0 || IsDirty ? "Save the profile first. Sessions use the saved version."
        : !IsReady ? ReadinessText
        : IsMockMode ? ""
        : AnswerLengthChoice == AnswerLengthChoice.Custom && !CustomWordsValid ? AnswerLengthHint
        : TechnologyMode && IsLoadingTechnologies ? "Reading the job description for technologies…"
        : TechnologyMode && SelectedTechnologies.Count == 0 && OtherTechnologyChecked ? "Type a technology next to Other, or untick it."
        : TechnologyMode && SelectedTechnologies.Count == 0 ? "Tick at least one technology under By technology, or type one next to Other."
        : "";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var saved = _profiles.FirstOrDefault(p => p.Id == EditingId);
        if (saved is null) return;

        if (Mode == SessionMode.Mock)
        {
            MockRequested?.Invoke(new MockSessionRequest(saved.Clone(), Round, DurationMinutes, ShowQuestionText, EmploymentType));
            return;
        }

        var request = new LearnSessionRequest(saved.Clone(), AllowedTypes, AnswerWords, SelectedTechnologies, EmploymentType);
        if (Mode == SessionMode.Practice) PracticeRequested?.Invoke(request);
        else LearnRequested?.Invoke(request);
    }

    public IReadOnlyList<Choice<Seniority>> SeniorityChoices { get; } =
        Enum.GetValues<Seniority>().Select(s => new Choice<Seniority>(s, s.ToString())).ToList();

    // Editor state
    [ObservableProperty] private bool _hasEditor;
    [ObservableProperty] private int _editingId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _jobRole = "";
    [ObservableProperty] private Seniority _selectedSeniority = Seniority.Mid;
    [ObservableProperty] private string _jobDescription = "";
    [ObservableProperty] private string _resumeText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNotBusy))] private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    public ProfileListItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_reloading || Equals(_selectedItem, value)) return;
            if (!ConfirmDiscard())
            {
                // The ListBox already moved its highlight; tell it to snap back once the binding finishes.
                _ui?.Post(_ => OnPropertyChanged(), null);
                return;
            }
            _selectedItem = value;
            OnPropertyChanged();
            if (value is null) EndEditing();
            else BeginEditing(_profiles.First(p => p.Id == value.Id));
        }
    }

    public string EditorTitle => EditingId == 0 ? "New profile" : "Edit profile";
    public bool CanDelete => HasEditor && EditingId != 0;

    public bool IsDirty => HasEditor && (
        Name != _baseline.Name || JobRole != _baseline.JobRole || SelectedSeniority != _baseline.Seniority ||
        JobDescription != _baseline.JobDescription || ResumeText != _baseline.ResumeText);

    public string JobDescriptionStats => $"{JobDescription.Length:N0} characters";
    public string ResumeStats => $"{ResumeText.Length:N0} characters";
    public bool JobDescriptionTooLong => JobDescription.Length > CandidateProfile.LongTextThreshold;
    public bool ResumeTooLong => ResumeText.Length > CandidateProfile.LongTextThreshold;

    public string LongTextWarning(int length) =>
        $"{length:N0} characters is very long. All of it is sent to the model on every request, which is slow and costs more. Trim it to about {CandidateProfile.LongTextThreshold:N0}?";

    public string JobDescriptionWarning => LongTextWarning(JobDescription.Length);
    public string ResumeWarning => LongTextWarning(ResumeText.Length);

    public string ReadinessText
    {
        get
        {
            var missing = CurrentFields().MissingForStart();
            return missing.Count == 0
                ? "Ready: this profile has everything a session needs."
                : $"Still needed before a session can start: {string.Join(", ", missing)}.";
        }
    }

    public bool IsReady => CurrentFields().MissingForStart().Count == 0;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null || !(EditorFields.Contains(e.PropertyName) || e.PropertyName == nameof(HasEditor))) return;

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(JobDescriptionStats));
        OnPropertyChanged(nameof(ResumeStats));
        OnPropertyChanged(nameof(JobDescriptionTooLong));
        OnPropertyChanged(nameof(ResumeTooLong));
        OnPropertyChanged(nameof(JobDescriptionWarning));
        OnPropertyChanged(nameof(ResumeWarning));
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartHint));
        StartCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the saved profiles and opens the most recently used one.</summary>
    public async Task InitializeAsync()
    {
        await RefreshAsync(selectId: null);
    }

    private async Task RefreshAsync(int? selectId)
    {
        _profiles = (await _repository.ListAsync()).ToList();

        _reloading = true;
        try
        {
            Profiles.Clear();
            foreach (var p in _profiles)
                Profiles.Add(new ProfileListItem(p.Id, p.Name, $"{p.JobRole} · {p.Seniority}"));
            var target = selectId is { } id ? _profiles.FirstOrDefault(p => p.Id == id) : _profiles.FirstOrDefault();
            _selectedItem = target is null ? null : Profiles.First(i => i.Id == target.Id);
        }
        finally { _reloading = false; }

        OnPropertyChanged(nameof(SelectedItem));
        var current = _selectedItem is null ? null : _profiles.First(p => p.Id == _selectedItem.Id);
        if (current is null) EndEditing();
        else BeginEditing(current);
    }

    private void BeginEditing(CandidateProfile profile)
    {
        _baseline = profile.Clone();
        EditingId = profile.Id;
        Name = profile.Name;
        JobRole = profile.JobRole;
        SelectedSeniority = profile.Seniority;
        JobDescription = profile.JobDescription;
        ResumeText = profile.ResumeText;
        HasEditor = true;
        Status = "";
        OnPropertyChanged(nameof(IsDirty));
        if (TechnologyMode) _ = RefreshTechnologiesAsync(); // the saved job description may be a different one now
    }

    private void EndEditing()
    {
        _baseline = new CandidateProfile();
        HasEditor = false;
        EditingId = 0;
        Name = JobRole = JobDescription = ResumeText = "";
        SelectedSeniority = Seniority.Mid;
        Status = "";
        if (TechnologyMode) _ = RefreshTechnologiesAsync();
    }

    private CandidateProfile CurrentFields() => new()
    {
        Id = EditingId,
        Name = Name,
        JobRole = JobRole,
        Seniority = SelectedSeniority,
        JobDescription = JobDescription,
        ResumeText = ResumeText,
    };

    private bool ConfirmDiscard()
        => !IsDirty || _dialogs.Confirm("Unsaved changes", "You have unsaved changes to this profile. Discard them?");

    [RelayCommand]
    private void New()
    {
        if (!ConfirmDiscard()) return;
        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        EndEditing();
        BeginEditing(new CandidateProfile());
        Status = "Fill in the details, then press Save.";
    }

    private bool CanSave() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var profile = CurrentFields();
        if (string.IsNullOrWhiteSpace(profile.Name))
            profile.Name = profile.JobRole;
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            Status = "Give the profile a name (or at least a job role) before saving.";
            return;
        }

        try
        {
            var saved = await _repository.SaveAsync(profile);
            await RefreshAsync(saved.Id);
            Status = $"Saved at {DateTime.Now:t}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            Status = $"Could not save: {ex.Message}";
        }
    }

    private bool CanRevert() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        BeginEditing(_baseline);
        Status = "Changes discarded.";
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (!_dialogs.Confirm("Delete profile", $"Delete \"{_baseline.Name}\"? This cannot be undone.")) return;

        await _repository.DeleteAsync(EditingId);
        await RefreshAsync(selectId: null);
        Status = "Profile deleted.";
    }

    [RelayCommand] private Task LoadJobDescriptionFileAsync() => LoadFileAsync(isResume: false);
    [RelayCommand] private Task LoadResumeFileAsync() => LoadFileAsync(isResume: true);

    private async Task LoadFileAsync(bool isResume)
    {
        var what = isResume ? "resume" : "job description";
        var exts = string.Join(";", _extractor.SupportedExtensions.Select(e => "*" + e));
        var path = _dialogs.PickFile($"Load {what}", $"Documents ({exts})|{exts}|All files (*.*)|*.*");
        if (path is null) return;

        var existing = isResume ? ResumeText : JobDescription;
        if (!string.IsNullOrWhiteSpace(existing) &&
            !_dialogs.Confirm($"Replace {what}", $"Replace the current {what} text with the contents of {Path.GetFileName(path)}?"))
            return;

        IsBusy = true;
        Status = $"Reading {Path.GetFileName(path)}…";
        try
        {
            var text = await _extractor.ExtractTextAsync(path);
            if (isResume) ResumeText = text; else JobDescription = text;
            Status = $"Loaded {Path.GetFileName(path)}: {text.Length:N0} characters. Read through it below and fix anything the extraction got wrong, then Save.";
        }
        catch (DocumentExtractionException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TrimJobDescription() => JobDescription = TextTools.TrimTo(JobDescription, CandidateProfile.LongTextThreshold);

    [RelayCommand]
    private void TrimResume() => ResumeText = TextTools.TrimTo(ResumeText, CandidateProfile.LongTextThreshold);
}
