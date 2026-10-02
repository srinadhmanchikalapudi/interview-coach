using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Persistence;

public sealed class LearnHistoryRepository(IDbContextFactory<AppDbContext> factory, IClock clock) : ILearnHistory
{
    public async Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default)
    {
        var questionKey = TextTools.NormalizeQuestion(entry.Question);
        var parentKey = TextTools.NormalizeQuestion(entry.ParentQuestion ?? "");
        var profile = entry.IsGeneral ? "" : entry.ProfileName?.Trim() ?? "";
        var json = JsonSerializer.Serialize(entry.Coach, JsonResponseParser.Options);
        var now = clock.UtcNow.UtcDateTime;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.LearnHistory.FirstOrDefaultAsync(
                h => h.QuestionKey == questionKey && h.ParentKey == parentKey && h.IsGeneral == entry.IsGeneral && h.ProfileName == profile, ct);
            if (row is null)
            {
                db.LearnHistory.Add(new LearnHistoryEntity
                {
                    QuestionKey = questionKey,
                    Question = entry.Question.Trim(),
                    QuestionType = entry.QuestionType,
                    Technology = entry.Technology?.Trim() ?? "",
                    Seniority = entry.Seniority ?? "",
                    Source = entry.Source,
                    IsFollowUp = entry.IsFollowUp,
                    ParentQuestion = entry.ParentQuestion?.Trim() ?? "",
                    ParentKey = parentKey,
                    IsGeneral = entry.IsGeneral,
                    ProfileName = profile,
                    CoachJson = json,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    TimesSeen = 1,
                });
            }
            else
            {
                row.CoachJson = json;
                row.LastSeenAt = now;
                row.TimesSeen++;
                if (!string.IsNullOrEmpty(entry.QuestionType)) row.QuestionType = entry.QuestionType;
                if (!string.IsNullOrWhiteSpace(entry.Technology)) row.Technology = entry.Technology.Trim();
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Two writers recorded the same new question at once; the second try updates the row the first one made.
            }
        }
    }

    public async Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.LearnHistory.AsNoTracking().OrderByDescending(h => h.LastSeenAt).ThenByDescending(h => h.Id).ToListAsync(ct);

        var entries = new List<LearnHistoryEntry>(rows.Count);
        foreach (var row in rows)
        {
            CoachOutput? coach;
            try
            {
                coach = JsonSerializer.Deserialize<CoachOutput>(row.CoachJson, JsonResponseParser.Options);
            }
            catch (JsonException)
            {
                continue; // an unreadable entry is left out rather than breaking the whole library
            }
            if (coach is null) continue;

            entries.Add(new LearnHistoryEntry
            {
                Id = row.Id,
                Question = row.Question,
                QuestionType = row.QuestionType,
                Technology = NullIfEmpty(row.Technology),
                Seniority = NullIfEmpty(row.Seniority),
                Source = row.Source,
                IsFollowUp = row.IsFollowUp,
                ParentQuestion = NullIfEmpty(row.ParentQuestion),
                IsGeneral = row.IsGeneral,
                ProfileName = NullIfEmpty(row.ProfileName),
                Coach = coach,
                FirstSeenAt = row.FirstSeenAt,
                LastSeenAt = row.LastSeenAt,
                TimesSeen = row.TimesSeen,
            });
        }
        return entries;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.LearnHistory.Where(h => h.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.LearnHistory.ExecuteDeleteAsync(ct);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
