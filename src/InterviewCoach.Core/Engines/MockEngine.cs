using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Engines;

public enum MockPhase
{
    Setup, Planning, InterviewerThinking, InterviewerSpeaking, CandidateAnswering, Ending, Debriefing, Done, Failed,
}

/// <summary>JSON options for what the mock engine stores and sends: snake_case, like the prompts' own replies.</summary>
public static class MockJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}

/// <summary>
/// The Mock Interview state machine (spec 6.1): a plan, a live interviewer who reacts to each answer, time pacing, the end of the round, and
/// afterwards the question threads, a Coach call for each (three at a time) and the debrief. No UI references. Nothing here gives feedback
/// while the interview runs: coaching starts only when the round has ended.
/// <para>
/// The interviewer's lines are spoken by the screen, which tells the engine when it has finished (<see cref="FinishedSpeakingAsync"/>);
/// the engine does not know about speech.
/// </para>
/// </summary>
public sealed class MockEngine(ILlmService llm, IPromptLibrary prompts, Func<DateTime>? now = null, IMockHistory? history = null)
{
    /// <summary>The round is closed once it has run this many minutes over its length.</summary>
    public const int OvertimeMinutes = 5;

    /// <summary>How many threads are coached at the same time.</summary>
    public const int MaxCoachCalls = 3;

    public const string TimeUpMessage = "[app context] Time is up. Wrap up now.";

    private enum Step { None, Plan, Interviewer, Debrief }

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private readonly List<ChatTurn> _messages = [];
    private readonly List<MockTurn> _turns = [];
    private readonly SemaphoreSlim _coachGate = new(MaxCoachCalls);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _coachTasks = [];

    private CandidateProfile _profile = new();
    private EmploymentType _employment = EmploymentType.FullTime;
    private string? _planJson;
    private DateTime? _startedAt;
    private DateTime? _endedAt;
    private bool _timeUpSent;
    private bool _threadsStarted;
    private bool _userEnded;
    private Step _failedStep;
    private int _operation;
    private CancellationTokenSource? _cts;
    private MockRecord? _record;
    private List<MockThread> _threads = [];

    public MockPhase Phase { get; private set; } = MockPhase.Setup;
    public string? Error { get; private set; }
    public RoundType RoundType { get; private set; } = RoundType.Mixed;
    public int DurationMinutes { get; private set; } = RoundTypes.DefaultDuration;
    public CandidateProfile Profile => _profile;
    public InterviewPlanDto? Plan { get; private set; }
    public DebriefDto? Debrief { get; private set; }

    /// <summary>The conversation so far, in order.</summary>
    public IReadOnlyList<MockTurn> Turns => _turns;

    /// <summary>The question threads, once the round has ended. Their coaching fills in as each call finishes.</summary>
    public IReadOnlyList<MockThread> Threads => _threads;

    /// <summary>What the interviewer is saying (or last said).</summary>
    public string CurrentLine => _turns.LastOrDefault(t => t.Speaker == MockSpeaker.Interviewer)?.Text ?? "";

    /// <summary>True when the line being spoken is the closing line: after it, the round is over.</summary>
    public bool IsClosing { get; private set; }

    public bool HasEnded => Phase is MockPhase.Ending or MockPhase.Debriefing or MockPhase.Done || (Phase == MockPhase.Failed && _failedStep == Step.Debrief);

    /// <summary>True while the interview is running, so End interview makes sense.</summary>
    public bool CanEnd => Phase is MockPhase.InterviewerThinking or MockPhase.InterviewerSpeaking or MockPhase.CandidateAnswering;

    /// <summary>Seconds since the interviewer's first turn began (frozen once the round has ended).</summary>
    public int ElapsedSeconds => _startedAt is { } start ? Math.Max(0, (int)((_endedAt ?? _now()) - start).TotalSeconds) : 0;

    public bool IsOvertime => ElapsedSeconds >= DurationMinutes * 60;

    public bool ThreadsPending => _threads.Any(t => t.Status == ThreadStatus.Coaching);

    public event Action? Changed;

    // ---- starting

