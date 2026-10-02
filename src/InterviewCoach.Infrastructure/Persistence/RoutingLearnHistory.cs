using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>The library the app resolves: the real database normally, a throwaway in-memory one in Demo mode.</summary>
public sealed class RoutingLearnHistory(ISettingsStore settings, LearnHistoryRepository real, InMemoryLearnHistory demo) : ILearnHistory
{
    private ILearnHistory Current => settings.Current.DemoMode ? demo : real;

    public Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default) => Current.RecordAsync(entry, ct);
    public Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default) => Current.ListAsync(ct);
    public Task DeleteAsync(int id, CancellationToken ct = default) => Current.DeleteAsync(id, ct);
    public Task ClearAsync(CancellationToken ct = default) => Current.ClearAsync(ct);
}
