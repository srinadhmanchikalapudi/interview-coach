using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Fakes;

/// <summary>Mock interviews that live only in memory: Demo mode uses it so made-up interviews never reach the real database.</summary>
public sealed class InMemoryMockHistory : IMockHistory
{
    private readonly object _gate = new();
    private readonly List<MockRecord> _records = [];
    private int _nextId = 1;

    public Task<int> AddAsync(MockRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            record.Id = _nextId++;
            _records.Add(record);
            return Task.FromResult(record.Id);
        }
    }

    public Task UpdateAsync(MockRecord record, CancellationToken ct = default) => Task.CompletedTask; // the engine updates the stored object itself

    public Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<MockRecord>>(_records.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).ToList());
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        lock (_gate) _records.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }
}
