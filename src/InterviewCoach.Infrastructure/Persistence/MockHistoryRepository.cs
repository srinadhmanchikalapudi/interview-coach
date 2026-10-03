using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Persistence;

public sealed class MockHistoryRepository(IDbContextFactory<AppDbContext> factory) : IMockHistory
{
    public async Task<int> AddAsync(MockRecord record, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = new MockSessionEntity
        {
            ProfileId = record.ProfileId,
            ProfileName = record.ProfileName,
            JobRole = record.JobRole,
            RoundType = record.RoundType,
            Employment = record.Employment,
            DurationMinutes = record.DurationMinutes,
            StartedAt = record.StartedAt,
            PlanJson = record.PlanJson ?? "",
        };
        Apply(row, record);
        db.MockSessions.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task UpdateAsync(MockRecord record, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.MockSessions.FirstOrDefaultAsync(m => m.Id == record.Id, ct);
        if (row is null) return;
        Apply(row, record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.MockSessions.AsNoTracking().OrderByDescending(m => m.StartedAt).ThenByDescending(m => m.Id).ToListAsync(ct);
        return rows.Select(r => new MockRecord
        {
            Id = r.Id,
            ProfileId = r.ProfileId,
            ProfileName = r.ProfileName,
            JobRole = r.JobRole,
            RoundType = r.RoundType,
            Employment = r.Employment,
            DurationMinutes = r.DurationMinutes,
            StartedAt = r.StartedAt,
            EndedAt = r.EndedAt,
            ElapsedSeconds = r.ElapsedSeconds,
            Finished = r.Finished,
            PlanJson = NullIfEmpty(r.PlanJson),
            TurnsJson = r.TurnsJson,
            DebriefJson = NullIfEmpty(r.DebriefJson),
            ThreadsJson = NullIfEmpty(r.ThreadsJson),
            HireSignal = NullIfEmpty(r.HireSignal),
        }).ToList();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.MockSessions.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
    }

    private static void Apply(MockSessionEntity row, MockRecord record)
    {
        row.EndedAt = record.EndedAt;
        row.ElapsedSeconds = record.ElapsedSeconds;
        row.Finished = record.Finished;
        row.TurnsJson = record.TurnsJson;
        row.DebriefJson = record.DebriefJson ?? "";
        row.ThreadsJson = record.ThreadsJson ?? "";
        row.HireSignal = record.HireSignal ?? "";
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
