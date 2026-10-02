using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>The repository the app resolves: the real database normally, a throwaway in-memory one in Demo mode.</summary>
public sealed class RoutingTechBankRepository(ISettingsStore settings, TechBankRepository real, InMemoryTechBankRepository demo) : ITechBankRepository
{
    private ITechBankRepository Current => settings.Current.DemoMode ? demo : real;

    public Task<IReadOnlyList<string>?> GetTechnologiesAsync(string jobDescriptionFingerprint, CancellationToken ct = default)
        => Current.GetTechnologiesAsync(jobDescriptionFingerprint, ct);

    public Task SaveTechnologiesAsync(string jobDescriptionFingerprint, IReadOnlyList<string> technologies, CancellationToken ct = default)
        => Current.SaveTechnologiesAsync(jobDescriptionFingerprint, technologies, ct);

    public Task<IReadOnlyList<TechQuestion>> ListQuestionsAsync(string technology, Seniority seniority, CancellationToken ct = default)
        => Current.ListQuestionsAsync(technology, seniority, ct);

    public Task<TechQuestion> AddQuestionAsync(string technology, Seniority seniority, string question, string focus, CancellationToken ct = default)
        => Current.AddQuestionAsync(technology, seniority, question, focus, ct);

    public Task<CoachOutput?> GetAnswerAsync(int questionId, int answerWords, CancellationToken ct = default)
        => Current.GetAnswerAsync(questionId, answerWords, ct);

    public Task SaveAnswerAsync(int questionId, int answerWords, CoachOutput answer, CancellationToken ct = default)
        => Current.SaveAnswerAsync(questionId, answerWords, answer, ct);

    public Task<TechBankStats> GetStatsAsync(CancellationToken ct = default) => Current.GetStatsAsync(ct);

    public Task ClearAsync(CancellationToken ct = default) => Current.ClearAsync(ct);
}
