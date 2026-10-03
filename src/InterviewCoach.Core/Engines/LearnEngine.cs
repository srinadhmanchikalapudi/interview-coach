using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Engines;

public enum LearnPhase
{
    Idle,
    /// <summary>Choosing the next question.</summary>
    GeneratingQuestion,
    /// <summary>The question is known and on screen; the Coach is still writing the model answer.</summary>
    LoadingAnswer,
    /// <summary>Question and Coach output are both available.</summary>
    Ready,
    /// <summary>An LLM call failed. State is kept; <see cref="LearnEngine.RetryAsync"/> repeats the failed step.</summary>
    Failed,
}

/// <summary>One question on the Learn screen, either a main question or a follow-up.</summary>
public sealed class LearnItem
{
    public required string Question { get; init; }
    /// <summary>The prompt id of the question type (for example "behavioral"); see <see cref="QuestionTypes.LabelFor"/>.</summary>
    public string QuestionType { get; init; } = "";
    public string Focus { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsFollowUp { get; init; }
    public string? ParentQuestion { get; init; }
    /// <summary>For follow-ups: the Coach's one-line hint for handling it.</summary>
    public string? Hint { get; init; }
    public CoachOutput? Coach { get; set; }

    /// <summary>The technology this question is about, when it came from the technology bank.</summary>
    public string? Technology { get; init; }
    /// <summary>
    /// True when the answer was written without the candidate's resume (a general answer that can be reused).
    /// <see cref="LearnEngine.PersonalizeAsync"/> turns it into one written from the resume.
    /// </summary>
    public bool IsGeneric { get; set; }
    /// <summary>The bank entry behind this question, if any; used to save the general answer for next time.</summary>
    public int? TechQuestionId { get; init; }
}

/// <summary>
/// Learn mode (spec 6.2): pick a question, then immediately ask the Coach how to answer it. There is no candidate answer.
/// Technical-concept questions come from the <see cref="TechBank"/> when one is supplied: saved per technology, with a
/// general answer that costs nothing to reuse. Everything else is written by the model from the resume and job description.
/// No UI references. Async methods resume on the caller's context, so events are raised on the UI thread when called from WPF.
/// </summary>
public sealed class LearnEngine(ILlmService llm, IPromptLibrary prompts, TechBank? bank = null, Func<double>? random = null, ILearnHistory? history = null)
{
    private enum Step { None, Generate, Coach }

    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    private CandidateProfile _profile = new();
    private IReadOnlyCollection<QuestionType> _allowed = [];
    private int? _answerWords;
    private EmploymentType _employment = EmploymentType.FullTime;
    private IReadOnlyList<string> _focus = [];
    private string? _lastTechnology;
    private readonly List<string> _asked = [];
    private readonly Stack<LearnItem> _trail = new();
    private IReadOnlyList<ResumeTopic>? _resumeTopics;
    private ResumeFocusPicker _focusPicker = new(Random.Shared.NextDouble);
    private IReadOnlyList<string>? _technologies;
    private CancellationTokenSource? _cts;
    private int _operation;
    private Step _failedStep = Step.None;
    private Task<LearnItem?>? _prefetch;
    private CancellationTokenSource? _prefetchCts;

    /// <summary>
    /// When on, the next question and its model answer are prepared in the background as soon as the current one is on
    /// screen, so Next shows it at once instead of waiting about ten seconds. Costs one extra pair of calls that is
    /// thrown away if the session ends first. Off by default so callers and tests see exactly the calls they asked for.
    /// </summary>
    public bool PrefetchNext { get; init; }

    public LearnPhase Phase { get; private set; } = LearnPhase.Idle;
    public LearnItem? Current { get; private set; }
    public string? Error { get; private set; }
    public bool CanGoBack => _trail.Count > 0;
    public IReadOnlyList<string> AskedQuestions => _asked;
    /// <summary>The technologies found in the job description, once known (empty if none or if the bank is not in use).</summary>
    public IReadOnlyList<string> Technologies => _technologies ?? [];

