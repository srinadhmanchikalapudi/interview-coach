using System.Collections.ObjectModel;
using System.Data.Common;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public enum LibrarySort { Newest, Oldest, Question, Type }

/// <summary>One chip in the type filter: "All" (no type id) or one question type, with how many entries it has.</summary>
public partial class LibraryTypeFilter(string? typeId, string label, Action<LibraryTypeFilter> selected) : ObservableObject
{
    public string? TypeId { get; } = typeId;
    public string Label { get; } = label;

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    public string Text => $"{Label}  {Count}";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Text));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>One line in the library list.</summary>
public sealed class LibraryRow(LearnHistoryEntry entry, string when)
{
    public LearnHistoryEntry Entry { get; } = entry;
    public int Id => Entry.Id;
    public string Question => Entry.Question;
    public string TypeLabel => QuestionTypes.LabelFor(Entry.QuestionType);
    public bool HasType => TypeLabel.Length > 0;
    public string Technology => Entry.Technology ?? "";
    public bool HasTechnology => Technology.Length > 0;
    public bool IsFollowUp => Entry.IsFollowUp;
    public string AnswerKind => Entry.IsGeneral ? "General answer" : "Tailored to your resume";
    public string When { get; } = when;
    public string Seen => Entry.TimesSeen > 1 ? $"Seen {Entry.TimesSeen} times" : "";
}