    /// <summary>Plans the round and gets the interviewer's opening turn. A failure leaves the engine in Failed with Retry.</summary>
    public async Task StartAsync(CandidateProfile profile, RoundType roundType, int durationMinutes, EmploymentType employment = EmploymentType.FullTime)
    {
        _cts?.Cancel();
        _profile = profile;
        _employment = employment;
        RoundType = roundType;
        DurationMinutes = Math.Max(1, durationMinutes);
        _messages.Clear();
        _turns.Clear();
        _threads = [];
        _coachTasks.Clear();
        _threadsStarted = false;
        _timeUpSent = false;
        _userEnded = false;
        _startedAt = null;
        _endedAt = null;
        _record = null;
        Plan = null;
        _planJson = null;
        Debrief = null;
        IsClosing = false;
        Error = null;
        _failedStep = Step.None;
        await PlanAndOpenAsync();
    }

    private async Task PlanAndOpenAsync()
    {
        var (token, id) = BeginOperation();
        Error = null;

        if (Plan is null)
        {
            SetPhase(MockPhase.Planning);
            try
            {
                var plan = await llm.GetJsonAsync<InterviewPlanDto>(
                    LlmRole.Planner, prompts.Render(PromptName.Planner, RoundVars()), [UserTurn(PromptName.Planner)], token);
                if (IsStale(id)) return;
                Plan = plan;
                _planJson = JsonSerializer.Serialize(plan, MockJson.Indented);
            }
            catch (LlmException ex)
            {
                if (IsStale(id)) return;
                Fail(Step.Plan, ex);
                return;
            }
            catch (OperationCanceledException) when (IsStale(id)) { return; }
        }

        if (_messages.Count == 0)
        {
            _startedAt = _now();
            _messages.Add(new ChatTurn(ChatTurnRole.User, $"[app context] The candidate has joined the call. Elapsed 0 min of {DurationMinutes} min."));
        }
        await InterviewerTurnAsync(id, token);
    }

    // ---- the interviewer's turns

    private async Task InterviewerTurnAsync(int id, CancellationToken token)
    {
        SetPhase(MockPhase.InterviewerThinking);
        InterviewerTurnDto reply;
        try
        {
            reply = await llm.GetJsonAsync<InterviewerTurnDto>(LlmRole.Interviewer, prompts.Render(PromptName.Interviewer, RoundVars()), _messages.ToList(), token);
            if (IsStale(id)) return;
            if (string.IsNullOrWhiteSpace(reply.Say)) throw new LlmException("The interviewer returned nothing to say.");
        }
        catch (LlmException ex)
        {
            if (IsStale(id)) return;
            Fail(Step.Interviewer, ex);
            return;
        }
        catch (OperationCanceledException) when (IsStale(id)) { return; }

        var say = reply.Say.Trim();
        // The next request repeats this turn as the model wrote it, which keeps the reply format stable (spec 6.1).
        var raw = JsonSerializer.Serialize(new
        {
            say,
            turn_type = reply.TurnType,
            phase = reply.Phase,
            focus_area_id = reply.FocusAreaId,
            end_interview = reply.EndInterview,
        });
        _messages.Add(new ChatTurn(ChatTurnRole.Assistant, raw));
        _turns.Add(new MockTurn
        {
            Index = _turns.Count,
            Speaker = MockSpeaker.Interviewer,
            Text = say,
            RawJson = raw,
            TurnType = reply.TurnType,
            Phase = reply.Phase,
            FocusAreaId = reply.FocusAreaId,
            At = _now(),
        });

        // After "Time is up" the next interviewer turn is the closing, whatever it says.
        IsClosing = reply.EndInterview || _timeUpSent;
        SetPhase(MockPhase.InterviewerSpeaking);
    }

    /// <summary>The screen has finished saying the interviewer's line: the candidate's turn starts, or the round ends after the closing.</summary>
    public async Task FinishedSpeakingAsync()
    {
        if (Phase != MockPhase.InterviewerSpeaking) return;
        if (IsClosing) await EndRoundAsync();
        else SetPhase(MockPhase.CandidateAnswering);
    }

    // ---- the candidate's turns

