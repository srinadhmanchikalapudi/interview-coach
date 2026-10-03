using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Engines;

public enum PracticePhase
{
    Idle,
    /// <summary>Choosing the next question.</summary>
    GeneratingQuestion,
    /// <summary>The question is on screen and the candidate is writing an answer. No Coach call is made in this phase.</summary>
    Answering,
    /// <summary>An answer was submitted and the Coach is reading it.</summary>
    Coaching,
    /// <summary>The Coach output is on screen. Retry, a follow-up or the next question can follow.</summary>
    ShowingFeedback,
    /// <summary>An LLM call failed. State, including the candidate's answer, is kept; <see cref="PracticeEngine.RetryAsync"/> repeats the failed step.</summary>
    Failed,
}

/// <summary>What <see cref="PracticeEngine.SubmitAsync"/> did with an answer.</summary>
public enum SubmitResult
{
    /// <summary>The answer was sent to the Coach (the call may still be running, or may have failed: look at the phase).</summary>
    Sent,
    /// <summary>Nothing was written. Nothing was sent.</summary>
    Empty,
    /// <summary>There is no question waiting for an answer right now. Nothing was sent.</summary>
    NotReady,
}

/// <summary>How an answer was given. Typed is all there is until voice arrives.</summary>
public static class AnswerInputMethod
{
    public const string Typed = "typed";
    public const string Voice = "voice";
    public const string Mixed = "mixed";
}

/// <summary>One answer to one question, with what the Coach made of it.</summary>
public sealed record PracticeAttempt(string AnswerText, string InputMethod, int DurationSeconds, int WordCount, CoachOutput Coach);

/// <summary>
/// Practice mode (spec 6.3): a question, then the candidate answers, then the Coach reads the answer.
/// <code>Generating → Answering → Coaching → ShowingFeedback → [Try again → Answering] | [Follow-up → Answering] | [Next → Generating]</code>
/// The engine never calls the Coach before an answer is submitted: only <see cref="SubmitAsync"/> and the retry of a failed
/// submission do, so the rule holds whatever the screen does. Questions come from the same <see cref="QuestionPicker"/> as Learn mode.
/// No UI references. Async methods resume on the caller's context, so events are raised on the UI thread when called from WPF.
/// </summary>
public sealed class PracticeEngine(ILlmService llm, IPromptLibrary prompts, TechBank? bank = null, Func<double>? random = null)
{
    private enum Step { None, Generate, Coach }

    private sealed record Submission(string Answer, string InputMethod, int DurationSeconds, int WordCount);

    private readonly QuestionPicker _picker = new(llm, prompts, bank, random ?? Random.Shared.NextDouble);
    private CandidateProfile _profile = new();
    private EmploymentType _employment = EmploymentType.FullTime;
    private int? _answerWords;
    private readonly List<(string Question, string Answer)> _thread = [];
    private string? _previousAttempt;
    private Submission? _pending;
    private CancellationTokenSource? _cts;
    private int _operation;
    private Step _failedStep = Step.None;

    public PracticePhase Phase { get; private set; } = PracticePhase.Idle;
    /// <summary>The question on screen (a main question or a follow-up). Its Coach property is not used in Practice.</summary>
    public LearnItem? Current { get; private set; }
    public string? Error { get; private set; }
    /// <summary>The newest answer and its feedback, while they are on screen.</summary>
    public PracticeAttempt? LastAttempt { get; private set; }
    /// <summary>1 for the first answer to the question on screen, 2 after one try again, and so on.</summary>
    public int AttemptNumber { get; private set; } = 1;
    /// <summary>The answer that was submitted and is waiting for (or failed to get) feedback. The screen puts it back if the Coach call fails.</summary>
    public string? PendingAnswer => _pending?.Answer;
    public IReadOnlyList<string> AskedQuestions => _picker.Asked;
    public bool CanSubmit => Phase == PracticePhase.Answering;
    public bool CanTryAgain => Phase == PracticePhase.ShowingFeedback && LastAttempt is not null;

    public event Action? Changed;

