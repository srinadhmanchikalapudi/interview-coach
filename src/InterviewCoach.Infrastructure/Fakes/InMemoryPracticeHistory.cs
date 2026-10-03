using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Fakes;

/// <summary>
/// Practice attempts that live only in memory. Demo mode uses it so made-up answers never reach the real database, and tests use it
/// to avoid touching disk.
/// </summary>
public sealed class InMemoryPracticeHistory(Func<DateTime>? now = null) : IPracticeHistory
{
    private readonly object _gate = new();
    private readonly List<PracticeRecord> _records = [];
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private int _nextId = 1;

    public Task RecordAsync(PracticeRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _records.Add(new PracticeRecord
            {
                Id = _nextId++, Question = record.Question.Trim(), QuestionType = record.QuestionType, Technology = record.Technology,
                Seniority = record.Seniority, Source = record.Source, IsFollowUp = record.IsFollowUp, ParentQuestion = record.ParentQuestion,
                ProfileName = record.ProfileName, AttemptNumber = record.AttemptNumber, AnswerText = record.AnswerText,
                InputMethod = record.InputMethod, DurationSeconds = record.DurationSeconds, WordCount = record.WordCount,
                Coach = record.Coach, CreatedAt = _now(),
            });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PracticeRecord>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PracticeRecord>>(_records.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList());
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        lock (_gate) _records.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate) _records.Clear();
        return Task.CompletedTask;
    }
}
