using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>The mock history the app resolves: the real database normally, a throwaway in-memory one in Demo mode.</summary>
public sealed class RoutingMockHistory(ISettingsStore settings, MockHistoryRepository real, InMemoryMockHistory demo) : IMockHistory
{
    private IMockHistory Current => settings.Current.DemoMode ? demo : real;

    public Task<int> AddAsync(MockRecord record, CancellationToken ct = default) => Current.AddAsync(record, ct);
    public Task UpdateAsync(MockRecord record, CancellationToken ct = default) => Current.UpdateAsync(record, ct);
    public Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default) => Current.ListAsync(ct);
    public Task DeleteAsync(int id, CancellationToken ct = default) => Current.DeleteAsync(id, ct);
}
