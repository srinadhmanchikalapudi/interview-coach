using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Persistence;

public sealed class TechBankRepository(IDbContextFactory<AppDbContext> factory, IClock clock) : ITechBankRepository
{
    private static string Key(string technology) => technology.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<string>?> GetTechnologiesAsync(string jobDescriptionFingerprint, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.JdTechnologies.AsNoTracking().FirstOrDefaultAsync(j => j.Fingerprint == jobDescriptionFingerprint, ct);
        return row is null ? null : JsonSerializer.Deserialize<List<string>>(row.TechnologiesJson) ?? [];
    }

    public async Task SaveTechnologiesAsync(string jobDescriptionFingerprint, IReadOnlyList<string> technologies, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(technologies);
        var row = await db.JdTechnologies.FirstOrDefaultAsync(j => j.Fingerprint == jobDescriptionFingerprint, ct);
        if (row is null)
            db.JdTechnologies.Add(new JdTechnologiesEntity { Fingerprint = jobDescriptionFingerprint, TechnologiesJson = json, CreatedAt = clock.UtcNow.UtcDateTime });
        else
            row.TechnologiesJson = json;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ResumeTopic>?> GetResumeTopicsAsync(string resumeFingerprint, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ResumeTopics.AsNoTracking().FirstOrDefaultAsync(r => r.Fingerprint == resumeFingerprint, ct);
        if (row is null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ResumeTopic>>(row.TopicsJson) ?? [];
        }
        catch (JsonException)
        {
            return null; // unreadable: treated as never read, so it is read again
        }
    }

    public async Task SaveResumeTopicsAsync(string resumeFingerprint, IReadOnlyList<ResumeTopic> topics, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(topics);
        var row = await db.ResumeTopics.FirstOrDefaultAsync(r => r.Fingerprint == resumeFingerprint, ct);
        if (row is null)
            db.ResumeTopics.Add(new ResumeTopicsEntity { Fingerprint = resumeFingerprint, TopicsJson = json, CreatedAt = clock.UtcNow.UtcDateTime });
        else
            row.TopicsJson = json;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TechQuestion>> ListQuestionsAsync(string technology, Seniority seniority, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var key = Key(technology);
        var rows = await db.TechQuestions.AsNoTracking()
            .Where(q => q.TechnologyKey == key && q.Seniority == seniority)
            .OrderBy(q => q.Id)
            .ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<TechQuestion> AddQuestionAsync(string technology, Seniority seniority, string question, string focus, CancellationToken ct = default)
    {
        var technologyKey = Key(technology);
        var questionKey = TextTools.NormalizeQuestion(question);

        await using var db = await factory.CreateDbContextAsync(ct);
        var existing = await FindAsync(db, technologyKey, seniority, questionKey, ct);
        if (existing is not null) return ToModel(existing);

        var row = new TechQuestionEntity
        {
            TechnologyKey = technologyKey,
            Technology = technology.Trim(),
            Seniority = seniority,
            QuestionKey = questionKey,
            Question = question.Trim(),
            Focus = focus.Trim(),
            CreatedAt = clock.UtcNow.UtcDateTime,
        };
        db.TechQuestions.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone stored the same question between the check and the insert; use theirs.
            await using var again = await factory.CreateDbContextAsync(ct);
            var winner = await FindAsync(again, technologyKey, seniority, questionKey, ct);
            if (winner is not null) return ToModel(winner);
            throw;
        }
        return ToModel(row);
    }

    public async Task<CoachOutput?> GetAnswerAsync(int questionId, int answerWords, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.TechAnswers.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TechQuestionId == questionId && a.AnswerWords == answerWords, ct);
        if (row is null) return null;
        try
        {
            return JsonSerializer.Deserialize<CoachOutput>(row.CoachJson, JsonResponseParser.Options);
        }
        catch (JsonException)
        {
            return null; // an unreadable entry is treated as missing, so a fresh answer replaces it
        }
    }

    public async Task SaveAnswerAsync(int questionId, int answerWords, CoachOutput answer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(answer, JsonResponseParser.Options);
        var row = await db.TechAnswers.FirstOrDefaultAsync(a => a.TechQuestionId == questionId && a.AnswerWords == answerWords, ct);
        if (row is null)
            db.TechAnswers.Add(new TechAnswerEntity { TechQuestionId = questionId, AnswerWords = answerWords, CoachJson = json, CreatedAt = clock.UtcNow.UtcDateTime });
        else
            row.CoachJson = json;
        await db.SaveChangesAsync(ct);
    }

    public async Task<TechBankStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new TechBankStats(await db.TechQuestions.CountAsync(ct), await db.TechAnswers.CountAsync(ct));
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.TechAnswers.ExecuteDeleteAsync(ct);
        await db.TechQuestions.ExecuteDeleteAsync(ct);
    }

    private static Task<TechQuestionEntity?> FindAsync(AppDbContext db, string technologyKey, Seniority seniority, string questionKey, CancellationToken ct)
        => db.TechQuestions.AsNoTracking().FirstOrDefaultAsync(
            q => q.TechnologyKey == technologyKey && q.Seniority == seniority && q.QuestionKey == questionKey, ct);

    private static TechQuestion ToModel(TechQuestionEntity row) => new()
    {
        Id = row.Id,
        Technology = row.Technology,
        Seniority = row.Seniority,
        Question = row.Question,
        Focus = row.Focus,
        CreatedAt = row.CreatedAt,
    };
}
