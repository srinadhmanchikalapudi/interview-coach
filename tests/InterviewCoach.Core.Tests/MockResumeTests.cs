using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

// Resuming a stored interview, and reading one back for the History screen. (Shares the harness of the engine tests.)
public partial class MockEngineTests
{
    private static MockEngine NewEngine(Script script, Clockwork clock, IMockHistory history)
        => new(new ScriptedLlmService(script.Handle), new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => clock.Now, history);

    // An interview that was left with the main question on screen, after one answer.
    private static async Task<(Harness H, MockRecord Record)> LeftMidwayAsync()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        h.Clock.Advance(125);
        await h.Engine.SubmitAnswerAsync("Good, thanks.", AnswerInputMethod.Voice, 6);
        h.Engine.Cancel();                                                  // the user opened another page
        return (h, h.History.Added[0]);
    }

    [Fact]
    public async Task The_interview_is_written_as_it_goes_so_a_round_that_is_left_is_kept_unfinished()
    {
        var (h, record) = await LeftMidwayAsync();

        Assert.Single(h.History.Added);
        Assert.False(record.Finished);
        Assert.True(record.IsUnfinished);
        Assert.False(record.NeedsDebrief);
        Assert.Null(record.EndedAt);
        Assert.Equal(125, record.ElapsedSeconds);
        Assert.Equal("Technical", record.RoundType);
        Assert.Equal("Full-time", record.Employment);
        Assert.Equal(30, record.DurationMinutes);
        using var turns = JsonDocument.Parse(record.TurnsJson);
        Assert.Equal(3, turns.RootElement.GetArrayLength());                // opener, the answer, the main question
        Assert.Null(record.DebriefJson);
        Assert.True(h.History.Updates >= 2);
    }

    [Fact]
    public async Task A_stored_turn_knows_the_interviews_own_clock()
    {
        var (_, record) = await LeftMidwayAsync();

        var restored = MockRecords.Restore(record);

        Assert.Equal([0, 125, 125], restored.Turns.Select(t => t.ElapsedSeconds));
        Assert.Equal("voice", restored.Turns[1].InputMethod);
        Assert.Equal(6, restored.Turns[1].DurationSeconds);
        Assert.NotNull(restored.Plan);
        Assert.Equal("Hi, thanks for joining.", restored.Plan!.OpeningLine);
    }

    [Fact]
    public async Task Resuming_goes_on_from_the_line_that_was_being_asked_without_planning_again_and_keeps_the_clock()
    {
        var (_, record) = await LeftMidwayAsync();
        var script = new Script();
        script.Turns.Enqueue(Turn("Makes sense. Anything you would change?", "follow_up"));
        var clock = new Clockwork();
        var engine = NewEngine(script, clock, new MemoryMockHistory());

        await engine.ResumeAsync(Profile(), record);

        Assert.Equal(MockPhase.InterviewerSpeaking, engine.Phase);          // the screen says the line again
        Assert.Equal("Walk me through the caching layer you built.", engine.CurrentLine);
        Assert.Equal(3, engine.Turns.Count);
        Assert.Empty(script.Planner);
        Assert.Empty(script.Interviewer);
        Assert.Equal(RoundType.Technical, engine.RoundType);
        Assert.Equal(30, engine.DurationMinutes);
        Assert.Equal(125, engine.ElapsedSeconds);                           // the time it was left does not count
        clock.Advance(40);
        Assert.Equal(165, engine.ElapsedSeconds);
        Assert.True(engine.CanEnd);
        Assert.Null(engine.Debrief);

        await engine.FinishedSpeakingAsync();
        await engine.SubmitAnswerAsync("I put Redis in front of the hot lookups.", AnswerInputMethod.Typed, 20);

        // What the interviewer is sent is the whole earlier conversation as it was, then the new answer.
        var sent = script.Interviewer.Single().Messages;
        Assert.Equal(5, sent.Count);
        Assert.Equal("[app context] The candidate has joined the call. Elapsed 0 min of 30 min.", sent[0].Content);
        Assert.Contains("\"say\":\"Hi, thanks for joining. How is your day going?\"", sent[1].Content);
        Assert.Equal("Good, thanks.\n[app context] Elapsed 2 min of 30 min. Answer took 6s via voice.", sent[2].Content);
        Assert.Contains("\"turn_type\":\"main_question\"", sent[3].Content);
        Assert.StartsWith("I put Redis in front of the hot lookups.\n[app context] Elapsed 2 min of 30 min.", sent[4].Content);
    }

    [Fact]
    public async Task Resuming_after_an_answer_whose_reply_never_came_asks_the_interviewer_again()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        h.Script.FailInterviewerTimes = 1;
        await h.Engine.SubmitAnswerAsync("Good, thanks.");                  // the reply fails
        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        h.Engine.Cancel();
        var record = h.History.Added[0];

        var script = new Script();
        script.Turns.Enqueue(Turn("Walk me through the cache.", "main_question"));
        var engine = NewEngine(script, new Clockwork(), new MemoryMockHistory());
        await engine.ResumeAsync(Profile(), record);

        Assert.Equal(MockPhase.InterviewerSpeaking, engine.Phase);
        Assert.Equal("Walk me through the cache.", engine.CurrentLine);
        Assert.Equal(3, engine.Turns.Count);
        Assert.StartsWith("Good, thanks.", script.Interviewer.Single().Messages.Last().Content);
    }

    [Fact]
    public async Task Resuming_on_a_closing_line_ends_the_round_after_it_and_writes_the_debrief()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("Good, thanks.");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I put Redis in front.");
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("I compared p99.");                // the closing line is on screen
        Assert.True(h.Engine.IsClosing);
        h.Engine.Cancel();

        var script = new Script();
        var engine = NewEngine(script, new Clockwork(), new MemoryMockHistory());
        await engine.ResumeAsync(Profile(), h.History.Added[0]);

        Assert.True(engine.IsClosing);
        Assert.Equal("Thanks, this was great to chat.", engine.CurrentLine);
        await engine.FinishedSpeakingAsync();
        await engine.WhenCoachedAsync();
        Assert.Equal(MockPhase.Done, engine.Phase);
        Assert.Single(script.Debriefs);
    }

    [Fact]
    public async Task A_round_that_ended_without_a_debrief_gets_its_debrief_written_when_resumed_and_the_same_row_is_updated()
    {
        var h = Standard();
        h.Script.FailDebriefTimes = 1;
        await RunStandardInterview(h);
        Assert.Equal(MockPhase.Failed, h.Engine.Phase);
        var record = h.History.Added[0];
        Assert.True(record.NeedsDebrief);
        Assert.False(record.IsUnfinished);

        var script = new Script();
        var history = new MemoryMockHistory();
        var engine = NewEngine(script, new Clockwork(), history);
        await engine.ResumeAsync(Profile(), record);
        await engine.WhenCoachedAsync();

        Assert.Equal(MockPhase.Done, engine.Phase);
        Assert.NotNull(engine.Debrief);
        Assert.Empty(history.Added);                                         // no second row: the first one was updated
        Assert.True(history.Updates >= 1);
        Assert.False(record.NeedsDebrief);
        Assert.Equal("lean_yes", record.HireSignal);
        Assert.Single(script.Coaches);
        Assert.Empty(script.Interviewer);
    }

    [Fact]
    public async Task A_record_that_cannot_be_read_cannot_be_resumed_and_says_so()
    {
        var (_, record) = await LeftMidwayAsync();
        record.TurnsJson = "{not json";
        var engine = NewEngine(new Script(), new Clockwork(), new MemoryMockHistory());

        await engine.ResumeAsync(Profile(), record);

        Assert.Equal(MockPhase.Failed, engine.Phase);
        Assert.Contains("cannot be resumed", engine.Error);
        Assert.False(engine.CanEnd);
    }

    [Fact]
    public async Task A_resumed_interview_that_was_over_time_when_the_answer_was_sent_still_tells_the_interviewer_to_wrap_up()
    {
        var h = Standard();
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 15);
        await Speak(h.Engine);
        h.Script.FailInterviewerTimes = 1;
        h.Clock.Advance(21 * 60);
        await h.Engine.SubmitAnswerAsync("Hello.");                         // 21 minutes in: told the time is up, but the reply fails
        h.Engine.Cancel();

        var script = new Script();
        script.Turns.Enqueue(Turn("Let us leave it there. Thanks.", "follow_up"));   // not marked as closing by the model
        var engine = NewEngine(script, new Clockwork(), new MemoryMockHistory());
        await engine.ResumeAsync(Profile(), h.History.Added[0]);

        Assert.EndsWith(MockEngine.TimeUpMessage, script.Interviewer.Single().Messages.Last().Content);
        Assert.True(engine.IsClosing);
    }

    // ---- reading a stored interview back

    [Fact]
    public async Task A_finished_interview_is_restored_with_its_debrief_and_the_coaching_of_each_thread()
    {
        var h = Standard();
        await RunStandardInterview(h);

        var restored = MockRecords.Restore(h.History.Added[0]);

        Assert.Equal(7, restored.Turns.Count);
        Assert.Equal("lean_yes", restored.Debrief!.HireSignal);
        var thread = Assert.Single(restored.Threads);
        Assert.Equal(ThreadStatus.Done, thread.Status);
        Assert.Equal("I measured p99 before and after.", thread.Coach!.ModelAnswer);
        Assert.Equal(h.Engine.Threads[0].Transcript, thread.Transcript);
    }

    [Fact]
    public async Task A_thread_whose_coaching_was_never_finished_is_restored_as_failed_and_an_unanswered_one_stays_unanswered()
    {
        var h = new Harness();
        h.Script.Turns.Enqueue(Turn("First?", "main_question"));
        h.Script.Turns.Enqueue(Turn("Second?", "main_question"));
        await h.Engine.StartAsync(Profile(), RoundType.Technical, 30);
        await Speak(h.Engine);
        await h.Engine.SubmitAnswerAsync("An answer.");
        await Speak(h.Engine);
        h.Engine.Cancel();                                                  // left while the second question is open
        var record = h.History.Added[0];

        var restored = MockRecords.Restore(record);

        Assert.Equal([ThreadStatus.Failed, ThreadStatus.NotAnswered], restored.Threads.Select(t => t.Status));
        Assert.Contains("never finished", restored.Threads[0].Error);
    }

    [Fact]
    public void Unreadable_parts_are_left_out_instead_of_breaking_the_whole_record()
    {
        var record = new MockRecord { TurnsJson = "garbage", PlanJson = "also garbage", DebriefJson = "{", ThreadsJson = "[" };

        var restored = MockRecords.Restore(record);

        Assert.Empty(restored.Turns);
        Assert.Null(restored.Plan);
        Assert.Null(restored.Debrief);
        Assert.Empty(restored.Threads);
    }

    [Theory]
    [InlineData("Technical", RoundType.Technical)]
    [InlineData("system design", RoundType.SystemDesign)]
    [InlineData("nonsense", RoundType.Mixed)]
    [InlineData(null, RoundType.Mixed)]
    public void A_stored_round_label_maps_back_to_the_round(string? label, RoundType expected) => Assert.Equal(expected, MockRecords.RoundOf(label));

    [Theory]
    [InlineData("Contract", EmploymentType.Contract)]
    [InlineData("Full-time", EmploymentType.FullTime)]
    [InlineData("", EmploymentType.FullTime)]
    public void A_stored_role_type_maps_back(string label, EmploymentType expected) => Assert.Equal(expected, MockRecords.EmploymentOf(label));
}
