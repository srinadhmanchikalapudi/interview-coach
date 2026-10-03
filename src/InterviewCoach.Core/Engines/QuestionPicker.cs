using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Engines;

/// <summary>
/// Chooses the next question for a session. Learn and Practice both use it, so they ask the same kind of question in the same way:
/// saved technical questions per technology (or batches of common ones), questions the model writes from the resume and job
/// description with each employer taking its turn, and a check that nothing already asked comes round again in other words.
/// It does not write any answer; the caller does that. No UI references.
/// </summary>
public sealed class QuestionPicker(ILlmService llm, IPromptLibrary prompts, TechBank? bank, Func<double> random)
{
    private CandidateProfile _profile = new();
    private IReadOnlyCollection<QuestionType> _allowed = [];
    private EmploymentType _employment = EmploymentType.FullTime;
    private IReadOnlyList<string> _focus = [];
    private string? _lastTechnology;
    private readonly List<string> _asked = [];
    private IReadOnlyList<string>? _technologies;
    private IReadOnlyList<ResumeTopic>? _resumeTopics;
    private ResumeFocusPicker _focusPicker = new(random);

    /// <summary>The questions asked so far this session, which the generator is told not to repeat.</summary>
    public IReadOnlyList<string> Asked => _asked;

    /// <summary>The technologies found in the job description, once known (empty if none or if the bank is not in use).</summary>
    public IReadOnlyList<string> Technologies => _technologies ?? [];

    /// <summary>Starts a new session: forgets what was asked and which parts of the resume were used.</summary>
    public void Start(
        CandidateProfile profile, IReadOnlyCollection<QuestionType> allowedTypes, IReadOnlyList<string>? focusTechnologies, EmploymentType employment)
    {
        _profile = profile;
        _employment = employment;
        _allowed = allowedTypes;
        _focus = focusTechnologies is { Count: > 0 }
            ? focusTechnologies.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        _lastTechnology = null;
        _asked.Clear();
        _technologies = null;
        _resumeTopics = null;
        _focusPicker = new ResumeFocusPicker(random);
    }

    /// <summary>Records a question as asked (a duplicate entry is harmless).</summary>
    public void Add(string question) => _asked.Add(question);

    /// <summary>Records a question as asked unless it is already on the list (used for follow-ups).</summary>
    public void AddIfNew(string question)
    {
        if (!_asked.Contains(question)) _asked.Add(question);
    }


    /// <summary>
    /// Questions come from up to two places: the technology source (saved general questions about one technology, or
    /// questions the model writes about a chosen technology) and the model, which writes everything else from the
    /// resume and job description. Each ticked type other than Technical concept counts as one share, and the technology
    /// source counts as one share, so Technical concept and By technology together are still just one share.
    /// </summary>
    public async Task<LearnItem> NextAsync(CancellationToken ct)
    {
        var tech = await TryTechnologyQuestionAsync(ct);
        if (tech.Item is not null) return tech.Item;

        // While the technology source is serving its share, the model is told not to write technical-concept questions,
        // so the mix stays as asked. If the source could not serve this one, the model gets the original filter back.
        var allowed = tech.Serving && !tech.Failed ? ModelTypes() : _allowed;
        return await GenerateQuestionAsync(allowed, focusTechnology: null, ct);
    }

    private bool FocusGiven => _focus.Count > 0;

    /// <summary>True when the filter asks for technology questions: By technology, Technical concept, or no filter at all.</summary>
    private bool TechnologySourceWanted => FocusGiven || _allowed.Count == 0 || _allowed.Contains(QuestionType.TechnicalConcept);

    /// <summary>The question types the model writes. Technical concept belongs to the technology source, not the model.</summary>
    private IReadOnlyCollection<QuestionType> ModelTypes()
    {
        var anyType = _allowed.Count == 0 && !FocusGiven;
        var pool = anyType ? QuestionTypes.Applicable(_employment) : _allowed; // 'Any' never includes the other kind of interview's own types
        return pool.Where(t => t != QuestionType.TechnicalConcept).Distinct().ToList();
    }

    private async Task<(LearnItem? Item, bool Serving, bool Failed)> TryTechnologyQuestionAsync(CancellationToken ct)
    {
        if (!TechnologySourceWanted) return (null, false, false);

        // The technologies to draw from: the ones picked up front, or else every technology in the job description.
        IReadOnlyList<string> pool;
        if (FocusGiven)
        {
            pool = _focus;
        }
        else
        {
            if (bank is null) return (null, false, false);
            if (_technologies is null)
            {
                try { _technologies = await bank.GetTechnologiesAsync(_profile, ct); }
                catch (LlmException) { _technologies = []; } // not worth failing a session over: the model writes the questions instead
            }
            if (_technologies.Count == 0) return (null, false, false);
            pool = _technologies;
        }

        // With other types ticked, the technology source gets its fair share and the model the rest.
        var shares = ModelTypes().Count + 1;
        if (shares > 1 && random() >= 1.0 / shares) return (null, true, false);

        var technology = PickTechnology(pool);

        // Technologies were picked and nothing else was asked for: asking about something else would ignore the choice,
        // so a failure is shown with Retry instead of falling back to the model.
        var onlyThisSource = FocusGiven && ModelTypes().Count == 0;

        if (bank is null)
        {
            // The saved bank is switched off: the model writes a question about the technology, tailored to the resume as usual.
            var written = await GenerateQuestionAsync([QuestionType.TechnicalConcept], technology, ct);
            return (written, true, false);
        }

        TechQuestion question;
        try
        {
            question = await bank.NextQuestionAsync(technology, _profile.Seniority, _asked, ct);
        }
        catch (LlmException) when (!onlyThisSource)
        {
            return (null, true, true);
        }
        if (WasAsked(question.Question))
        {
            if (onlyThisSource)
                throw new LlmException($"Could not come up with a new question about {technology}. Try again, or pick another technology.");
            return (null, true, true);
        }

        var item = new LearnItem
        {
            Question = question.Question,
            QuestionType = QuestionType.TechnicalConcept.Id(),
            Focus = question.Focus,
            Source = "fundamentals",
            Technology = question.Technology,
            IsGeneric = true,
            TechQuestionId = question.Id,
        };
        return (item, true, false);
    }

