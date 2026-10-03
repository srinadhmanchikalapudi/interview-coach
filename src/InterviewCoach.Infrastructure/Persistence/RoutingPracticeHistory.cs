using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>The practice history the app resolves: the real database normally, a throwaway in-memory one in Demo mode.</summary>
public sealed class RoutingPracticeHistory(ISettingsStore settings, PracticeHistoryRepository real, InMemoryPracticeHistory demo) : IPracticeHistory
{
    private IPracticeHistory Current => settings.Current.DemoMode ? demo : real;

    public Task RecordAsync(PracticeRecord record, CancellationToken ct = default) => Current.RecordAsync(record, ct);
    public Task<IReadOnlyList<PracticeRecord>> ListAsync(CancellationToken ct = default) => Current.ListAsync(ct);
    public Task DeleteAsync(int id, CancellationToken ct = default) => Current.DeleteAsync(id, ct);
    public Task ClearAsync(CancellationToken ct = default) => Current.ClearAsync(ct);
}
