using System.Collections.ObjectModel;
using System.Data.Common;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public enum LibrarySort { Newest, Oldest, Question, Type }

/// <summary>One chip in a filter: "All" (no id) or one question type or kind, with how many entries it has.</summary>
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

/// <summary>
/// One thing in the library: a question Learn mode showed with its answer, or an answer given in Practice with the feedback on it.
/// <see cref="Key"/> is unique across both (they have separate id numbers in the database).
/// </summary>
public sealed record LibraryItem(
    string Key, int Id, bool IsPractice, string Question, string QuestionType, string? Technology, bool IsFollowUp, string? ParentQuestion,
    bool IsGeneral, string? ProfileName, CoachOutput Coach, DateTime FirstSeenAt, DateTime LastSeenAt, int TimesSeen,
    string? AnswerText = null, string? InputMethod = null, int DurationSeconds = 0, int WordCount = 0, int AttemptNumber = 1)
{
    public static LibraryItem From(LearnHistoryEntry e) => new(
        $"L{e.Id}", e.Id, false, e.Question, e.QuestionType, e.Technology, e.IsFollowUp, e.ParentQuestion, e.IsGeneral, e.ProfileName,
        e.Coach, e.FirstSeenAt, e.LastSeenAt, e.TimesSeen);

    public static LibraryItem From(PracticeRecord r) => new(
        $"P{r.Id}", r.Id, true, r.Question, r.QuestionType, r.Technology, r.IsFollowUp, r.ParentQuestion, false, r.ProfileName,
        r.Coach, r.CreatedAt, r.CreatedAt, 1, r.AnswerText, r.InputMethod, r.DurationSeconds, r.WordCount, r.AttemptNumber);
}

/// <summary>One line in the library list.</summary>
public sealed class LibraryRow(LibraryItem item, string when)
{
    public LibraryItem Item { get; } = item;
    public string Key => Item.Key;
    public int Id => Item.Id;
    public string Question => Item.Question;
    public string TypeLabel => QuestionTypes.LabelFor(Item.QuestionType);
    public bool HasType => TypeLabel.Length > 0;
    public string Technology => Item.Technology ?? "";
    public bool HasTechnology => Technology.Length > 0;
    public bool IsFollowUp => Item.IsFollowUp;
    public bool IsPractice => Item.IsPractice;
    public string AnswerKind => Item.IsPractice
        ? (Item.AttemptNumber > 1 ? $"Your answer, attempt {Item.AttemptNumber}" : "Your answer")
        : Item.IsGeneral ? "General answer" : "Tailored to your resume";
    public string When { get; } = when;
    public string Seen => Item.TimesSeen > 1 ? $"Seen {Item.TimesSeen} times" : "";
}

/// <summary>
/// The library: every question and answer Learn mode has shown and every answer given in Practice with its feedback, to go back to
/// later. Filter by kind and question type, search, sort, open one to read it again, or remove it.
/// </summary>
public partial class LibraryViewModel : ObservableObject
{
    private readonly ILearnHistory _history;
    private readonly IPracticeHistory? _practice;
    private readonly IDialogService? _dialogs;
    private readonly Func<DateTime> _now;
    private IReadOnlyList<LibraryItem> _all = [];
    private bool _updating;

    public LibraryViewModel(ILearnHistory history, IDialogService? dialogs = null, Func<DateTime>? now = null, IPracticeHistory? practice = null)
    {
        _history = history;
        _practice = practice;
        _dialogs = dialogs;
        _now = now ?? (() => DateTime.Now);
        RebuildFilters();
    }

    /// <summary>Raised when the user asks to go back to the Home screen.</summary>
    public event Action? ExitRequested;

    public ObservableCollection<LibraryTypeFilter> KindFilters { get; } = [];
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

    /// <summary>The Learn / Practice chips are only worth showing once there is something from Practice too.</summary>
    public bool HasKindFilter => _all.Any(i => i.IsPractice);

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
    public bool DetailIsPractice => Selected?.IsPractice == true;
    public string DetailParent => Selected?.Item.ParentQuestion ?? "";

