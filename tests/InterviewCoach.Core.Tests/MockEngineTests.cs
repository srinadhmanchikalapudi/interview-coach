using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public partial class MockEngineTests
{
    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class MemoryMockHistory : IMockHistory
    {
        public List<MockRecord> Added { get; } = [];
        public int Updates { get; private set; }
        public bool Fail { get; set; }

        public Task<int> AddAsync(MockRecord record, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("disk full");
            Added.Add(record);
            return Task.FromResult(Added.Count);
        }

        public Task UpdateAsync(MockRecord record, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("disk full");
            Updates++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MockRecord>>(Added);
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A stand-in for the planner, the interviewer, the coach and the debrief writer.</summary>
    private sealed class Script
    {
        public const string PlanMarker = "hiring manager preparing to interview";
        public const string InterviewerMarker = "running a live";
        public const string DebriefMarker = "who just finished this";

        public Queue<InterviewerTurnDto> Turns { get; } = new();
        public bool FailPlan { get; set; }
        public int FailInterviewerTimes { get; set; }
        public int FailDebriefTimes { get; set; }
        public string? FailCoachContaining { get; set; }
        public int CoachDelayMs { get; set; }
        public int ActiveCoaches;
        public int MaxActiveCoaches;
        public List<LlmCall> Calls { get; } = [];

        public IEnumerable<LlmCall> Planner => Calls.Where(c => c.SystemPrompt.Contains(PlanMarker));
        public IEnumerable<LlmCall> Interviewer => Calls.Where(c => c.SystemPrompt.Contains(InterviewerMarker));
        public IEnumerable<LlmCall> Debriefs => Calls.Where(c => c.SystemPrompt.Contains(DebriefMarker));
        public IEnumerable<LlmCall> Coaches => Calls.Where(c => c.Role == LlmRole.Coach);

        public async Task<object> Handle(LlmCall call)
        {
            lock (Calls) Calls.Add(call);

            if (call.Role == LlmRole.Planner)
            {
                if (FailPlan) throw new LlmException("the planner is unavailable");
                return new InterviewPlanDto
                {
                    FocusAreas = [new FocusAreaDto { Id = "fa1", Name = "Caching", Why = "JD must-have", Source = "jd" }],
                    ResumeClaimsToProbe = [new ClaimProbeDto { Claim = "cut p99 by 60%", Probe = "How was it measured?" }],
                    Phases = [new PhaseDto { Phase = "opener", TargetMinutes = 2, Topics = ["intro"] }, new PhaseDto { Phase = "technical", TargetMinutes = 13, Topics = ["caching"] }],
                    OpeningLine = "Hi, thanks for joining.",
                };
            }

            if (call.Role == LlmRole.Interviewer)
            {
                if (FailInterviewerTimes > 0)
                {
                    FailInterviewerTimes--;
                    throw new LlmException("the interviewer is unavailable");
                }
                return Turns.Dequeue();
            }

            if (call.Role == LlmRole.Debrief)
            {
                if (FailDebriefTimes > 0)
                {
                    FailDebriefTimes--;
                    throw new LlmException("the debrief is unavailable");
                }
                return new DebriefDto
                {
                    OverallSummary = "You were clear on caching and vague on measuring.",
                    HireSignal = "lean_yes",
                    FocusAreaRatings = [new FocusRatingDto { FocusAreaId = "fa1", Name = "Caching", Rating = 3, Evidence = "\"cut p99\"" }],
                    Strengths = ["Specific numbers"],
                    TopFixes = [new TopFixDto { Fix = "Say how you measured", Example = "the p99 claim", HowToPractice = "Name the metric first" }],
                    PracticeNext = ["How do you measure latency?"],
                };
            }

            var active = Interlocked.Increment(ref ActiveCoaches);
            lock (Calls) MaxActiveCoaches = Math.Max(MaxActiveCoaches, active);
            try
            {
                if (CoachDelayMs > 0) await Task.Delay(CoachDelayMs);
                if (FailCoachContaining is { } marker && call.SystemPrompt.Contains(marker)) throw new LlmException("the coach is unavailable");
                return new CoachOutput
                {
                    WhatTheyreTesting = "ownership",
                    Feedback = [new FeedbackPoint { Kind = "strength", Point = "Concrete.", Quote = "cut p99" }],
                    ModelAnswer = "I measured p99 before and after.",
                    Shape = "A → B",
                    FollowUps = [],
                };
            }
            finally
            {
                Interlocked.Decrement(ref ActiveCoaches);
            }
        }
    }

    private static InterviewerTurnDto Turn(string say, string type, string phase = "technical", bool end = false) => new()
    {
        Say = say, TurnType = type, Phase = phase, FocusAreaId = "fa1", EndInterview = end,
    };

    private static CandidateProfile Profile() => new()
    {
        Name = "Acme backend", JobRole = "Backend Engineer", Seniority = Seniority.Senior,
        JobDescription = "JD-MARKER we cache with Redis", ResumeText = "RESUME-MARKER cut p99 by 60%",
    };

    private sealed class Harness
    {
        public Script Script { get; } = new();
        public Clockwork Clock { get; } = new();
        public MemoryMockHistory History { get; } = new();
        public MockEngine Engine { get; }

        public Harness(bool withHistory = true)
        {
            var llm = new ScriptedLlmService(Script.Handle);
            Engine = new MockEngine(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => Clock.Now, withHistory ? History : null);
        }
    }

    // A short interview: opener, a main question, a follow-up, then the close.
    private static Harness Standard(bool withHistory = true)
    {
        var h = new Harness(withHistory);
        h.Script.Turns.Enqueue(Turn("Hi, thanks for joining. How is your day going?", "smalltalk", "opener"));
        h.Script.Turns.Enqueue(Turn("Walk me through the caching layer you built.", "main_question"));
        h.Script.Turns.Enqueue(Turn("How did you measure the p99 improvement?", "follow_up"));
        h.Script.Turns.Enqueue(Turn("Thanks, this was great to chat.", "closing", "candidate_questions", end: true));
        return h;
    }

    private static async Task Speak(MockEngine engine) => await engine.FinishedSpeakingAsync();

    private static async Task RunStandardInterview(Harness h)
    {
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);                                                         // the opener
        await h.Engine.SubmitAnswerAsync("Good, thanks.", AnswerInputMethod.Typed, 3);
        await Speak(h.Engine);                                                         // the main question
        await h.Engine.SubmitAnswerAsync("I put Redis in front of the hot lookups and cut p99 by sixty percent.", AnswerInputMethod.Voice, 40);
        await Speak(h.Engine);                                                         // the follow-up
        await h.Engine.SubmitAnswerAsync("I compared p99 before and after on the dashboard.", AnswerInputMethod.Voice, 20);
        await Speak(h.Engine);                                                         // the close: the round ends and the debrief runs
        await h.Engine.WhenCoachedAsync();
    }

    // ---- starting

    [Fact]
    public async Task Starting_plans_the_round_then_the_interviewer_opens_with_the_candidate_joining()
    {
        var h = Standard();

        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);

        var plan = Assert.Single(h.Script.Planner);
        Assert.Equal(LlmRole.Planner, plan.Role);
        Assert.Equal("Create the interview plan.", plan.Messages.Single().Content);
        Assert.Contains("JD-MARKER", plan.SystemPrompt);
        Assert.Contains("RESUME-MARKER", plan.SystemPrompt);
        Assert.Contains("Technical round lasting 30 minutes", plan.SystemPrompt);

        var first = Assert.Single(h.Script.Interviewer);
        Assert.Equal(LlmRole.Interviewer, first.Role);
        Assert.Equal("[app context] The candidate has joined the call. Elapsed 0 min of 30 min.", first.Messages.Single().Content);
        Assert.Contains("Hi, thanks for joining.", first.SystemPrompt);                       // the plan, rendered into <your_private_plan>
        Assert.Contains("\"focus_areas\"", first.SystemPrompt);

        Assert.Equal(MockPhase.InterviewerSpeaking, h.Engine.Phase);
        Assert.Equal("Hi, thanks for joining. How is your day going?", h.Engine.CurrentLine);
        Assert.False(h.Engine.IsClosing);
    }

    [Fact]
    public async Task The_candidate_may_answer_only_after_the_interviewer_has_finished_speaking()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);

        Assert.Equal(SubmitResult.NotReady, await h.Engine.SubmitAnswerAsync("too early"));
        Assert.Single(h.Script.Interviewer);

        await Speak(h.Engine);
        Assert.Equal(MockPhase.CandidateAnswering, h.Engine.Phase);
        Assert.Equal(SubmitResult.Sent, await h.Engine.SubmitAnswerAsync("Good, thanks."));
    }

    [Fact]
    public async Task An_empty_answer_is_not_sent()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);

        Assert.Equal(SubmitResult.Empty, await h.Engine.SubmitAnswerAsync("   \n "));

        Assert.Single(h.Script.Interviewer);
        Assert.Equal(MockPhase.CandidateAnswering, h.Engine.Phase);
    }

    // ---- the conversation

    [Fact]
    public async Task Each_answer_goes_with_the_elapsed_time_and_how_it_was_given_and_earlier_turns_are_repeated_as_they_were()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        h.Clock.Advance(125);

        await h.Engine.SubmitAnswerAsync("  Good, thanks.  ", AnswerInputMethod.Voice, 7);

        var second = h.Script.Interviewer.Last();
        Assert.Equal(3, second.Messages.Count);
        Assert.Equal(ChatTurnRole.User, second.Messages[0].Role);
        Assert.Equal(ChatTurnRole.Assistant, second.Messages[1].Role);
        Assert.Contains("\"say\":\"Hi, thanks for joining. How is your day going?\"", second.Messages[1].Content);
        Assert.Contains("\"turn_type\":\"smalltalk\"", second.Messages[1].Content);
        Assert.Equal("Good, thanks.\n[app context] Elapsed 2 min of 30 min. Answer took 7s via voice.", second.Messages[2].Content);
    }

    [Fact]
    public async Task The_conversation_is_kept_in_order_with_what_each_turn_was()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var turns = h.Engine.Turns;
        Assert.Equal(7, turns.Count);
        Assert.Equal([MockSpeaker.Interviewer, MockSpeaker.Candidate, MockSpeaker.Interviewer, MockSpeaker.Candidate, MockSpeaker.Interviewer, MockSpeaker.Candidate, MockSpeaker.Interviewer],
            turns.Select(t => t.Speaker));
        Assert.Equal(Enumerable.Range(0, 7), turns.Select(t => t.Index));
        Assert.Equal("main_question", turns[2].TurnType);
        Assert.Equal("technical", turns[2].Phase);
        Assert.Equal("fa1", turns[2].FocusAreaId);
        Assert.Equal("voice", turns[3].InputMethod);
        Assert.Equal(40, turns[3].DurationSeconds);
    }

    [Fact]
    public async Task Nothing_is_coached_or_debriefed_while_the_interview_runs()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("Good, thanks.");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I put Redis in front of the hot lookups.");

        Assert.Empty(h.Script.Coaches);
        Assert.Empty(h.Script.Debriefs);
        Assert.Empty(h.Engine.Threads);
        Assert.Null(h.Engine.Debrief);
    }

    // ---- ending

    [Fact]
    public async Task When_the_interviewer_closes_the_round_it_ends_after_the_closing_line_is_spoken_and_the_debrief_follows()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("Good, thanks.");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I put Redis in front of the hot lookups and cut p99.");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I compared p99 on the dashboard.");

        Assert.True(h.Engine.IsClosing);                                    // the closing line is on screen, being spoken
        Assert.Equal(MockPhase.InterviewerSpeaking, h.Engine.Phase);
        Assert.Empty(h.Script.Debriefs);

        await Speak(h.Engine);

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
        Assert.True(h.Engine.HasEnded);
        Assert.False(h.Engine.EndedByUser);
        Assert.Single(h.Script.Debriefs);
        Assert.Equal("lean_yes", h.Engine.Debrief!.HireSignal);
    }

    [Fact]
    public async Task The_debrief_prompt_gets_the_plan_and_the_whole_transcript()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var debrief = Assert.Single(h.Script.Debriefs);
        Assert.Equal(LlmRole.Debrief, debrief.Role);
        Assert.Equal("Write the debrief.", debrief.Messages.Single().Content);
        Assert.Contains("Interviewer: Hi, thanks for joining. How is your day going?\nYou: Good, thanks.\nInterviewer: Walk me through the caching layer you built.", debrief.SystemPrompt);
        Assert.Contains("You: I compared p99 before and after on the dashboard.", debrief.SystemPrompt);
        Assert.Contains("Interviewer: Thanks, this was great to chat.", debrief.SystemPrompt);
        Assert.Contains("\"opening_line\": \"Hi, thanks for joining.\"", debrief.SystemPrompt);
    }

    [Fact]
    public async Task The_user_can_end_the_interview_and_no_closing_line_is_asked_for()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("Good, thanks.");
        await Speak(h.Engine);                                              // the main question is on screen, the answer not given yet
        Assert.True(h.Engine.CanEnd);

        await h.Engine.EndNowAsync();

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
        Assert.True(h.Engine.EndedByUser);
        Assert.Equal(2, h.Script.Interviewer.Count());                      // the opener and the main question; nothing after End
        Assert.Single(h.Script.Debriefs);
        Assert.Single(h.Engine.Threads);
        Assert.Equal(ThreadStatus.NotAnswered, h.Engine.Threads[0].Status); // the question was never answered
        Assert.Empty(h.Script.Coaches);
        Assert.False(h.Engine.CanEnd);
    }

    [Fact]
    public async Task Ending_while_the_interviewer_is_still_thinking_ignores_the_late_reply()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);

        await h.Engine.EndNowAsync();                                       // the opener is being spoken

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
        Assert.Empty(h.Engine.Threads);
        Assert.False(h.Engine.CanEnd);
        Assert.Equal(SubmitResult.NotReady, await h.Engine.SubmitAnswerAsync("late"));
    }

    [Fact]
    public async Task Past_the_length_of_the_round_plus_five_minutes_the_interviewer_is_told_to_wrap_up_and_the_next_turn_is_the_closing()
    {
        var h = Standard();
        h.Script.Turns.Clear();
        h.Script.Turns.Enqueue(Turn("Hello.", "smalltalk", "opener"));
        h.Script.Turns.Enqueue(Turn("One more thing: what would you change?", "follow_up"));   // does not close, though it was told to
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 15);
        await Speak(h.Engine);
        h.Clock.Advance(20 * 60);                                           // 15 + 5 minutes

        await h.Engine.SubmitAnswerAsync("Hello back.", AnswerInputMethod.Typed, 5);

        var message = h.Script.Interviewer.Last().Messages.Last().Content;
        Assert.EndsWith("\n" + MockEngine.TimeUpMessage, message);
        Assert.Contains("Elapsed 20 min of 15 min.", message);
        Assert.True(h.Engine.IsClosing);                                    // treated as the closing whatever it says
        Assert.True(h.Engine.IsOvertime);
    }

    [Fact]
    public async Task Before_the_overtime_limit_the_interviewer_is_not_told_the_time_is_up()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 15);
        await Speak(h.Engine);
        h.Clock.Advance(19 * 60);                                           // over 15 minutes but under 20

        await h.Engine.SubmitAnswerAsync("Good, thanks.");

        Assert.DoesNotContain(MockEngine.TimeUpMessage, h.Script.Interviewer.Last().Messages.Last().Content);
        Assert.False(h.Engine.IsClosing);
        Assert.True(h.Engine.IsOvertime);
    }

    [Fact]
    public async Task The_elapsed_time_counts_from_the_first_turn_and_stops_when_the_round_ends()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        Assert.Equal(0, h.Engine.ElapsedSeconds);
        h.Clock.Advance(90);
        Assert.Equal(90, h.Engine.ElapsedSeconds);
        await Speak(h.Engine);

        await h.Engine.EndNowAsync();
        h.Clock.Advance(600);

        Assert.Equal(90, h.Engine.ElapsedSeconds);
    }

    // ---- threads and coaching

    [Fact]
    public async Task Each_main_question_becomes_a_thread_with_its_follow_ups_and_answers_and_smalltalk_and_the_closing_are_left_out()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var thread = Assert.Single(h.Engine.Threads);
        Assert.Equal("Walk me through the caching layer you built.", thread.Question);
        Assert.Equal(4, thread.Turns.Count);   // question, answer, follow-up, answer
        Assert.Equal("Interviewer: Walk me through the caching layer you built.\nYou: I put Redis in front of the hot lookups and cut p99 by sixty percent.\nInterviewer: How did you measure the p99 improvement?\nYou: I compared p99 before and after on the dashboard.",
            thread.Transcript);
        Assert.DoesNotContain("Good, thanks.", thread.Transcript);
        Assert.DoesNotContain("great to chat", thread.Transcript);
    }

    [Fact]
    public async Task Every_answered_thread_is_coached_in_mock_mode_with_the_whole_exchange_as_the_answer()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var coach = Assert.Single(h.Script.Coaches);
        Assert.Equal("Coach this.", coach.Messages.Single().Content);
        Assert.Contains("mock: candidate_answer holds the whole exchange", coach.SystemPrompt);   // the fixed text of coach.md
        Assert.Contains("Mode: mock", coach.SystemPrompt);
        Assert.Contains("Interviewer: How did you measure the p99 improvement?", coach.SystemPrompt);
        Assert.Contains("input_method=\"voice\"", coach.SystemPrompt);
        Assert.Contains("duration_seconds=\"60\"", coach.SystemPrompt);                           // 40 s and 20 s of speaking
        Assert.Equal(ThreadStatus.Done, h.Engine.Threads[0].Status);
        Assert.Equal("I measured p99 before and after.", h.Engine.Threads[0].Coach!.ModelAnswer);
    }

    [Fact]
    public async Task A_typed_answer_reports_no_duration_to_the_coach()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("Walk me through it.", "main_question"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I typed this.", AnswerInputMethod.Typed, 90);
        await Speak(h.Engine);
        await h.Engine.WhenCoachedAsync();

        var coach = Assert.Single(h.Script.Coaches);
        Assert.Contains("input_method=\"typed\"", coach.SystemPrompt);
        Assert.DoesNotContain("duration_seconds=\"90\"", coach.SystemPrompt);
    }

    [Fact]
    public async Task At_most_three_threads_are_coached_at_the_same_time()
    {
        var h = new Harness();
        h.Script.CoachDelayMs = 60;
        for (var i = 1; i <= 6; i++)
        {
            h.Script.Turns.Enqueue(Turn($"Question {i}?", "main_question"));
        }
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        for (var i = 1; i <= 6; i++)
        {
            await Speak(h.Engine);
            await h.Engine.SubmitAnswerAsync($"Answer {i}.");
        }
        await Speak(h.Engine);
        await h.Engine.WhenCoachedAsync();

        Assert.Equal(6, h.Script.Coaches.Count());
        Assert.Equal(MockEngine.MaxCoachCalls, h.Script.MaxActiveCoaches);
        Assert.All(h.Engine.Threads, t => Assert.Equal(ThreadStatus.Done, t.Status));
    }

    [Fact]
    public async Task The_debrief_does_not_wait_for_the_coaching_of_each_question()
    {
        var h = new Harness();
        h.Script.CoachDelayMs = 300;
        h.Script.Turns.Enqueue(Turn("Question?", "main_question"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("An answer.");
        await Speak(h.Engine);

        Assert.Equal(MockPhase.Done, h.Engine.Phase);                       // the debrief is in
        Assert.True(h.Engine.ThreadsPending);                               // the thread card is still being written
        await h.Engine.WhenCoachedAsync();
        Assert.False(h.Engine.ThreadsPending);
    }

    [Fact]
    public async Task A_coaching_failure_affects_only_its_own_thread_and_can_be_retried()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("First question?", "main_question"));
        h.Script.Turns.Enqueue(Turn("Second question?", "main_question"));
        h.Script.Turns.Enqueue(Turn("Thanks.", "closing", end: true));
        h.Script.FailCoachContaining = "You: bad answer";
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("bad answer");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("good answer");
        await Speak(h.Engine);
        await h.Engine.WhenCoachedAsync();

        Assert.Equal([ThreadStatus.Failed, ThreadStatus.Done], h.Engine.Threads.Select(t => t.Status));
        Assert.Contains("the coach is unavailable", h.Engine.Threads[0].Error);
        Assert.Equal(MockPhase.Done, h.Engine.Phase);

        h.Script.FailCoachContaining = null;
        await h.Engine.RetryThreadAsync(h.Engine.Threads[0]);

        Assert.Equal(ThreadStatus.Done, h.Engine.Threads[0].Status);
        Assert.Null(h.Engine.Threads[0].Error);
    }

    // ---- failures

    [Fact]
    public async Task A_failed_plan_keeps_the_engine_ready_to_retry_and_the_retry_continues_into_the_interview()
    {
        var h = Standard();
        h.Script.FailPlan = true;

        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);

        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        Assert.Contains("the planner is unavailable", h.Engine.Error);
        Assert.Empty(h.Script.Interviewer);

        h.Script.FailPlan = false;
        await h.Engine.RetryAsync();

        Assert.Equal(MockPhase.InterviewerSpeaking, h.Engine.Phase);
        Assert.Null(h.Engine.Error);
        Assert.Equal(2, h.Script.Planner.Count());
        Assert.Single(h.Script.Interviewer);
    }

    [Fact]
    public async Task A_failed_interviewer_turn_keeps_the_answer_and_a_retry_sends_it_again_without_duplicating_it()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        h.Script.FailInterviewerTimes = 1;

        await h.Engine.SubmitAnswerAsync("Good, thanks.");

        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        Assert.Contains(h.Engine.Turns, t => t.Speaker == MockSpeaker.Candidate && t.Text == "Good, thanks.");

        await h.Engine.RetryAsync();

        Assert.Equal(MockPhase.InterviewerSpeaking, h.Engine.Phase);
        Assert.Single(h.Engine.Turns, t => t.Speaker == MockSpeaker.Candidate);
        var lastRequest = h.Script.Interviewer.Last();
        Assert.Equal(3, lastRequest.Messages.Count);                          // not 4: the answer was not added twice
        Assert.Equal("Walk me through the caching layer you built.", h.Engine.CurrentLine);
    }

    [Fact]
    public async Task A_failed_debrief_keeps_the_conversation_and_the_coaching_and_a_retry_only_repeats_the_debrief()
    {
        var h = Standard();
        h.Script.FailDebriefTimes = 1;
        await RunStandardInterview(h);

        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        Assert.True(h.Engine.HasEnded);
        Assert.Null(h.Engine.Debrief);
        Assert.Equal(ThreadStatus.Done, h.Engine.Threads[0].Status);          // the coaching went on regardless

        await h.Engine.RetryAsync();

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
        Assert.NotNull(h.Engine.Debrief);
        Assert.Equal(2, h.Script.Debriefs.Count());
        Assert.Single(h.Script.Coaches);                                      // no thread was coached again
    }

    [Fact]
    public async Task An_empty_interviewer_line_is_an_error_not_a_silent_turn()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("   ", "smalltalk"));

        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);

        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        Assert.Contains("nothing to say", h.Engine.Error);
        Assert.Empty(h.Engine.Turns);
    }

    [Fact]
    public async Task A_cancelled_interview_accepts_nothing_more_and_starts_no_debrief()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);

        h.Engine.Cancel();

        Assert.False(h.Engine.CanEnd);
        Assert.Equal(SubmitResult.NotReady, await h.Engine.SubmitAnswerAsync("too late"));
        await h.Engine.FinishedSpeakingAsync();
        await h.Engine.EndNowAsync();
        Assert.Empty(h.Script.Debriefs);
        Assert.Null(h.Engine.Debrief);
    }

    // ---- recording

    [Fact]
    public async Task The_interview_is_recorded_when_it_ends_and_updated_with_the_debrief_and_the_coaching()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var record = Assert.Single(h.History.Added);
        Assert.Equal("Acme backend", record.ProfileName);
        Assert.Equal("Technical", record.RoundType);
        Assert.Equal(30, record.DurationMinutes);
        Assert.True(record.Finished);
        Assert.Equal("lean_yes", record.HireSignal);
        Assert.Contains("Walk me through the caching layer", record.TurnsJson);
        Assert.Contains("\"focus_areas\"", record.PlanJson);
        Assert.Contains("lean_yes", record.DebriefJson);
        Assert.Contains("I measured p99 before and after.", record.ThreadsJson);
        Assert.True(h.History.Updates >= 1);
        using var turns = JsonDocument.Parse(record.TurnsJson);
        Assert.Equal(7, turns.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_recording_failure_never_interrupts_the_debrief()
    {
        var h = Standard();
        h.History.Fail = true;

        await RunStandardInterview(h);

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
        Assert.NotNull(h.Engine.Debrief);
    }

    [Fact]
    public async Task Without_a_history_the_interview_still_runs()
    {
        var h = Standard(withHistory: false);

        await RunStandardInterview(h);

        Assert.Equal(MockPhase.Done, h.Engine.Phase);
    }
}
