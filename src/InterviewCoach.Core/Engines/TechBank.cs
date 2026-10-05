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

    /// <summary>The most technologies kept for one job role.</summary>
    public const int MaxRoleTechnologies = 24;

    /// <summary>The key a role is saved under: lower case, with spaces tidied, so "Backend  Engineer" and "backend engineer" are one role.</summary>
    public static string RoleKey(string? role) => string.Join(' ', (role ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>The technologies already saved for a role, or null when it was never fetched. Never calls the model.</summary>
    public async Task<IReadOnlyList<string>?> GetSavedRoleTechnologiesAsync(string role, CancellationToken ct = default)
        => RoleKey(role).Length == 0 ? null : await repository.GetRoleTechnologiesAsync(RoleKey(role), ct);

    /// <summary>
    /// The main technologies a job role uses, for technology concepts sessions that start from a role instead of a job description. Asked of
    /// the model once per role and saved, so opening the same role again costs nothing; <paramref name="refresh"/> asks again and replaces
    /// the saved list (the user asked for it, because the list looks wrong or out of date).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRoleTechnologiesAsync(string role, bool refresh = false, CancellationToken ct = default)
    {
        var key = RoleKey(role);
        if (key.Length == 0) return [];
        if (!refresh && await repository.GetRoleTechnologiesAsync(key, ct) is { } known) return known;

        var vars = new Dictionary<string, string?> { ["JOB_ROLE"] = role.Trim(), ["MAX_TECHNOLOGIES"] = MaxRoleTechnologies.ToString() };
        var system = prompts.Render(PromptName.RoleTechnologies, vars);
        var reply = await llm.GetJsonAsync<TechTagsDto>(
            LlmRole.QuestionGenerator, system, [new ChatTurn(ChatTurnRole.User, PromptName.RoleTechnologies.UserMessage()!)], ct);

        var technologies = reply.Technologies
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Select(t => t.Length > 60 ? t[..60].TrimEnd() : t)
            .DistinctBy(t => t.ToLowerInvariant())
            .Take(MaxRoleTechnologies)
            .ToList();
        // An empty reply is not saved: it would stick, and the next visit would show nothing without ever asking again.
        if (technologies.Count > 0) await repository.SaveRoleTechnologiesAsync(key, role.Trim(), technologies, ct);
        return technologies;
    }

    /// <summary>The technologies already read from the profile's job description, or none. Never calls the model.</summary>
    public async Task<IReadOnlyList<string>> GetSavedTechnologiesAsync(CandidateProfile profile, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(profile.JobDescription)
            ? []
            : await repository.GetTechnologiesAsync(TextTools.Fingerprint(profile.JobDescription), ct) ?? [];

    /// <summary>The employers and highlights already read from the profile's resume, or none. Never calls the model.</summary>
    public async Task<IReadOnlyList<ResumeTopic>> GetSavedResumeTopicsAsync(CandidateProfile profile, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(profile.ResumeText)
            ? []
            : await repository.GetResumeTopicsAsync(TextTools.Fingerprint(profile.ResumeText), ct) ?? [];

    /// <summary>Employers kept from a resume, and highlights kept per employer.</summary>
    public const int MaxEmployers = 8;
    public const int MaxHighlights = 5;

    /// <summary>
    /// What the resume says was done, grouped by employer and project, so resume questions can take turns across all of them. One
    /// cheap call per distinct resume text; the answer is remembered (identified by a fingerprint of the text), so using the same
    /// resume again, or editing only the job description, costs nothing.
    /// </summary>
    public async Task<IReadOnlyList<ResumeTopic>> GetResumeTopicsAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ResumeText)) return [];

        var fingerprint = TextTools.Fingerprint(profile.ResumeText);
        if (await repository.GetResumeTopicsAsync(fingerprint, ct) is { } known) return known;

        var system = prompts.Render(PromptName.ResumeTopics, PromptVars.ForProfile(profile));
        var reply = await llm.GetJsonAsync<ResumeTopicsDto>(
            LlmRole.QuestionGenerator, system, [new ChatTurn(ChatTurnRole.User, PromptName.ResumeTopics.UserMessage()!)], ct);

        var topics = new List<ResumeTopic>();
        foreach (var entry in reply.Topics.Take(MaxEmployers))
        {
            var employer = entry.Employer?.Trim() ?? "";
            if (employer.Length == 0) employer = "Other work";
            var project = entry.Project?.Trim() ?? "";
            foreach (var highlight in (entry.Highlights ?? []).Select(h => h?.Trim() ?? "").Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxHighlights))
                topics.Add(new ResumeTopic(employer, project, highlight));
        }
        await repository.SaveResumeTopicsAsync(fingerprint, topics, ct);
        return topics;
    }

    /// <summary>How many questions one batch call asks for.</summary>
    public const int BatchSize = 10;

    /// <summary>
    /// A saved question for this technology and level that has not come up this session. Only when every saved one has
    /// been seen does the model write more, and then a batch of <see cref="BatchSize"/> in one call, most commonly asked
    /// first and spread over different areas. Writing them one at a time drifted into obscure corners and the same
    /// "What's the difference between" form, and cost more per question. If the batch fails or adds nothing new, one
    /// question is written the old way.
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

        var batch = await TryWriteBatchAsync(technology, seniority, avoid, ct);
        if (batch.Count > 0) return batch[0];

        QuestionDto written = new();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            written = await llm.GetJsonAsync<QuestionDto>(
                LlmRole.QuestionGenerator,
                RenderPrompt(PromptName.QuestionGenerator, technology, seniority, avoid),
                [new ChatTurn(ChatTurnRole.User, PromptName.QuestionGenerator.UserMessage()!)], ct);
            if (string.IsNullOrWhiteSpace(written.Question))
                throw new LlmException("The model returned an empty question.");
            if (!avoid.Any(a => TextTools.SameQuestion(a, written.Question))) break;
        }

        return await repository.AddQuestionAsync(technology, seniority, written.Question.Trim(), written.Focus, ct);
    }

    private async Task<List<TechQuestion>> TryWriteBatchAsync(string technology, Seniority seniority, List<string> avoid, CancellationToken ct)
    {
        var added = new List<TechQuestion>();
        try
        {
            var batch = await llm.GetJsonAsync<QuestionBatchDto>(
                LlmRole.QuestionGenerator,
                RenderPrompt(PromptName.QuestionBatch, technology, seniority, avoid),
                [new ChatTurn(ChatTurnRole.User, PromptName.QuestionBatch.UserMessage()!)], ct);

            foreach (var item in batch.Questions.Take(BatchSize + 5))
            {
                var text = item.Question?.Trim() ?? "";
                if (text.Length == 0 || avoid.Any(a => TextTools.IsNearDuplicate(a, text))) continue;
                added.Add(await repository.AddQuestionAsync(technology, seniority, text, item.Area ?? "", ct));
                avoid.Add(text);
                if (added.Count == BatchSize) break;
            }
        }
        catch (LlmException)
        {
            // Not worth failing the session: the caller writes a single question instead.
        }
        return added;
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
        string questionType = "technical_concept", CancellationToken ct = default, string? answerRules = null)
    {
        var vars = PromptVars.Generic(seniority);
        vars["ANSWER_RULES"] = PromptVars.AnswerRules(answerRules);
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

    private string RenderPrompt(PromptName name, string technology, Seniority seniority, IReadOnlyList<string> avoid)
    {
        var vars = PromptVars.Generic(seniority);
        vars["QUESTION_TYPES"] = "- " + QuestionType.TechnicalConcept.Id();
        vars["EMPLOYMENT_TYPE"] = null; // saved questions are general, shared by every kind of interview
        vars["ALREADY_ASKED"] = PromptVars.AlreadyAsked(avoid);
        vars["FOCUS_TECHNOLOGY"] = technology;
        vars["RESUME_FOCUS"] = null; // saved questions are general and never about a resume
        return prompts.Render(name, vars);
    }
}
