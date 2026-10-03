using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Fakes;

/// <summary>
/// A technology bank that lives only in memory. Demo mode uses it so the made-up demo questions never reach the real
/// database, and tests use it to avoid touching disk.
/// </summary>
public sealed class InMemoryTechBankRepository : ITechBankRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<string>> _technologies = [];
    private readonly Dictionary<string, List<ResumeTopic>> _resumeTopics = [];
    private readonly List<TechQuestion> _questions = [];
    private readonly Dictionary<(int QuestionId, int Words), CoachOutput> _answers = [];
    private int _nextId = 1;

    private static string Key(string technology) => technology.Trim().ToLowerInvariant();

    public Task<IReadOnlyList<string>?> GetTechnologiesAsync(string jobDescriptionFingerprint, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>?>(_technologies.TryGetValue(jobDescriptionFingerprint, out var list) ? list.ToList() : null);
    }

    public Task SaveTechnologiesAsync(string jobDescriptionFingerprint, IReadOnlyList<string> technologies, CancellationToken ct = default)
    {
        lock (_gate) _technologies[jobDescriptionFingerprint] = technologies.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ResumeTopic>?> GetResumeTopicsAsync(string resumeFingerprint, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ResumeTopic>?>(_resumeTopics.TryGetValue(resumeFingerprint, out var list) ? list.ToList() : null);
    }

    public Task SaveResumeTopicsAsync(string resumeFingerprint, IReadOnlyList<ResumeTopic> topics, CancellationToken ct = default)
    {
        lock (_gate) _resumeTopics[resumeFingerprint] = topics.ToList();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TechQuestion>> ListQuestionsAsync(string technology, Seniority seniority, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TechQuestion>>(_questions
                .Where(q => Key(q.Technology) == Key(technology) && q.Seniority == seniority)
                .OrderBy(q => q.Id).ToList());
    }

    public Task<TechQuestion> AddQuestionAsync(string technology, Seniority seniority, string question, string focus, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _questions.FirstOrDefault(q =>
                Key(q.Technology) == Key(technology) && q.Seniority == seniority && TextTools.SameQuestion(q.Question, question));
            if (existing is not null) return Task.FromResult(existing);

            var added = new TechQuestion
            {
                Id = _nextId++, Technology = technology.Trim(), Seniority = seniority, Question = question.Trim(), Focus = focus.Trim(),
                CreatedAt = DateTime.UtcNow,
            };
            _questions.Add(added);
            return Task.FromResult(added);
        }
    }

    public Task<CoachOutput?> GetAnswerAsync(int questionId, int answerWords, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_answers.GetValueOrDefault((questionId, answerWords)));
    }

    public Task SaveAnswerAsync(int questionId, int answerWords, CoachOutput answer, CancellationToken ct = default)
    {
        lock (_gate) _answers[(questionId, answerWords)] = answer;
        return Task.CompletedTask;
    }

    public Task<TechBankStats> GetStatsAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(new TechBankStats(_questions.Count, _answers.Count));
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _questions.Clear();
            _answers.Clear();
        }
        return Task.CompletedTask;
    }
}
