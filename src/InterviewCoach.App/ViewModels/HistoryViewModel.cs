using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.ViewModels;

public enum HistoryKind { Finished, Unfinished, NeedsDebrief }

/// <summary>One mock interview in the History list.</summary>
public sealed partial class HistoryRow : ObservableObject
{
    public HistoryRow(MockRecord record, Func<HistoryRow, Task> open, Func<HistoryRow, Task> resume, Func<HistoryRow, Task> delete)
    {
        Record = record;
        Kind = record.IsUnfinished ? HistoryKind.Unfinished : record.NeedsDebrief ? HistoryKind.NeedsDebrief : HistoryKind.Finished;
        OpenCommand = new AsyncRelayCommand(() => open(this), () => Kind == HistoryKind.Finished);
        ResumeCommand = new AsyncRelayCommand(() => resume(this), () => Kind != HistoryKind.Finished);
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public MockRecord Record { get; }
    public HistoryKind Kind { get; }

    public string Title => string.IsNullOrWhiteSpace(Record.JobRole) ? Record.ProfileName : Record.JobRole;
    public string Subtitle => $"{Record.ProfileName} · {Record.StartedAt.ToLocalTime():d MMM yyyy, HH:mm}";
    public string RoundText => $"{Record.RoundType} · {Record.DurationMinutes} min";
    public string ElapsedText => $"{Record.ElapsedSeconds / 60}:{Record.ElapsedSeconds % 60:00} spent";

    public bool IsFinished => Kind == HistoryKind.Finished;
    public bool IsUnfinished => Kind == HistoryKind.Unfinished;
    public bool NeedsDebrief => Kind == HistoryKind.NeedsDebrief;

    /// <summary>True for a round that can be picked up (left midway) or whose debrief can be written; false for a finished one, which is opened.</summary>
    public bool CanResume => Kind != HistoryKind.Finished;

    public string SignalLabel => HireSignals.Label(Record.HireSignal);
    public bool IsPositive => IsFinished && HireSignals.Tone(Record.HireSignal) == SignalTone.Positive;
    public bool IsNegative => IsFinished && HireSignals.Tone(Record.HireSignal) == SignalTone.Negative;

    public string StatusText => Kind switch
    {
        HistoryKind.Unfinished => "Unfinished",
        HistoryKind.NeedsDebrief => "No debrief yet",
        _ => SignalLabel,
    };

    public string ResumeLabel => Kind == HistoryKind.NeedsDebrief ? "Write the debrief" : "Resume";

    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
}

/// <summary>
/// The History screen (spec 4.8) for mock interviews: every one that was started, newest first. A finished one opens its debrief; a round
/// that was left midway can be resumed where it stopped; one that ended before its debrief was written can have it written now. What was
/// learned or practised is in the Library.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IMockHistory _history;
    private readonly IProfileRepository _profiles;
    private readonly IDialogService? _dialogs;
    private readonly ISettingsStore? _settings;

    public HistoryViewModel(IMockHistory history, IProfileRepository profiles, IDialogService? dialogs = null, ISettingsStore? settings = null)
    {
        _history = history;
        _profiles = profiles;
        _dialogs = dialogs;
        _settings = settings;
    }

    /// <summary>Raised when a finished interview is opened: the debrief screen to show.</summary>
    public event Action<DebriefViewModel>? OpenRequested;

    /// <summary>Raised to pick an interview up again (or to write its debrief), with the request that starts it.</summary>
    public event Action<MockSessionRequest, MockRecord>? ResumeRequested;

    /// <summary>Raised when the user asks for the Library.</summary>
    public event Action? LibraryRequested;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isLoading;

    public bool IsEmpty => Rows.Count == 0 && !IsLoading && Error.Length == 0;
    public bool HasRows => Rows.Count > 0;
    public bool HasNotice => Notice.Length > 0;
    public bool HasError => Error.Length > 0;

    public string CountText => Rows.Count switch
    {
        0 => "",
        1 => "1 mock interview",
        var n => $"{n} mock interviews",
    };

    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnErrorChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(IsEmpty)); }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Reads the list again (every time the screen is opened: an interview may have ended since).</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = "";
        try
        {
            var records = await _history.ListAsync();
            Rows.Clear();
            foreach (var record in records) Rows.Add(new HistoryRow(record, Open, Resume, Delete));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        {
            Error = $"Could not read the history: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void OpenLibrary() => LibraryRequested?.Invoke();

    // ---- opening a finished interview

    private async Task Open(HistoryRow row)
    {
        Notice = "";
        var restored = MockRecords.Restore(row.Record);
        if (restored.Debrief is null || restored.Turns.Count == 0)
        {
            Notice = "This interview cannot be opened: its saved debrief could not be read.";
            return;
        }

        var profile = await FindProfileAsync(row.Record);
        var record = row.Record;
        var source = new DebriefSource(
            restored.Debrief, restored.Threads, record.JobRole, MockRecords.RoundOf(record.RoundType), record.DurationMinutes, record.ElapsedSeconds,
            record.StartedAt, profile, MockRecords.EmploymentOf(record.Employment));
        OpenRequested?.Invoke(new DebriefViewModel(source, _dialogs) { BackLabel = "Back to History" });
    }

    // ---- resuming

    private async Task Resume(HistoryRow row)
    {
        Notice = "";
        var request = await RequestForAsync(row.Record);
        if (request is null)
        {
            Notice = "This interview cannot be resumed: the profile it was for has been deleted.";
            return;
        }
        ResumeRequested?.Invoke(request, row.Record);
    }

    /// <summary>The request that picks a stored interview up again, or null when its profile is gone.</summary>
    private async Task<MockSessionRequest?> RequestForAsync(MockRecord record)
        => await FindProfileAsync(record) is { } profile ? MockSessionRequest.From(record, profile, _settings?.Current.ShowQuestionTextDefault ?? true) : null;

    private async Task<CandidateProfile?> FindProfileAsync(MockRecord record)
    {
        try
        {
            if (record.ProfileId != 0 && await _profiles.GetAsync(record.ProfileId) is { } byId) return byId;
            // Interviews stored before profiles were recorded are matched by name.
            return (await _profiles.ListAsync()).FirstOrDefault(p => p.Name == record.ProfileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- deleting

    private async Task Delete(HistoryRow row)
    {
        if (_dialogs is not null && !_dialogs.Confirm("Delete interview", $"Delete this {row.Record.RoundType.ToLowerInvariant()} interview from {row.Record.StartedAt.ToLocalTime():d MMM yyyy}? This cannot be undone."))
            return;
        try
        {
            await _history.DeleteAsync(row.Record.Id);
            Notice = "Interview deleted.";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            Notice = $"Could not delete the interview: {ex.Message}";
            return;
        }
        await LoadAsync();
    }
}