    /// <summary>Begins a session and shows the first question. The arguments mean what they mean for <see cref="LearnEngine.StartAsync"/>.</summary>
    public Task StartAsync(
        CandidateProfile profile, IReadOnlyCollection<QuestionType> allowedTypes, int? answerWords = null,
        IReadOnlyList<string>? focusTechnologies = null, EmploymentType employment = EmploymentType.FullTime)
    {
        Begin(profile, allowedTypes, answerWords, focusTechnologies, employment);
        return NextAsync();
    }

    /// <summary>
    /// Begins a session on a question that is already known (for example one the user just read in Learn mode). The next
    /// question after it is chosen with the same filter as the session it came from.
    /// </summary>
    public void StartWithQuestion(
        CandidateProfile profile, LearnItem question, IReadOnlyCollection<QuestionType> allowedTypes, int? answerWords = null,
        IReadOnlyList<string>? focusTechnologies = null, EmploymentType employment = EmploymentType.FullTime)
    {
        Begin(profile, allowedTypes, answerWords, focusTechnologies, employment);
        _picker.Add(question.Question);
        Current = AsQuestion(question);
        SetPhase(PracticePhase.Answering);
    }

    /// <summary>Shows a fresh main question. Whatever was in flight is abandoned, and the earlier answers are forgotten.</summary>
    public async Task NextAsync()
    {
        var (ct, id) = BeginOperation();
        ResetThread();
        Current = null;
        Error = null;
        _failedStep = Step.None;
        SetPhase(PracticePhase.GeneratingQuestion);

        LearnItem item;
        try
        {
            item = await _picker.NextAsync(ct);
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
        Current = AsQuestion(item);
        SetPhase(PracticePhase.Answering);
    }

    /// <summary>
    /// Sends an answer to the Coach. Only possible while a question is waiting for an answer, and never for an empty answer
    /// (the screen says "I don't know" is a fine answer too, so anything written counts).
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(string answer, string inputMethod = AnswerInputMethod.Typed, int durationSeconds = 0)
    {
        if (Phase != PracticePhase.Answering || Current is null) return SubmitResult.NotReady;
        if (string.IsNullOrWhiteSpace(answer)) return SubmitResult.Empty;

        var text = answer.Trim();
        _pending = new Submission(text, inputMethod, Math.Max(0, durationSeconds), AnswerLength.CountWords(text));
        Error = null;
        var (ct, id) = BeginOperation();
        await CoachPendingAsync(ct, id);
        return SubmitResult.Sent;
    }

    /// <summary>Repeats the step that failed, keeping everything that already worked, including the candidate's answer.</summary>
    public Task RetryAsync()
    {
        if (Phase != PracticePhase.Failed) return Task.CompletedTask;
        if (_failedStep == Step.Coach && _pending is not null)
        {
            var (ct, id) = BeginOperation();
            Error = null;
            return CoachPendingAsync(ct, id);
        }
        return NextAsync();
    }

    /// <summary>
    /// Answers the same question again. The last answer is passed to the Coach with the next one, so the feedback can say what
    /// changed. No LLM call happens here.
    /// </summary>
    public void TryAgain()
    {
        if (!CanTryAgain) return;
        BeginOperation();
        _previousAttempt = LastAttempt!.AnswerText;
        AttemptNumber++;
        LastAttempt = null;
        _pending = null;
        Error = null;
        SetPhase(PracticePhase.Answering);
    }

    /// <summary>
    /// Makes one of the Coach's follow-ups the next question. The question and answer so far go into the transcript the Coach
    /// sees. No LLM call happens here: the next call is the Coach, after the answer is submitted.
    /// </summary>
    public void AnswerFollowUp(FollowUp followUp)
    {
        if (!CanTryAgain || Current is null) return;
        BeginOperation();
        var parent = Current;
        _thread.Add((parent.Question, LastAttempt!.AnswerText));
        _picker.AddIfNew(followUp.Question);
        Current = new LearnItem
        {
            Question = followUp.Question,
            QuestionType = parent.QuestionType,
            Focus = parent.Focus,
            Source = parent.Source,
            IsFollowUp = true,
            ParentQuestion = parent.Question,
            Hint = followUp.Hint,
            Technology = parent.Technology,
        };
        _previousAttempt = null;
        AttemptNumber = 1;
        LastAttempt = null;
        _pending = null;
        Error = null;
        SetPhase(PracticePhase.Answering);
    }

    /// <summary>Abandons any call in flight (for example when the screen is closed).</summary>
    public void Cancel() => BeginOperation();

    // ---- the Coach call

    private async Task CoachPendingAsync(CancellationToken ct, int id)
    {
        var submission = _pending!;
        _failedStep = Step.None;
        SetPhase(PracticePhase.Coaching);
        try
        {
            var coach = await llm.GetJsonAsync<CoachOutput>(
                LlmRole.Coach, RenderCoachPrompt(Current!, submission), [UserTurn(PromptName.Coach)], ct);
            if (IsStale(id)) return;
            LastAttempt = new PracticeAttempt(submission.Answer, submission.InputMethod, submission.DurationSeconds, submission.WordCount, coach.ForPractice());
            Error = null;
            SetPhase(PracticePhase.ShowingFeedback);
        }
        catch (LlmException ex)
        {
            if (!IsStale(id)) Fail(Step.Coach, ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // superseded by Next, Try again or Cancel
        }
    }

    private string RenderCoachPrompt(LearnItem item, Submission submission)
    {
        var vars = PromptVars.ForProfile(_profile);
        vars["MODE"] = "practice";
        vars["QUESTION"] = item.Question;
        vars["TRANSCRIPT"] = BuildTranscript();
        vars["QUESTION_TYPE"] = string.IsNullOrWhiteSpace(item.QuestionType) ? null : item.QuestionType;
        vars["EMPLOYMENT_TYPE"] = _employment.PromptValue();
        vars["ANSWER_LENGTH"] = AnswerLength.Describe(_answerWords, item.QuestionType);
        vars["CANDIDATE_ANSWER"] = submission.Answer;
        vars["PREVIOUS_ATTEMPT"] = _previousAttempt;
        vars["INPUT_METHOD"] = submission.InputMethod;
        vars["DURATION_SECONDS"] = submission.DurationSeconds > 0 ? submission.DurationSeconds.ToString() : null;
        vars["WORD_COUNT"] = submission.WordCount.ToString();
        return prompts.Render(PromptName.Coach, vars);
    }

    /// <summary>For a follow-up, the questions leading here and what the candidate said to each (spec section 9), or null for a main question.</summary>
    private string? BuildTranscript()
        => _thread.Count == 0 ? null : string.Join("\n", _thread.Select(t => $"Interviewer: {t.Question}\nYou: {t.Answer}"));

    // ---- state

    private void Begin(
        CandidateProfile profile, IReadOnlyCollection<QuestionType> allowedTypes, int? answerWords, IReadOnlyList<string>? focusTechnologies, EmploymentType employment)
    {
        BeginOperation();
        _profile = profile;
        _employment = employment;
        _answerWords = answerWords;
        _picker.Start(profile, allowedTypes, focusTechnologies, employment);
        ResetThread();
        Current = null;
        Error = null;
        _failedStep = Step.None;
    }

    private void ResetThread()
    {
        _thread.Clear();
        _previousAttempt = null;
        _pending = null;
        LastAttempt = null;
        AttemptNumber = 1;
    }

    // A question from the picker may carry a saved model answer (a saved technical question). Practice never shows it.
    private static LearnItem AsQuestion(LearnItem item) => new()
    {
        Question = item.Question,
        QuestionType = item.QuestionType,
        Focus = item.Focus,
        Source = item.Source,
        Technology = item.Technology,
        IsFollowUp = item.IsFollowUp,
        ParentQuestion = item.ParentQuestion,
        Hint = item.Hint,
    };

    private static ChatTurn UserTurn(PromptName name) => new(ChatTurnRole.User, name.UserMessage()!);

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
        SetPhase(PracticePhase.Failed);
    }

    private void SetPhase(PracticePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
