using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Engines;

/// <summary>
/// General technical-concept questions and answers, kept per technology and seniority and reused across sessions,
/// resumes and job descriptions. Because nothing here is sent your resume, an entry stays valid however often you
/// edit it, and reusing one costs no tokens at all.
/// </summary>
public sealed class TechBank(ITechBankRepository repository, ILlmService llm, IPromptLibrary prompts, Func<double>? random = null)
{
    public const int MaxTechnologies = 8;

    /// <summary>Stands in for the resume when a general answer is written, so the Coach writes one that fits anybody.</summary>
    public const string GeneralAnswerNote =
        "(No resume is included on purpose. Write a strong general answer that does not depend on any one person's background. " +
        "Wherever the candidate's own experience would go, use a bracketed placeholder such as [where you have used it].)";

    /// <summary>
    /// Key for "no word count requested" in saved answers. It was 0 while the Coach was told nothing about length and wrote
    /// answers far longer than interviewers expect; those saved answers are never matched again and a fresh one is written.
    /// </summary>
    public const int InterviewerNormKey = -1;

    private static int Key(int? answerWords) => answerWords ?? InterviewerNormKey;

    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public Task<TechBankStats> GetStatsAsync(CancellationToken ct = default) => repository.GetStatsAsync(ct);

    public Task ClearAsync(CancellationToken ct = default) => repository.ClearAsync(ct);

    /// <summary>
    /// The main technologies in the profile's job description. Asked of the model once per distinct job description
    /// (identified by a fingerprint of its text) and remembered, so editing your resume costs nothing and re-pasting the
    /// same job description costs nothing either.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTechnologiesAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.JobDescription)) return [];

        var fingerprint = TextTools.Fingerprint(profile.JobDescription);
        if (await repository.GetTechnologiesAsync(fingerprint, ct) is { } known) return known;

        var system = prompts.Render(PromptName.TechTags, PromptVars.ForProfile(profile));
        var reply = await llm.GetJsonAsync<TechTagsDto>(
            LlmRole.QuestionGenerator, system, [new ChatTurn(ChatTurnRole.User, PromptName.TechTags.UserMessage()!)], ct);

        var technologies = reply.Technologies
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .DistinctBy(t => t.ToLowerInvariant())
            .Take(MaxTechnologies)
            .ToList();
        await repository.SaveTechnologiesAsync(fingerprint, technologies, ct);
        return technologies;
    }

    /// <summary>
    /// A saved question for this technology and level that has not come up this session. Only when every saved one has
    /// been seen is a new question written (and saved), so the bank grows exactly as fast as it is used up.
    /// </summary>
    public async Task<TechQuestion> NextQuestionAsync(
        string technology, Seniority seniority, IReadOnlyCollection<string> askedThisSession, CancellationToken ct = default)
    {
        var saved = await repository.ListQuestionsAsync(technology, seniority, ct);
        var unseen = saved.Where(q => !askedThisSession.Any(a => TextTools.SameQuestion(a, q.Question))).ToList();
        if (unseen.Count > 0)
            return unseen[Math.Min(unseen.Count - 1, (int)(_random() * unseen.Count))];

        var avoid = new List<string>();
        foreach (var question in saved.Select(q => q.Question).Concat(askedThisSession))
            if (!avoid.Any(a => TextTools.SameQuestion(a, question))) avoid.Add(question);
        QuestionDto written = new();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            written = await llm.GetJsonAsync<QuestionDto>(
                LlmRole.QuestionGenerator,
                RenderGeneratorPrompt(technology, seniority, avoid),
                [new ChatTurn(ChatTurnRole.User, PromptName.QuestionGenerator.UserMessage()!)], ct);
            if (string.IsNullOrWhiteSpace(written.Question))
                throw new LlmException("The model returned an empty question.");
            if (!avoid.Any(a => TextTools.SameQuestion(a, written.Question))) break;
        }

        return await repository.AddQuestionAsync(technology, seniority, written.Question.Trim(), written.Focus, ct);
    }

    public Task<CoachOutput?> GetSavedAnswerAsync(int questionId, int? answerWords, CancellationToken ct = default)
        => repository.GetAnswerAsync(questionId, Key(answerWords), ct);

    public Task SaveAnswerAsync(int questionId, int? answerWords, CoachOutput answer, CancellationToken ct = default)
        => repository.SaveAnswerAsync(questionId, Key(answerWords), answer, ct);

    /// <summary>
    /// Writes a general answer: the Coach is told there is no resume and no job description. Much cheaper than a tailored
    /// answer (a third of the input) and, once saved, free to reuse. <paramref name="transcript"/> is for follow-ups.
    /// </summary>
    public async Task<CoachOutput> WriteGeneralAnswerAsync(
        string question, Seniority seniority, int? answerWords, string? transcript,
        string questionType = "technical_concept", CancellationToken ct = default)
    {
        var vars = PromptVars.Generic(seniority);
        vars["RESUME"] = GeneralAnswerNote;
        vars["MODE"] = "learn";
        vars["QUESTION"] = question;
        vars["TRANSCRIPT"] = transcript;
        vars["QUESTION_TYPE"] = questionType;
        vars["EMPLOYMENT_TYPE"] = null; // saved answers are general, shared by every kind of interview
        vars["ANSWER_LENGTH"] = AnswerLength.Describe(answerWords, questionType);
        vars["CANDIDATE_ANSWER"] = null;
        vars["PREVIOUS_ATTEMPT"] = null;
        vars["INPUT_METHOD"] = null;
        vars["DURATION_SECONDS"] = null;
        vars["WORD_COUNT"] = null;

        var coach = await llm.GetJsonAsync<CoachOutput>(
            LlmRole.Coach, prompts.Render(PromptName.Coach, vars),
            [new ChatTurn(ChatTurnRole.User, PromptName.Coach.UserMessage()!)], ct);
        return coach.ForLearning();
    }

    private string RenderGeneratorPrompt(string technology, Seniority seniority, IReadOnlyList<string> avoid)
    {
        var vars = PromptVars.Generic(seniority);
        vars["QUESTION_TYPES"] = "- " + QuestionType.TechnicalConcept.Id();
        vars["EMPLOYMENT_TYPE"] = null; // saved questions are general, shared by every kind of interview
        vars["ALREADY_ASKED"] = PromptVars.AlreadyAsked(avoid);
        vars["FOCUS_TECHNOLOGY"] = technology;
        return prompts.Render(PromptName.QuestionGenerator, vars);
    }
}