/// <summary>
/// The library: every question and answer Learn mode has shown, to go back to later. Filter by question type, search, sort,
/// open one to read its answer again, or remove it.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly ILearnHistory _history;
    private readonly IDialogService? _dialogs;
    private readonly Func<DateTime> _now;
    private IReadOnlyList<LearnHistoryEntry> _all = [];
    private bool _updating;

    public LibraryViewModel(ILearnHistory history, IDialogService? dialogs = null, Func<DateTime>? now = null)
    {
        _history = history;
        _dialogs = dialogs;
        _now = now ?? (() => DateTime.Now);
        RebuildFilters();
    }

    /// <summary>Raised when the user asks to go back to the Home screen.</summary>
    public event Action? ExitRequested;

    public ObservableCollection<LibraryTypeFilter> TypeFilters { get; } = [];
    public ObservableCollection<LibraryRow> Rows { get; } = [];

    public IReadOnlyList<Choice<LibrarySort>> SortChoices { get; } =
    [
        new(LibrarySort.Newest, "Newest first"),
        new(LibrarySort.Oldest, "Oldest first"),
        new(LibrarySort.Question, "Question (A to Z)"),
        new(LibrarySort.Type, "Question type"),
    ];

    [ObservableProperty] private LibrarySort _sort = LibrarySort.Newest;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private LibraryRow? _selected;
    [ObservableProperty] private CoachOutputViewModel? _detail;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _loadError = "";
    [ObservableProperty] private bool _isLoading;

    public bool HasEntries => _all.Count > 0;
    public bool IsEmpty => _all.Count == 0 && !IsLoading && LoadError.Length == 0;
    public bool HasError => LoadError.Length > 0;
    public bool HasMessage => Message.Length > 0;
    public bool NoMatches => HasEntries && Rows.Count == 0;
    public bool HasDetail => Selected is not null && Detail is not null;
    public bool NothingSelected => HasEntries && Selected is null;

    public string CountText => !HasEntries ? "" : Rows.Count == _all.Count
        ? $"{Rows.Count} {(Rows.Count == 1 ? "entry" : "entries")}"
        : $"{Rows.Count} of {_all.Count} entries";

    // What is open on the right.
    public string DetailQuestion => Selected?.Question ?? "";
    public string DetailTypeLabel => Selected?.TypeLabel ?? "";
    public bool HasDetailType => DetailTypeLabel.Length > 0;
    public string DetailTechnology => Selected?.Technology ?? "";
    public bool HasDetailTechnology => DetailTechnology.Length > 0;
    public bool DetailIsFollowUp => Selected?.IsFollowUp == true;
    public string DetailParent => Selected?.Entry.ParentQuestion ?? "";
    public string DetailMeta => Selected is not { } row ? "" : string.Join("  ·  ", new[]
    {
        $"Last seen {row.When}",
        row.Seen,
        row.Entry.IsGeneral ? "General answer, written without your resume"
            : string.IsNullOrEmpty(row.Entry.ProfileName) ? "Tailored to your resume" : $"Tailored to your resume for {row.Entry.ProfileName}",
    }.Where(part => part.Length > 0));

    /// <summary>Reads the library again. Called each time the screen is opened.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = "";
        try
        {
            _all = await _history.ListAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException)
        {
            _all = [];
            LoadError = "Could not read your saved questions: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
        Message = "";
        RebuildFilters();
        ApplyFilter();
    }

    [RelayCommand]
    private void Back() => ExitRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } row) return;
        if (_dialogs is not null &&
            !_dialogs.Confirm("Remove from library", "Remove this question and its answer from the library? You can see it in Learn mode again later, and it will be added back."))
            return;

        try
        {
            await _history.DeleteAsync(row.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbException)
        {
            Message = "Could not remove it: " + ex.Message;
            return;
        }
        Selected = null;
        await LoadAsync();
    }

    private bool CanDelete() => Selected is not null;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSortChanged(LibrarySort value) => ApplyFilter();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnLoadErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(LibraryRow? value)
    {
        Message = "";
        Detail = value is null ? null : new CoachOutputViewModel(value.Entry.Coach.ForLearning(), OpenFollowUp, value.Entry.QuestionType);
        foreach (var name in new[]
        {
            nameof(HasDetail), nameof(NothingSelected), nameof(DetailQuestion), nameof(DetailTypeLabel), nameof(HasDetailType),
            nameof(DetailTechnology), nameof(HasDetailTechnology), nameof(DetailIsFollowUp), nameof(DetailParent), nameof(DetailMeta),
        })
            OnPropertyChanged(name);
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnDetailChanged(CoachOutputViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    // ---- filtering

    private void RebuildFilters()
    {
        var keep = TypeFilters.FirstOrDefault(f => f.IsSelected)?.TypeId;
        var present = _all.Select(e => e.QuestionType).Where(t => t.Length > 0).Distinct().ToList();
        var order = QuestionTypes.All.Select(t => t.Id()).ToList();
        var ordered = present.OrderBy(t => order.IndexOf(t) is var i and >= 0 ? i : int.MaxValue).ThenBy(t => t, StringComparer.Ordinal).ToList();

        _updating = true;
        try
        {
            TypeFilters.Clear();
            TypeFilters.Add(new LibraryTypeFilter(null, "All", OnFilterSelected) { Count = _all.Count });
            foreach (var id in ordered)
                TypeFilters.Add(new LibraryTypeFilter(id, QuestionTypes.LabelFor(id), OnFilterSelected) { Count = _all.Count(e => e.QuestionType == id) });
            (TypeFilters.FirstOrDefault(f => f.TypeId == keep) ?? TypeFilters[0]).IsSelected = true;
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnFilterSelected(LibraryTypeFilter chosen)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            foreach (var other in TypeFilters.Where(f => !ReferenceEquals(f, chosen))) other.IsSelected = false;
        }
        finally
        {
            _updating = false;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_updating) return;

        var typeId = TypeFilters.FirstOrDefault(f => f.IsSelected)?.TypeId;
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = _all
            .Where(e => typeId is null || e.QuestionType == typeId)
            .Where(e => words.All(w => Mentions(e, w)));

        var ordered = Sort switch
        {
            LibrarySort.Oldest => matches.OrderBy(e => e.FirstSeenAt).ThenBy(e => e.Id),
            LibrarySort.Question => matches.OrderBy(e => e.Question, StringComparer.OrdinalIgnoreCase),
            LibrarySort.Type => matches.OrderBy(e => QuestionTypes.LabelFor(e.QuestionType), StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.LastSeenAt),
            _ => matches.OrderByDescending(e => e.LastSeenAt).ThenByDescending(e => e.Id),
        };

        var keep = Selected?.Id;
        Rows.Clear();
        foreach (var entry in ordered) Rows.Add(new LibraryRow(entry, WhenText(entry.LastSeenAt)));
        Selected = Rows.FirstOrDefault(r => r.Id == keep);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(NothingSelected));
    }

    private static bool Mentions(LearnHistoryEntry e, string word)
        => e.Question.Contains(word, StringComparison.OrdinalIgnoreCase)
           || (e.Technology?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
           || QuestionTypes.LabelFor(e.QuestionType).Contains(word, StringComparison.OrdinalIgnoreCase)
           || e.Coach.ModelAnswer.Contains(word, StringComparison.OrdinalIgnoreCase);

    // ---- follow-ups inside an open answer

    private void OpenFollowUp(FollowUp followUp)
    {
        if (Selected is not { } parent) return;

        var saved = _all.FirstOrDefault(e => e.IsFollowUp
            && TextTools.SameQuestion(e.Question, followUp.Question)
            && TextTools.SameQuestion(e.ParentQuestion ?? "", parent.Question));
        if (saved is null)
        {
            Message = "You have not opened this follow-up in Learn mode yet, so there is no saved answer for it.";
            return;
        }

        if (Rows.All(r => r.Id != saved.Id))
        {
            // It is filtered out; show everything so the follow-up can be opened.
            _updating = true;
            try { Search = ""; }
            finally { _updating = false; }
            TypeFilters[0].IsSelected = true;
            ApplyFilter();
        }
        Selected = Rows.FirstOrDefault(r => r.Id == saved.Id);
    }

    private string WhenText(DateTime stored)
    {
        // The database keeps UTC; the screen shows the user's local time.
        var local = DateTime.SpecifyKind(stored, DateTimeKind.Utc).ToLocalTime();
        var today = _now().Date;
        var time = local.ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        if (local.Date == today) return $"today, {time}";
        if (local.Date == today.AddDays(-1)) return $"yesterday, {time}";
        return local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }
}