    /// <summary>
    /// Sends the candidate's answer and gets the interviewer's next turn. An empty answer is not sent. Past the length of the round plus
    /// five minutes, the interviewer is told to wrap up.
    /// </summary>
    public async Task<SubmitResult> SubmitAnswerAsync(string answer, string inputMethod = AnswerInputMethod.Typed, int durationSeconds = 0)
    {
        if (Phase != MockPhase.CandidateAnswering) return SubmitResult.NotReady;
        var text = answer?.Trim() ?? "";
        if (text.Length == 0) return SubmitResult.Empty;

        _turns.Add(new MockTurn
        {
            Index = _turns.Count,
            Speaker = MockSpeaker.Candidate,
            Text = text,
            InputMethod = inputMethod,
            DurationSeconds = Math.Max(0, durationSeconds),
            At = _now(),
        });

        var context = $"[app context] Elapsed {ElapsedSeconds / 60} min of {DurationMinutes} min. Answer took {Math.Max(0, durationSeconds)}s via {inputMethod}.";
        if (ElapsedSeconds >= (DurationMinutes + OvertimeMinutes) * 60)
        {
            _timeUpSent = true;
            context += "\n" + TimeUpMessage;
        }
        _messages.Add(new ChatTurn(ChatTurnRole.User, text + "\n" + context));

        var (token, id) = BeginOperation();
        await InterviewerTurnAsync(id, token);
        return SubmitResult.Sent;
    }

    /// <summary>The user ends the interview. No closing line is spoken; the debrief starts at once.</summary>
    public async Task EndNowAsync()
    {
        if (!CanEnd) return;
        _userEnded = true;
        _cts?.Cancel();
        _operation++; // a reply still on its way is ignored
        await EndRoundAsync();
    }

    // ---- after the round

    private async Task EndRoundAsync()
    {
        SetPhase(MockPhase.Ending);
        _endedAt = _now();
        _threads = MockText.BuildThreads(_turns);
        await RecordAsync();
        StartCoaching();
        await DebriefAsync();
    }

    private async Task DebriefAsync()
    {
        var (token, id) = BeginOperation();
        Error = null;
        SetPhase(MockPhase.Debriefing);

        var vars = RoundVars();
        vars["TRANSCRIPT"] = MockText.Transcript(_turns);
        try
        {
            var debrief = await llm.GetJsonAsync<DebriefDto>(
                LlmRole.Debrief, prompts.Render(PromptName.Debrief, vars), [UserTurn(PromptName.Debrief)], token);
            if (IsStale(id)) return;
            Debrief = debrief;
        }
        catch (LlmException ex)
        {
            if (IsStale(id)) return;
            Fail(Step.Debrief, ex);
            return;
        }
        catch (OperationCanceledException) when (IsStale(id)) { return; }

        SetPhase(MockPhase.Done);
        await RecordAsync();
    }

    /// <summary>One Coach call per thread the candidate answered, at most <see cref="MaxCoachCalls"/> at a time, while the debrief is written.</summary>
    private void StartCoaching()
    {
        if (_threadsStarted) return;
        _threadsStarted = true;
        foreach (var thread in _threads.Where(t => t.HasAnswer))
            _coachTasks.Add(CoachThreadAsync(thread));
    }

    private async Task CoachThreadAsync(MockThread thread)
    {
        var token = _lifetime.Token;
        try
        {
            await _coachGate.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            thread.Status = ThreadStatus.Coaching;
            thread.Error = null;
            Changed?.Invoke();
            var coach = await llm.GetJsonAsync<CoachOutput>(LlmRole.Coach, RenderCoachPrompt(thread), [UserTurn(PromptName.Coach)], token);
            thread.Coach = coach.ForPractice();
            thread.Status = ThreadStatus.Done;
        }
        catch (LlmException ex)
        {
            thread.Status = ThreadStatus.Failed;
            thread.Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _coachGate.Release();
        }
        Changed?.Invoke();
        if (!ThreadsPending) await RecordAsync();
    }

    /// <summary>Asks the Coach again about a thread whose call failed.</summary>
    public Task RetryThreadAsync(MockThread thread)
    {
        if (thread.Status != ThreadStatus.Failed || !_threads.Contains(thread)) return Task.CompletedTask;
        thread.Status = ThreadStatus.Coaching;
        Changed?.Invoke();
        var task = CoachThreadAsync(thread);
        _coachTasks.Add(task);
        return task;
    }

