using System.IO;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>The Mock Interview screen: planning, the interviewer's lines, answering by typing or voice, ending, and the debrief it hands over.</summary>
public class MockTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public int Saves { get; private set; }
        public void Save(AppSettings settings) { Current = settings; Saves++; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static MockSessionRequest Request(bool showText = true, int minutes = 15, RoundType round = RoundType.Technical)
        => new(Samples.CompleteProfile(), round, minutes, showText, EmploymentType.FullTime);

    /// <summary>The demo model, which can be made to fail for one role.</summary>
    private sealed class FlakyLlm : ILlmService
    {
        public FakeLlmService Inner { get; } = new();
        public LlmRole? FailRole { get; set; }

        public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
            => role == FailRole ? throw new LlmException($"the {role} is unavailable") : Inner.GetJsonAsync<T>(role, systemPrompt, messages, ct);

        public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct) => Inner.TestConnectionAsync(settings, ct);
    }

    private sealed class Harness
    {
        public FlakyLlm Flaky { get; } = new();
        public FakeLlmService Llm => Flaky.Inner;
        public Clockwork Clock { get; } = new();
        public InMemoryMockHistory History { get; } = new();
        public ScriptedDialogs Dialogs { get; } = new();
        public MockViewModel Vm { get; }
        public DebriefViewModel? Debrief { get; private set; }
        public int DebriefsRaised { get; private set; }

        public Harness(AppSettings? settings = null, TestSpeech? speech = null)
        {
            var s = settings ?? new AppSettings { AutoListen = false, SilenceAutoSubmit = false };
            Vm = new MockViewModel(Flaky, Prompts, new MemorySettings(s), speech, Dialogs, History, () => Clock.Now);
            Vm.DebriefReady += d => { Debrief = d; DebriefsRaised++; };
        }

        public IEnumerable<(LlmRole Role, string SystemPrompt, IReadOnlyList<ChatTurn> Messages)> InterviewerCalls => Llm.Calls.Where(c => c.Role == LlmRole.Interviewer);

        /// <summary>Answers every question until the interview closes and the debrief is ready.</summary>
        public async Task AnswerUntilDebriefAsync(string answer = "A sample answer with enough words to count.")
        {
            for (var guard = 0; Debrief is null && guard < 20; guard++)
            {
                Assert.NotEqual(MockPhase.Failed, Vm.Phase);
                if (Vm.IsAnswering)
                {
                    Vm.AnswerText = answer;
                    await Vm.SubmitCommand.ExecuteAsync(null);
                }
            }
            Assert.NotNull(Debrief);
        }
    }

    // ---- the start

    [Fact]
    public void Starting_plans_the_round_and_the_interviewers_first_line_is_shown_ready_for_the_candidates_turn()
    {
        var h = new Harness();

        h.Vm.Begin(Request());

        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);            // no voice here, so the line is "spoken" at once
        Assert.Equal("Hi, thanks for joining. How is your day going?", h.Vm.InterviewerLine);
        Assert.True(h.Vm.HasInterviewerLine);
        Assert.Equal("Your turn", h.Vm.StatusText);
        Assert.Equal("Senior Backend Engineer · Technical · 15 min", h.Vm.RoleLine);
        var first = Assert.Single(h.Vm.Conversation);
        Assert.Equal("Interviewer", first.Speaker);
        Assert.True(first.IsInterviewer);
        Assert.Equal("0:00 / 15:00", h.Vm.ElapsedText);
        Assert.Contains(h.Llm.Calls, c => c.Role == LlmRole.Planner);
    }

    [Fact]
    public async Task A_failed_plan_is_explained_with_Retry_and_the_retry_continues_into_the_interview()
    {
        var h = new Harness();
        h.Flaky.FailRole = LlmRole.Planner;

        h.Vm.Begin(Request());

        Assert.True(h.Vm.IsFailed);
        Assert.Contains("the Planner is unavailable", h.Vm.ErrorText);
        Assert.False(h.Vm.CanEnd);
        Assert.Equal("Problem", h.Vm.StatusText);
        h.Flaky.FailRole = null;

        await h.Vm.RetryCommand.ExecuteAsync(null);

        Assert.False(h.Vm.IsFailed);
        Assert.True(h.Vm.HasInterviewerLine);
        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
    }

    // ---- answering

    [Fact]
    public async Task Sending_an_answer_adds_it_to_the_conversation_clears_the_box_and_the_interviewer_replies()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        h.Vm.AnswerText = "Good, thanks.";

        await h.Vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("", h.Vm.AnswerText);
        Assert.Equal(["Interviewer", "You", "Interviewer"], h.Vm.Conversation.Select(c => c.Speaker));
        Assert.Equal("Good, thanks.", h.Vm.Conversation[1].Text);
        Assert.Contains("Walk me through a project", h.Vm.InterviewerLine);
        Assert.True(h.Vm.IsAnswering);
        var sent = h.InterviewerCalls.Last().Messages;
        Assert.Contains("Good, thanks.", sent[2].Content);
        Assert.Contains("via typed", sent[2].Content);
    }

    [Fact]
    public async Task An_empty_answer_is_not_sent_and_the_box_says_what_to_do()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        var calls = h.InterviewerCalls.Count();
        h.Vm.AnswerText = "   ";

        await h.Vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(MockViewModel.EmptyAnswerMessage, h.Vm.EmptyMessage);
        Assert.True(h.Vm.HasEmptyMessage);
        Assert.Equal(calls, h.InterviewerCalls.Count());
        h.Vm.AnswerText = "Now something.";
        Assert.False(h.Vm.HasEmptyMessage);
    }

    [Fact]
    public async Task The_answer_timer_starts_at_the_first_keystroke_and_the_duration_goes_to_the_interviewer()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        h.Clock.Advance(30);
        h.Vm.Tick();
        Assert.Equal("0:00", h.Vm.AnswerTimerText);                         // thinking time does not count

        h.Vm.AnswerText = "I";
        h.Clock.Advance(47);
        h.Vm.Tick();
        Assert.Equal("0:47", h.Vm.AnswerTimerText);
        await h.Vm.SubmitCommand.ExecuteAsync(null);

        Assert.Contains("Answer took 47s via typed.", h.InterviewerCalls.Last().Messages[2].Content);
        Assert.Contains("Elapsed 1 min of 15 min.", h.InterviewerCalls.Last().Messages[2].Content);
    }

    [Fact]
    public void The_clock_shows_elapsed_over_target_and_says_when_the_round_is_over_time()
    {
        var h = new Harness();
        h.Vm.Begin(Request(minutes: 15));

        h.Clock.Advance(65);
        h.Vm.Tick();
        Assert.Equal("1:05 / 15:00", h.Vm.ElapsedText);
        Assert.False(h.Vm.IsOvertime);
        Assert.Equal("", h.Vm.OvertimeText);

        h.Clock.Advance(15 * 60);
        h.Vm.Tick();
        Assert.True(h.Vm.IsOvertime);
        Assert.Contains("Over time", h.Vm.OvertimeText);
    }

    // ---- voice

    [Fact]
    public void The_interviewers_line_is_spoken_with_the_formatting_removed_and_then_it_is_the_candidates_turn()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);

        h.Vm.Begin(Request());

        Assert.Equal(["Hi, thanks for joining. How is your day going?"], speech.Voice.Spoken);
        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
        Assert.False(h.Vm.IsSpeaking);
    }

    [Fact]
    public void While_the_line_is_spoken_the_pill_says_speaking_and_the_microphone_is_not_opened_by_itself_when_auto_listen_is_off()
    {
        var speech = new TestSpeech();
        speech.Voice.Hold = true;
        var h = new Harness(speech: speech);

        h.Vm.Begin(Request());

        Assert.Equal(MockPhase.InterviewerSpeaking, h.Vm.Phase);
        Assert.True(h.Vm.IsSpeaking);
        Assert.Equal("Speaking", h.Vm.StatusText);
        Assert.False(h.Vm.IsAnswering);

        speech.Voice.Release();

        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
        Assert.Empty(speech.Recognizers);
    }

    [Fact]
    public void After_the_line_has_been_spoken_the_microphone_opens_by_itself_when_auto_listen_is_on()
    {
        var speech = new TestSpeech();
        var h = new Harness(new AppSettings { AutoListen = true }, speech);

        h.Vm.Begin(Request());

        Assert.True(h.Vm.Composer.IsListening);
        Assert.True(speech.Mic.Started);
        Assert.Equal("Listening", h.Vm.StatusText);
    }

    [Fact]
    public void Auto_listen_stays_quiet_when_speech_to_text_is_not_set_up()
    {
        var speech = new TestSpeech { SpeechToTextReadiness = SpeechReadiness.NotReady("add a key") };
        var h = new Harness(new AppSettings { AutoListen = true }, speech);

        h.Vm.Begin(Request());

        Assert.False(h.Vm.Composer.IsListening);
        Assert.False(h.Vm.Composer.HasSpeechMessage);        // the user did not ask for the microphone
        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
    }

    [Fact]
    public async Task Talking_over_the_interviewer_stops_them_and_it_becomes_the_candidates_turn()
    {
        var speech = new TestSpeech();
        speech.Voice.Hold = true;
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request());
        Assert.True(h.Vm.IsSpeaking);

        await h.Vm.Composer.ToggleMicCommand.ExecuteAsync(null);     // barge-in

        Assert.False(h.Vm.IsSpeaking);
        Assert.Equal(1, speech.Voice.StopCount);
        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
        Assert.True(h.Vm.Composer.IsListening);
    }

    [Fact]
    public async Task A_dictated_answer_is_sent_as_voice_with_the_time_it_took()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request());
        await h.Vm.Composer.ToggleMicCommand.ExecuteAsync(null);
        h.Clock.Advance(33);
        speech.Mic.Final("I led the migration and kept the data consistent.");

        await h.Vm.SubmitCommand.ExecuteAsync(null);

        var message = h.InterviewerCalls.Last().Messages[2].Content;
        Assert.StartsWith("I led the migration and kept the data consistent.", message);
        Assert.Contains("Answer took 33s via voice.", message);
        Assert.False(h.Vm.Composer.IsListening);
    }

    [Fact]
    public async Task A_pause_after_ten_words_submits_the_answer_when_silence_auto_submit_is_on()
    {
        var speech = new TestSpeech();
        var h = new Harness(new AppSettings { AutoListen = false, SilenceAutoSubmit = true, SilenceSeconds = 6 }, speech);
        h.Vm.Begin(Request());
        await h.Vm.Composer.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Final("I led the migration and the hardest part was keeping data consistent.");

        h.Clock.Advance(5); h.Vm.Tick();
        Assert.Single(h.Vm.Conversation);                                  // not yet
        h.Clock.Advance(1); h.Vm.Tick();

        Assert.Equal(["Interviewer", "You", "Interviewer"], h.Vm.Conversation.Select(c => c.Speaker));
    }

    [Fact]
    public async Task Audio_only_hides_the_words_when_a_voice_works_and_shows_them_anyway_when_it_does_not()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request(showText: false));
        Assert.Equal(MockViewModel.AudioOnlyText, h.Vm.InterviewerLine);
        Assert.True(h.Vm.IsLineHidden);

        h.Vm.ShowQuestionText = true;                                   // the user can turn it on again while it runs
        Assert.Contains("Hi, thanks", h.Vm.InterviewerLine);

        var broken = new TestSpeech { TextToSpeechReadiness = SpeechReadiness.NotReady("To hear questions with an OpenAI voice, add your OpenAI API key in Settings.") };
        var noVoice = new Harness(speech: broken);
        noVoice.Vm.Begin(Request(showText: false));
        Assert.Contains("Hi, thanks", noVoice.Vm.InterviewerLine);        // there is no way to hear it, so it is shown
        Assert.Contains("OpenAI API key", noVoice.Vm.SpeechNotice);
        Assert.True(noVoice.Vm.HasSpeechNotice);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Repeat_says_the_last_line_again_or_explains_why_it_cannot()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request());

        await h.Vm.RepeatCommand.ExecuteAsync(null);
        Assert.Equal(2, speech.Voice.Spoken.Count);
        Assert.Equal(speech.Voice.Spoken[0], speech.Voice.Spoken[1]);

        speech.TextToSpeechReadiness = SpeechReadiness.NotReady("no voice");
        await h.Vm.RepeatCommand.ExecuteAsync(null);
        Assert.Equal(2, speech.Voice.Spoken.Count);
        Assert.Equal("no voice", h.Vm.SpeechNotice);
    }

    [Fact]
    public void A_voice_that_fails_shows_why_and_the_interview_goes_on()
    {
        var speech = new TestSpeech();
        speech.Voice.Throw = new InterviewCoach.Core.Speech.SpeechException("The voice could not be played through the speakers.");
        var h = new Harness(speech: speech);

        h.Vm.Begin(Request());

        Assert.Contains("The voice could not be played", h.Vm.SpeechNotice);
        Assert.Contains("The text is on screen", h.Vm.SpeechNotice);
        Assert.Equal(MockPhase.CandidateAnswering, h.Vm.Phase);
        Assert.True(h.Vm.HasInterviewerLine);
    }

    // ---- ending

    [Fact]
    public async Task Answering_to_the_end_shows_the_debrief_once_and_nothing_was_coached_before_that()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        Assert.DoesNotContain(h.Llm.Calls, c => c.Role is LlmRole.Coach or LlmRole.Debrief);

        await h.AnswerUntilDebriefAsync();

        Assert.Equal(1, h.DebriefsRaised);
        Assert.NotNull(h.Debrief);
        Assert.Equal("Lean yes", h.Debrief!.SignalLabel);
        Assert.Equal(2, h.Debrief.Threads.Count);
        Assert.Single(await h.History.ListAsync());                         // the interview was kept
    }

    [Fact]
    public async Task End_interview_asks_first_and_declining_changes_nothing()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        h.Dialogs.ConfirmAnswer = false;

        await h.Vm.EndInterviewCommand.ExecuteAsync(null);

        Assert.Equal(["End the interview"], h.Dialogs.Confirmations);
        Assert.True(h.Vm.CanEnd);
        Assert.Null(h.Debrief);
        Assert.DoesNotContain(h.Llm.Calls, c => c.Role == LlmRole.Debrief);
    }

    [Fact]
    public async Task Ending_the_interview_goes_straight_to_the_debrief_with_no_closing_line()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        h.Vm.AnswerText = "Good, thanks.";
        await h.Vm.SubmitCommand.ExecuteAsync(null);                         // now the main question is on screen
        var interviewerTurns = h.Vm.Conversation.Count(c => c.IsInterviewer);

        await h.Vm.EndInterviewCommand.ExecuteAsync(null);

        Assert.NotNull(h.Debrief);
        Assert.Equal(interviewerTurns, h.Vm.Conversation.Count(c => c.IsInterviewer));   // nobody said goodbye
        Assert.Single(h.Debrief!.Threads);
        Assert.True(h.Debrief.Threads[0].IsNotAnswered);
    }

    [Fact]
    public async Task Ending_closes_the_microphone_and_stops_the_voice()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request());
        await h.Vm.Composer.ToggleMicCommand.ExecuteAsync(null);

        await h.Vm.EndInterviewCommand.ExecuteAsync(null);

        Assert.True(speech.Mic.Stopped);
        Assert.False(h.Vm.Composer.IsListening);
        Assert.False(h.Vm.CanEnd);
    }

    [Fact]
    public async Task Leaving_the_screen_midway_stops_everything_and_a_new_interview_starts_clean()
    {
        var speech = new TestSpeech();
        var h = new Harness(speech: speech);
        h.Vm.Begin(Request());
        await h.Vm.Composer.ToggleMicCommand.ExecuteAsync(null);

        h.Vm.Abandon();

        Assert.True(speech.Mic.Stopped);
        Assert.Null(h.Debrief);

        h.Vm.Begin(Request(round: RoundType.Behavioral));
        Assert.Single(h.Vm.Conversation);
        Assert.Equal("Senior Backend Engineer · Behavioral · 15 min", h.Vm.RoleLine);
    }

    // ---- failures

    [Fact]
    public async Task A_failed_interviewer_turn_keeps_the_conversation_and_Retry_carries_on()
    {
        var h = new Harness();
        h.Vm.Begin(Request());
        h.Flaky.FailRole = LlmRole.Interviewer;
        h.Vm.AnswerText = "Good, thanks.";

        await h.Vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(h.Vm.IsFailed);
        Assert.Contains("the Interviewer is unavailable", h.Vm.ErrorText);
        Assert.Contains(h.Vm.Conversation, c => c.Text == "Good, thanks.");   // the answer is kept
        h.Flaky.FailRole = null;

        await h.Vm.RetryCommand.ExecuteAsync(null);

        Assert.False(h.Vm.IsFailed);
        Assert.Equal(1, h.Vm.Conversation.Count(c => c.Text == "Good, thanks."));
        Assert.Contains("Walk me through a project", h.Vm.InterviewerLine);
    }
}