    public event Action? Changed;

    /// <summary>
    /// Begins a new session and shows the first question. <paramref name="answerWords"/> asks for model answers of about that
    /// length; null leaves it to the Coach. <paramref name="focusTechnologies"/> asks for questions about those technologies:
    /// on its own (no <paramref name="allowedTypes"/>) every question is about them; with types ticked it is one more share.
    /// <paramref name="employment"/> is the kind of job: it shapes the questions asked and how the answers are coached.
    /// </summary>
    public Task StartAsync(
        CandidateProfile profile, IReadOnlyCollection<QuestionType> allowedTypes, int? answerWords = null,
        IReadOnlyList<string>? focusTechnologies = null, EmploymentType employment = EmploymentType.FullTime)
    {
        _profile = profile;
        _employment = employment;
        _allowed = allowedTypes;
        _answerWords = answerWords;
        _focus = focusTechnologies is { Count: > 0 } ? focusTechnologies.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        _lastTechnology = null;
        _asked.Clear();
        _technologies = null;
        _resumeTopics = null;
        _focusPicker = new ResumeFocusPicker(_random);
        DiscardPrefetch();
        return NextAsync();
    }

    /// <summary>Shows a fresh main question with its model answer. Whatever was in flight is abandoned.</summary>
    public async Task NextAsync()
    {
        var (ct, id) = BeginOperation();
        _trail.Clear();
        Current = null;
        Error = null;
        _failedStep = Step.None;
        SetPhase(LearnPhase.GeneratingQuestion);

        // A question prepared in the background is shown straight away, or as soon as it finishes if it is still running.
        if (_prefetch is { } pending)
        {
            _prefetch = null;
            _prefetchCts = null;
            var prepared = await pending;
            if (IsStale(id)) return;
            if (prepared is not null)
            {
                Current = prepared;
                SetPhase(LearnPhase.Ready);
                StartPrefetch();
                await RecordShownAsync(prepared);
                return;
            }
            // The background attempt failed or was cancelled; fall through and do it the normal way.
        }

        LearnItem item;
        try
        {
            item = await ProduceQuestionAsync(ct);
        }
        catch (LlmException ex)
        {
            if (!IsStale(id)) Fail(Step.Generate, ex);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        if (IsStale(id)) return;

        _asked.Add(item.Question);
        Current = item;
        if (item.Coach is not null)
        {
            // A saved general answer: nothing to wait for.
            SetPhase(LearnPhase.Ready);
            StartPrefetch();
            await RecordShownAsync(item);
            return;
        }
        await LoadAnswerAsync(item, ct, id);
    }

    /// <summary>Shows the Coach output for one of the follow-up questions on the current item.</summary>
    public async Task OpenFollowUpAsync(FollowUp followUp)
    {
        if (Current is not { Coach: not null } parent) return;

        var (ct, id) = BeginOperation();
        _trail.Push(parent);
        var item = new LearnItem
        {
            Question = followUp.Question,
            QuestionType = parent.QuestionType,
            Focus = parent.Focus,
            Source = parent.Source,
            IsFollowUp = true,
            ParentQuestion = parent.Question,
            Hint = followUp.Hint,
            // A follow-up to a general answer is answered generally too (and is not saved: it is not a bank question).
            Technology = parent.Technology,
            IsGeneric = parent.IsGeneric,
        };
        if (!_asked.Contains(item.Question)) _asked.Add(item.Question);
        Current = item;
        Error = null;
        await LoadAnswerAsync(item, ct, id);
    }

    /// <summary>
    /// Replaces a general answer with one written from the candidate's resume and job description.
    /// Costs a normal Coach call, and the tailored answer is not saved for reuse.
    /// </summary>
    public async Task PersonalizeAsync()
    {
        if (Current is not { IsGeneric: true, Coach: not null } item) return;

        var (ct, id) = BeginOperation();
        item.IsGeneric = false;
        item.Coach = null;
        Error = null;
        await LoadAnswerAsync(item, ct, id);
    }

    /// <summary>Returns from a follow-up to the question it came from.</summary>
    public void Back()
    {
        if (_trail.Count == 0) return;
        BeginOperation(); // drops any in-flight follow-up call
        Current = _trail.Pop();
        Error = null;
        _failedStep = Step.None;
        SetPhase(LearnPhase.Ready);
    }

    /// <summary>Repeats the step that failed, keeping everything that already worked.</summary>
    public Task RetryAsync()
    {
        if (Phase != LearnPhase.Failed) return Task.CompletedTask;
        if (_failedStep == Step.Coach && Current is { } item)
        {
            var (ct, id) = BeginOperation();
            Error = null;
            return LoadAnswerAsync(item, ct, id);
        }
        return NextAsync();
    }

    /// <summary>Abandons any call in flight (for example when the screen is closed).</summary>
    public void Cancel()
    {
        BeginOperation();
        DiscardPrefetch();
    }

    private async Task LoadAnswerAsync(LearnItem item, CancellationToken ct, int id)
    {
        _failedStep = Step.None;
        SetPhase(LearnPhase.LoadingAnswer);
        try
        {
            var coach = await FetchCoachAsync(item, BuildTranscript(), ct);
            if (IsStale(id)) return;
            item.Coach = coach;
            Error = null;
            SetPhase(LearnPhase.Ready);
            if (!item.IsFollowUp) StartPrefetch();
            await RecordShownAsync(item);
        }
        catch (LlmException ex)
        {
            if (!IsStale(id)) Fail(Step.Coach, ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // superseded by Next/Back/Cancel
        }
    }

    /// <summary>
    /// Keeps the question and its answer for the library once both are on screen. Best effort: a problem saving a note about a
    /// question must never interrupt practice, so any failure here is ignored.
    /// </summary>
    private async Task RecordShownAsync(LearnItem item)
    {
        if (history is null || item.Coach is null) return;
        try
        {
            await history.RecordAsync(new LearnHistoryEntry
            {
                Question = item.Question,
                QuestionType = item.QuestionType,
                Technology = item.Technology,
                Seniority = _profile.Seniority.ToString(),
                Source = item.Source,
                IsFollowUp = item.IsFollowUp,
                ParentQuestion = item.ParentQuestion,
                IsGeneral = item.IsGeneric,
                ProfileName = item.IsGeneric ? null : _profile.Name,
                Coach = item.Coach,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // intentionally ignored, see above
        }
    }

    /// <summary>The model answer for an item: a general one (saved for reuse when it belongs to a bank question) or one from the resume.</summary>
    private async Task<CoachOutput> FetchCoachAsync(LearnItem item, string? transcript, CancellationToken ct)
    {
        if (item.IsGeneric && bank is not null)
        {
            var type = string.IsNullOrWhiteSpace(item.QuestionType) ? QuestionType.TechnicalConcept.Id() : item.QuestionType;
            var general = await bank.WriteGeneralAnswerAsync(item.Question, _profile.Seniority, _answerWords, transcript, type, ct);
            if (item.TechQuestionId is { } questionId)
                await bank.SaveAnswerAsync(questionId, _answerWords, general, ct);
            return general;
        }

        var coach = await llm.GetJsonAsync<CoachOutput>(
            LlmRole.Coach, RenderCoachPrompt(item, transcript), [UserTurn(PromptName.Coach)], ct);
        return coach.ForLearning();
    }

    // ---- choosing the next question

    /// <summary>
    /// Questions come from up to two places: the technology source (saved general questions about one technology, or
    /// questions the model writes about a chosen technology) and the model, which writes everything else from the
    /// resume and job description. Each ticked type other than Technical concept counts as one share, and the technology
    /// source counts as one share, so Technical concept and By technology together are still just one share.
    /// </summary>
    private async Task<LearnItem> ProduceQuestionAsync(CancellationToken ct)
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
        if (shares > 1 && _random() >= 1.0 / shares) return (null, true, false);

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
        item.Coach = (await bank.GetSavedAnswerAsync(question.Id, _answerWords, ct))?.ForLearning();
        return (item, true, false);
    }

    /// <summary>A random technology from the pool, avoiding the one just asked about when there is a choice.</summary>
    private string PickTechnology(IReadOnlyList<string> pool)
    {
        var candidates = pool.Count > 1 && _lastTechnology is not null
            ? pool.Where(t => !t.Equals(_lastTechnology, StringComparison.OrdinalIgnoreCase)).ToList()
            : pool.ToList();
        var chosen = candidates[Math.Min(candidates.Count - 1, (int)(_random() * candidates.Count))];
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

    // ---- preparing the next question in the background

    private void StartPrefetch()
    {
        if (!PrefetchNext || _prefetch is not null) return; // already prepared or being prepared
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        _prefetch = PrefetchAsync(cts.Token);
    }

    private void DiscardPrefetch()
    {
        _prefetchCts?.Cancel();
        _prefetchCts = null;
        _prefetch = null;
    }

    /// <summary>Prepares the next question and its answer without touching the visible state. Null means "do it the normal way".</summary>
    private async Task<LearnItem?> PrefetchAsync(CancellationToken ct)
    {
        try
        {
            var item = await ProduceQuestionAsync(ct);
            ct.ThrowIfCancellationRequested();
            _asked.Add(item.Question); // so any later question avoids it, even before it is shown
            item.Coach ??= await FetchCoachAsync(item, transcript: null, ct);
            ct.ThrowIfCancellationRequested();
            return item;
        }
        catch (LlmException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // ---- prompts

    private string RenderCoachPrompt(LearnItem item, string? transcript)
    {
        var vars = PromptVars.ForProfile(_profile);
        vars["MODE"] = "learn";
        vars["QUESTION"] = item.Question;
        vars["TRANSCRIPT"] = transcript;
        vars["QUESTION_TYPE"] = string.IsNullOrWhiteSpace(item.QuestionType) ? null : item.QuestionType;
        vars["EMPLOYMENT_TYPE"] = _employment.PromptValue();
        // The requested word count, else the range interviewers expect for this type: the same range the screen shows.
        // Left alone the Coach wrote 250 to 300 words even for short concept questions.
        vars["ANSWER_LENGTH"] = AnswerLength.Describe(_answerWords, item.QuestionType);
        // Learn mode has no candidate answer; these render as "(none)".
        vars["CANDIDATE_ANSWER"] = null;
        vars["PREVIOUS_ATTEMPT"] = null;
        vars["INPUT_METHOD"] = null;
        vars["DURATION_SECONDS"] = null;
        vars["WORD_COUNT"] = null;
        return prompts.Render(PromptName.Coach, vars);
    }

    /// <summary>For follow-ups, the questions leading here and the model answers the candidate was shown for them.</summary>
    private string? BuildTranscript()
    {
        if (_trail.Count == 0) return null;
        return string.Join("\n", _trail.Reverse().Select(i =>
            $"Interviewer: {i.Question}\nModel answer the candidate was shown: {i.Coach?.ModelAnswer}"));
    }

    private static ChatTurn UserTurn(PromptName name) => new(ChatTurnRole.User, name.UserMessage()!);

    private bool WasAsked(string question) => _asked.Any(a => TextTools.SameQuestion(a, question));

    // ---- operations and state

    private (CancellationToken Token, int Id) BeginOperation()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return (_cts.Token, ++_operation);
    }

    private bool IsStale(int id) => id != _operation;

    private void Fail(Step step, LlmException ex)
    {
        _failedStep = step;
        Error = ex.Message;
        SetPhase(LearnPhase.Failed);
    }

    private void SetPhase(LearnPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
