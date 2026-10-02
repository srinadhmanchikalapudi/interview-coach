using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Persistence;

public sealed class ProfileRepository(IDbContextFactory<AppDbContext> factory, IClock clock) : IProfileRepository
{
    public async Task<IReadOnlyList<CandidateProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Sorting happens client-side: SQLite has no native DateTime type, and this list is tiny.
        var all = await db.Profiles.AsNoTracking().ToListAsync(ct);
        return all.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Name).ToList();
    }

    public async Task<CandidateProfile?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<CandidateProfile> SaveAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        var now = clock.UtcNow.UtcDateTime;
        await using var db = await factory.CreateDbContextAsync(ct);

        CandidateProfile entity;
        if (profile.Id == 0)
        {
            entity = new CandidateProfile { CreatedAt = now };
            db.Profiles.Add(entity);
        }
        else
        {
            entity = await db.Profiles.FirstOrDefaultAsync(p => p.Id == profile.Id, ct)
                ?? throw new InvalidOperationException("This profile no longer exists. It may have been deleted.");
        }

        entity.Name = profile.Name.Trim();
        entity.JobRole = profile.JobRole.Trim();
        entity.Seniority = profile.Seniority;
        entity.JobDescription = profile.JobDescription;
        entity.ResumeText = profile.ResumeText;
        entity.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return entity.Clone();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Profiles.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }
}