    /// <summary>For a practice entry: what the candidate wrote.</summary>
    public string DetailAnswer => Selected?.Item.AnswerText ?? "";
    public bool HasDetailAnswer => DetailIsPractice && DetailAnswer.Length > 0;
    public string DetailAnswerMeta => Selected?.Item is { IsPractice: true } p
        ? $"{p.WordCount} {(p.WordCount == 1 ? "word" : "words")}" + (p.DurationSeconds > 0 ? $"  ·  {p.DurationSeconds / 60}:{p.DurationSeconds % 60:00}" : "")
        : "";

    public string DetailMeta
    {
        get
        {
            if (Selected is not { } row) return "";
            var item = row.Item;
            var parts = new List<string>();
            if (item.IsPractice)
            {
                parts.Add($"Practised {row.When}");
                if (item.AttemptNumber > 1) parts.Add($"Attempt {item.AttemptNumber}");
                if (!string.IsNullOrEmpty(item.ProfileName)) parts.Add($"Feedback for {item.ProfileName}");
            }
            else
            {
                parts.Add($"Last seen {row.When}");
                if (row.Seen.Length > 0) parts.Add(row.Seen);
                parts.Add(item.IsGeneral
                    ? "General answer, written without your resume"
                    : string.IsNullOrEmpty(item.ProfileName) ? "Tailored to your resume" : $"Tailored to your resume for {item.ProfileName}");
            }
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Reads the library again. Called each time the screen is opened.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = "";
        try
        {
            var learned = await _history.ListAsync();
            var practised = _practice is null ? [] : await _practice.ListAsync();
            _all = [.. learned.Select(LibraryItem.From), .. practised.Select(LibraryItem.From)];
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
        var what = row.IsPractice ? "this answer and its feedback" : "this question and its answer";
        if (_dialogs is not null &&
            !_dialogs.Confirm("Remove from library", $"Remove {what} from the library? Questions you see in Learn mode again are added back."))
            return;

        try
        {
            if (row.IsPractice) await (_practice?.DeleteAsync(row.Id) ?? Task.CompletedTask);
            else await _history.DeleteAsync(row.Id);
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
        Detail = value is null
            ? null
            : new CoachOutputViewModel(
                value.Item.IsPractice ? value.Item.Coach.ForPractice() : value.Item.Coach.ForLearning(), OpenFollowUp, value.Item.QuestionType,
                "Click one to open its saved answer, if there is one.");
        foreach (var name in new[]
        {
            nameof(HasDetail), nameof(NothingSelected), nameof(DetailQuestion), nameof(DetailTypeLabel), nameof(HasDetailType),
            nameof(DetailTechnology), nameof(HasDetailTechnology), nameof(DetailIsFollowUp), nameof(DetailIsPractice), nameof(DetailParent),
            nameof(DetailMeta), nameof(DetailAnswer), nameof(HasDetailAnswer), nameof(DetailAnswerMeta),
        })
            OnPropertyChanged(name);
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnDetailChanged(CoachOutputViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    // ---- filtering

    private void RebuildFilters()
    {
        var keepType = TypeFilters.FirstOrDefault(f => f.IsSelected)?.TypeId;
        var keepKind = KindFilters.FirstOrDefault(f => f.IsSelected)?.TypeId;
        var present = _all.Select(e => e.QuestionType).Where(t => t.Length > 0).Distinct().ToList();
        var order = QuestionTypes.All.Select(t => t.Id()).ToList();
        var ordered = present.OrderBy(t => order.IndexOf(t) is var i and >= 0 ? i : int.MaxValue).ThenBy(t => t, StringComparer.Ordinal).ToList();

        _updating = true;
        try
        {
            TypeFilters.Clear();
            TypeFilters.Add(new LibraryTypeFilter(null, "All", OnTypeSelected) { Count = _all.Count });
            foreach (var id in ordered)
                TypeFilters.Add(new LibraryTypeFilter(id, QuestionTypes.LabelFor(id), OnTypeSelected) { Count = _all.Count(e => e.QuestionType == id) });
            (TypeFilters.FirstOrDefault(f => f.TypeId == keepType) ?? TypeFilters[0]).IsSelected = true;

            KindFilters.Clear();
            KindFilters.Add(new LibraryTypeFilter(null, "All", OnKindSelected) { Count = _all.Count });
            KindFilters.Add(new LibraryTypeFilter("learn", "Learned", OnKindSelected) { Count = _all.Count(e => !e.IsPractice) });
            KindFilters.Add(new LibraryTypeFilter("practice", "Practised", OnKindSelected) { Count = _all.Count(e => e.IsPractice) });
            (KindFilters.FirstOrDefault(f => f.TypeId == keepKind) ?? KindFilters[0]).IsSelected = true;
        }
        finally
        {
            _updating = false;
        }
        OnPropertyChanged(nameof(HasKindFilter));
    }

    private void OnTypeSelected(LibraryTypeFilter chosen) => OnChipSelected(chosen, TypeFilters);

    private void OnKindSelected(LibraryTypeFilter chosen) => OnChipSelected(chosen, KindFilters);

    private void OnChipSelected(LibraryTypeFilter chosen, IEnumerable<LibraryTypeFilter> group)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            foreach (var other in group.Where(f => !ReferenceEquals(f, chosen))) other.IsSelected = false;
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
        var kind = KindFilters.FirstOrDefault(f => f.IsSelected)?.TypeId;
        var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = _all
            .Where(e => kind is null || (kind == "practice") == e.IsPractice)
            .Where(e => typeId is null || e.QuestionType == typeId)
            .Where(e => words.All(w => Mentions(e, w)));

        var ordered = Sort switch
        {
            LibrarySort.Oldest => matches.OrderBy(e => e.FirstSeenAt).ThenBy(e => e.IsPractice).ThenBy(e => e.Id),
            LibrarySort.Question => matches.OrderBy(e => e.Question, StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.LastSeenAt),
            LibrarySort.Type => matches.OrderBy(e => QuestionTypes.LabelFor(e.QuestionType), StringComparer.OrdinalIgnoreCase).ThenByDescending(e => e.LastSeenAt),
            _ => matches.OrderByDescending(e => e.LastSeenAt).ThenByDescending(e => e.IsPractice).ThenByDescending(e => e.Id),
        };

        var keep = Selected?.Key;
        Rows.Clear();
        foreach (var item in ordered) Rows.Add(new LibraryRow(item, WhenText(item.LastSeenAt)));
        Selected = Rows.FirstOrDefault(r => r.Key == keep);

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(NothingSelected));
    }

    private static bool Mentions(LibraryItem e, string word)
        => e.Question.Contains(word, StringComparison.OrdinalIgnoreCase)
           || (e.Technology?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
           || QuestionTypes.LabelFor(e.QuestionType).Contains(word, StringComparison.OrdinalIgnoreCase)
           || e.Coach.ModelAnswer.Contains(word, StringComparison.OrdinalIgnoreCase)
           || (e.AnswerText?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);

    // ---- follow-ups inside an open answer

    private void OpenFollowUp(FollowUp followUp)
    {
        if (Selected is not { } parent) return;

        // A follow-up may have been opened in Learn mode, answered in Practice, or both: prefer the same kind as the open entry, then the newest.
        var saved = _all
            .Where(e => e.IsFollowUp
                && TextTools.SameQuestion(e.Question, followUp.Question)
                && TextTools.SameQuestion(e.ParentQuestion ?? "", parent.Question))
            .OrderByDescending(e => e.IsPractice == parent.IsPractice)
            .ThenByDescending(e => e.LastSeenAt)
            .FirstOrDefault();
        if (saved is null)
        {
            Message = "You have not opened this follow-up in Learn or Practice yet, so there is no saved answer for it.";
            return;
        }

        if (Rows.All(r => r.Key != saved.Key))
        {
            // It is filtered out; show everything so the follow-up can be opened.
            _updating = true;
            try { Search = ""; }
            finally { _updating = false; }
            TypeFilters[0].IsSelected = true;
            KindFilters[0].IsSelected = true;
            ApplyFilter();
        }
        Selected = Rows.FirstOrDefault(r => r.Key == saved.Key);
    }

    private string WhenText(DateTime stored)
    {
        // The database keeps UTC; the screen shows the user's local time.
        var local = DateTime.SpecifyKind(stored, DateTimeKind.Utc).ToLocalTime();
        var today = _now().Date;
        var time = local.ToString("h:mm tt", CultureInfo.CurrentCulture);
        if (local.Date == today) return $"today, {time}";
        if (local.Date == today.AddDays(-1)) return $"yesterday, {time}";
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
