using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Fakes;

/// <summary>
/// A library that lives only in memory. Demo mode uses it so made-up demo questions never reach the real database, and tests
/// use it to avoid touching disk. Entries behave like the real repository does: the same question and answer kind is one entry.
/// </summary>
public sealed class InMemoryLearnHistory(Func<DateTime>? now = null) : ILearnHistory
{
    private readonly object _gate = new();
    private readonly List<LearnHistoryEntry> _entries = [];
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private int _nextId = 1;

    private static bool Same(LearnHistoryEntry a, LearnHistoryEntry b)
        => TextTools.SameQuestion(a.Question, b.Question)
           && TextTools.SameQuestion(a.ParentQuestion ?? "", b.ParentQuestion ?? "")
           && a.IsGeneral == b.IsGeneral
           && (a.IsGeneral || string.Equals(a.ProfileName?.Trim() ?? "", b.ProfileName?.Trim() ?? "", StringComparison.Ordinal));

    public Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(e => Same(e, entry));
            var now = _now();
            if (existing is null)
            {
                _entries.Add(new LearnHistoryEntry
                {
                    Id = _nextId++, Question = entry.Question.Trim(), QuestionType = entry.QuestionType, Technology = entry.Technology,
                    Seniority = entry.Seniority, Source = entry.Source, IsFollowUp = entry.IsFollowUp, ParentQuestion = entry.ParentQuestion,
                    IsGeneral = entry.IsGeneral, ProfileName = entry.IsGeneral ? null : entry.ProfileName, Coach = entry.Coach,
                    FirstSeenAt = now, LastSeenAt = now, TimesSeen = 1,
                });
            }
            else
            {
                _entries[_entries.IndexOf(existing)] = new LearnHistoryEntry
                {
                    Id = existing.Id, Question = existing.Question,
                    QuestionType = string.IsNullOrEmpty(entry.QuestionType) ? existing.QuestionType : entry.QuestionType,
                    Technology = string.IsNullOrWhiteSpace(entry.Technology) ? existing.Technology : entry.Technology,
                    Seniority = existing.Seniority, Source = existing.Source, IsFollowUp = existing.IsFollowUp, ParentQuestion = existing.ParentQuestion,
                    IsGeneral = existing.IsGeneral, ProfileName = existing.ProfileName, Coach = entry.Coach,
                    FirstSeenAt = existing.FirstSeenAt, LastSeenAt = now, TimesSeen = existing.TimesSeen + 1,
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<LearnHistoryEntry>>(_entries.OrderByDescending(e => e.LastSeenAt).ThenByDescending(e => e.Id).ToList());
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        lock (_gate) _entries.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate) _entries.Clear();
        return Task.CompletedTask;
    }
}