    /// <summary>A random technology from the pool, avoiding the one just asked about when there is a choice.</summary>
    private string PickTechnology(IReadOnlyList<string> pool)
    {
        var candidates = pool.Count > 1 && _lastTechnology is not null
            ? pool.Where(t => !t.Equals(_lastTechnology, StringComparison.OrdinalIgnoreCase)).ToList()
            : pool.ToList();
        var chosen = candidates[Math.Min(candidates.Count - 1, (int)(random() * candidates.Count))];
        _lastTechnology = chosen;
        return chosen;
    }

    private async Task<LearnItem> GenerateQuestionAsync(IReadOnlyCollection<QuestionType> allowed, string? focusTechnology, CancellationToken ct)
    {
        var focus = await PickResumeFocusAsync(allowed, focusTechnology, ct);
        QuestionDto question = new();
        // The prompt already gets the list of earlier questions. If the model asks the same thing again anyway, in the same or in
        // other words, it is told so and asked once more.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            List<ChatTurn> turns = [UserTurn(PromptName.QuestionGenerator)];
            if (attempt > 0)
            {
                turns.Add(new ChatTurn(ChatTurnRole.Assistant, System.Text.Json.JsonSerializer.Serialize(new { question = question.Question })));
                turns.Add(new ChatTurn(ChatTurnRole.User, "That is too close to a question you already asked. Give a different one, about a different topic."));
            }
            question = await llm.GetJsonAsync<QuestionDto>(
                LlmRole.QuestionGenerator, RenderGeneratorPrompt(allowed, focusTechnology, focus), turns, ct);
            if (string.IsNullOrWhiteSpace(question.Question))
                throw new LlmException("The model returned an empty question.");
            if (!IsRepeat(question.Question)) break;
        }

        // The employer and highlight are used up only by a question that was really about the resume.
        if (focus is not null && question.QuestionType.Equals(QuestionType.ResumeDeepDive.Id(), StringComparison.OrdinalIgnoreCase))
            _focusPicker.Commit(focus);

        return new LearnItem
        {
            Question = question.Question.Trim(),
            QuestionType = question.QuestionType,
            Focus = question.Focus,
            Source = question.Source,
            Technology = focusTechnology,
        };
    }

    /// <summary>
    /// What a resume question should be about: the least used employer and highlight, so the whole resume gets its turn. Only when
    /// resume questions are possible, the technology bank is in use (it remembers the topics) and the model is writing the question
    /// freely. Reading the topics costs one cheap call per resume; if it fails the question is written without a focus.
    /// </summary>
    private async Task<ResumeFocus?> PickResumeFocusAsync(IReadOnlyCollection<QuestionType> allowed, string? focusTechnology, CancellationToken ct)
    {
        if (bank is null || focusTechnology is not null) return null;
        if (allowed.Count > 0 && !allowed.Contains(QuestionType.ResumeDeepDive)) return null;

        if (_resumeTopics is null)
        {
            try { _resumeTopics = await bank.GetResumeTopicsAsync(_profile, ct); }
            catch (LlmException) { _resumeTopics = []; }
        }
        return _focusPicker.Pick(_resumeTopics);
    }

    private bool IsRepeat(string question) => _asked.Any(a => TextTools.IsNearDuplicate(a, question));

    private string RenderGeneratorPrompt(IReadOnlyCollection<QuestionType> allowed, string? focusTechnology, ResumeFocus? focus)
    {
        var vars = PromptVars.ForProfile(_profile);
        vars["QUESTION_TYPES"] = QuestionTypes.RenderFilter(allowed);
        vars["ALREADY_ASKED"] = PromptVars.AlreadyAsked(_asked);
        vars["FOCUS_TECHNOLOGY"] = focusTechnology; // null renders as "(none)"
        vars["RESUME_FOCUS"] = focus?.Describe();
        vars["EMPLOYMENT_TYPE"] = _employment.PromptValue();
        return prompts.Render(PromptName.QuestionGenerator, vars);
    }

    private static ChatTurn UserTurn(PromptName name) => new(ChatTurnRole.User, name.UserMessage()!);

    private bool WasAsked(string question) => _asked.Any(a => TextTools.SameQuestion(a, question));
}