    /// <summary>Completes when every thread has been coached (or has failed). Used by tests and by anything that must wait for the full debrief.</summary>
    public Task WhenCoachedAsync() => Task.WhenAll(_coachTasks.ToArray());

    private string RenderCoachPrompt(MockThread thread)
    {
        var vars = PromptVars.ForProfile(_profile);
        var type = QuestionTypes.All.Cast<QuestionType?>().FirstOrDefault(t => t!.Value.Id().Equals(thread.Phase?.Trim(), StringComparison.OrdinalIgnoreCase));
        vars["MODE"] = "mock";
        vars["QUESTION"] = thread.Question;
        vars["TRANSCRIPT"] = null;
        vars["QUESTION_TYPE"] = type?.Id();
        vars["EMPLOYMENT_TYPE"] = _employment.PromptValue();
        vars["ANSWER_LENGTH"] = null;
        vars["CANDIDATE_ANSWER"] = thread.Transcript; // the whole exchange for this question, follow-ups included (coach.md, mock mode)
        vars["PREVIOUS_ATTEMPT"] = null;
        vars["INPUT_METHOD"] = thread.InputMethod;
        vars["DURATION_SECONDS"] = thread.SpokenSeconds > 0 ? thread.SpokenSeconds.ToString() : null; // typing time says nothing about delivery
        vars["WORD_COUNT"] = thread.WordCount.ToString();
        return prompts.Render(PromptName.Coach, vars);
    }

    // ---- recording

    private async Task RecordAsync()
    {
        if (history is null || _startedAt is null) return;
        try
        {
            var first = _record is null;
            _record ??= new MockRecord
            {
                ProfileName = _profile.Name,
                JobRole = _profile.JobRole,
                RoundType = RoundType.Label(),
                DurationMinutes = DurationMinutes,
                StartedAt = _startedAt.Value,
                PlanJson = _planJson,
            };
            _record.EndedAt = _endedAt;
            _record.ElapsedSeconds = ElapsedSeconds;
            _record.Finished = true;
            _record.TurnsJson = JsonSerializer.Serialize(_turns, MockJson.Options);
            _record.DebriefJson = Debrief is null ? null : JsonSerializer.Serialize(Debrief, MockJson.Options);
            _record.HireSignal = Debrief?.HireSignal;
            _record.ThreadsJson = JsonSerializer.Serialize(
                _threads.Select(t => new { t.Question, t.Phase, t.FocusAreaId, Status = t.Status.ToString(), t.Coach, t.Error, Transcript = t.Transcript }), MockJson.Options);
            if (first) _record.Id = await history.AddAsync(_record);
            else await history.UpdateAsync(_record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording is best effort: it must not interrupt the debrief.
        }
    }

    // ---- recovery

    /// <summary>Repeats the step that failed (the plan, an interviewer turn or the debrief). The conversation so far is kept.</summary>
    public async Task RetryAsync()
    {
        if (Phase != MockPhase.Failed) return;
        var step = _failedStep;
        _failedStep = Step.None;
        switch (step)
        {
            case Step.Plan:
                await PlanAndOpenAsync();
                break;
            case Step.Interviewer:
                var (token, id) = BeginOperation();
                Error = null;
                await InterviewerTurnAsync(id, token);
                break;
            case Step.Debrief:
                await DebriefAsync();
                break;
        }
    }

    /// <summary>Stops everything that is still running (leaving the screen).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _lifetime.Cancel();
        _operation++;
    }

    // ---- plumbing

    private Dictionary<string, string?> RoundVars()
    {
        var vars = PromptVars.ForProfile(_profile);
        vars["ROUND_TYPE"] = RoundType.PromptValue();
        vars["DURATION"] = DurationMinutes.ToString();
        vars["PLAN_JSON"] = _planJson;
        return vars;
    }

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
        SetPhase(MockPhase.Failed);
    }

    private void SetPhase(MockPhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    private static ChatTurn UserTurn(PromptName name) => new(ChatTurnRole.User, name.UserMessage()!);

    /// <summary>True when the round ended because the user ended it, not because the interviewer closed it.</summary>
    public bool EndedByUser => _userEnded;
}
