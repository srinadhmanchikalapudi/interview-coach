using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Persistence;

public sealed class PracticeHistoryRepository(IDbContextFactory<AppDbContext> factory, IClock clock) : IPracticeHistory
{
    public async Task RecordAsync(PracticeRecord record, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.PracticeAttempts.Add(new PracticeAttemptEntity
        {
            Question = record.Question.Trim(),
            QuestionType = record.QuestionType,
            Technology = record.Technology?.Trim() ?? "",
            Seniority = record.Seniority ?? "",
            Source = record.Source,
            IsFollowUp = record.IsFollowUp,
            ParentQuestion = record.ParentQuestion?.Trim() ?? "",
            ProfileName = record.ProfileName?.Trim() ?? "",
            AttemptNumber = record.AttemptNumber,
            AnswerText = record.AnswerText,
            InputMethod = record.InputMethod,
            DurationSeconds = record.DurationSeconds,
            WordCount = record.WordCount,
            CoachJson = JsonSerializer.Serialize(record.Coach, JsonResponseParser.Options),
            CreatedAt = clock.UtcNow.UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PracticeRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.PracticeAttempts.AsNoTracking().OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).ToListAsync(ct);

        var records = new List<PracticeRecord>(rows.Count);
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

            records.Add(new PracticeRecord
            {
                Id = row.Id,
                Question = row.Question,
                QuestionType = row.QuestionType,
                Technology = NullIfEmpty(row.Technology),
                Seniority = NullIfEmpty(row.Seniority),
                Source = row.Source,
                IsFollowUp = row.IsFollowUp,
                ParentQuestion = NullIfEmpty(row.ParentQuestion),
                ProfileName = NullIfEmpty(row.ProfileName),
                AttemptNumber = row.AttemptNumber,
                AnswerText = row.AnswerText,
                InputMethod = row.InputMethod,
                DurationSeconds = row.DurationSeconds,
                WordCount = row.WordCount,
                Coach = coach,
                CreatedAt = row.CreatedAt,
            });
        }
        return records;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.PracticeAttempts.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.PracticeAttempts.ExecuteDeleteAsync(ct);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
