using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Abstractions;

/// <summary>
/// Storage for the technology bank: technologies found in job descriptions, general technical questions per
/// technology and seniority, and the general answers written for them. Nothing here is tied to a profile or a resume,
/// so it survives editing both.
/// </summary>
public interface ITechBankRepository
{
    /// <summary>Technologies previously extracted from a job description with this fingerprint, or null if never extracted.</summary>
    Task<IReadOnlyList<string>?> GetTechnologiesAsync(string jobDescriptionFingerprint, CancellationToken ct = default);

    Task SaveTechnologiesAsync(string jobDescriptionFingerprint, IReadOnlyList<string> technologies, CancellationToken ct = default);

    /// <summary>The technologies saved for a job role (the key is the role normalised by <c>TechBank.RoleKey</c>), or null if never fetched.</summary>
    Task<IReadOnlyList<string>?> GetRoleTechnologiesAsync(string roleKey, CancellationToken ct = default);

    Task SaveRoleTechnologiesAsync(string roleKey, string role, IReadOnlyList<string> technologies, CancellationToken ct = default);

    /// <summary>The employers, projects and highlights previously read from a resume with this fingerprint, or null if never read.</summary>
    Task<IReadOnlyList<ResumeTopic>?> GetResumeTopicsAsync(string resumeFingerprint, CancellationToken ct = default);

    Task SaveResumeTopicsAsync(string resumeFingerprint, IReadOnlyList<ResumeTopic> topics, CancellationToken ct = default);

    /// <summary>Oldest first. The technology is matched ignoring case.</summary>
    Task<IReadOnlyList<TechQuestion>> ListQuestionsAsync(string technology, Seniority seniority, CancellationToken ct = default);

    /// <summary>Saves a question, or returns the existing one if the same question is already stored for that technology and level.</summary>
    Task<TechQuestion> AddQuestionAsync(string technology, Seniority seniority, string question, string focus, CancellationToken ct = default);

    /// <param name="answerWords">The requested answer length in words, or 0 for "interviewer norm".</param>
    Task<CoachOutput?> GetAnswerAsync(int questionId, int answerWords, CancellationToken ct = default);

    Task SaveAnswerAsync(int questionId, int answerWords, CoachOutput answer, CancellationToken ct = default);

    Task<TechBankStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>Removes saved questions and answers. Extracted technologies are kept: they are cheap and still correct.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
