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


    private readonly QuestionPicker _picker = new(llm, prompts, bank, random ?? Random.Shared.NextDouble);
    private CandidateProfile _profile = new();
    private int? _answerWords;
    private EmploymentType _employment = EmploymentType.FullTime;
    private readonly Stack<LearnItem> _trail = new();
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
    public IReadOnlyList<string> AskedQuestions => _picker.Asked;
    /// <summary>The technologies found in the job description, once known (empty if none or if the bank is not in use).</summary>
    public IReadOnlyList<string> Technologies => _picker.Technologies;

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
        _answerWords = answerWords;
        _picker.Start(profile, allowedTypes, focusTechnologies, employment);
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

        _picker.Add(item.Question);
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
        _picker.AddIfNew(item.Question);
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

    /// <summary>The next question from the shared picker, with the saved general answer when it is a saved technical question.</summary>
    private async Task<LearnItem> ProduceQuestionAsync(CancellationToken ct)
    {
        var item = await _picker.NextAsync(ct);
        if (item is { IsGeneric: true, TechQuestionId: { } questionId } && bank is not null)
            item.Coach = (await bank.GetSavedAnswerAsync(questionId, _answerWords, ct))?.ForLearning();
        return item;
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
            _picker.Add(item.Question); // so any later question avoids it, even before it is shown
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
