using System.IO;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>Practice with a microphone and a voice: dictation, spoken questions, barge-in, auto-listen, silence submit, read aloud.</summary>
public class PracticeVoiceTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>Recognizers that hear whatever the test says, when the test says it.</summary>
    private sealed class ScriptedStt : ISpeechToText
    {
        public bool SupportsPartials { get; init; } = true;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public string? SayOnStop { get; set; }

        public event EventHandler<string>? PartialRecognized;
        public event EventHandler<string>? FinalRecognized;
        public event EventHandler<string>? Error;

        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }

        public Task StopAsync()
        {
            Stopped = true;
            if (SayOnStop is { } text) FinalRecognized?.Invoke(this, text);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public void Partial(string text) => PartialRecognized?.Invoke(this, text);
        public void Final(string text) => FinalRecognized?.Invoke(this, text);
        public void Fail(string message) => Error?.Invoke(this, message);
    }

    private sealed class FakeFactory : ISpeechFactory
    {
        public FakeTextToSpeech Voice { get; } = new() { PerCharacter = TimeSpan.Zero };
        public List<ScriptedStt> Recognizers { get; } = [];
        public SpeechReadiness TextToSpeechReadiness { get; set; } = SpeechReadiness.Ready;
        public SpeechReadiness SpeechToTextReadiness { get; set; } = SpeechReadiness.Ready;
        public Func<ScriptedStt> Next { get; set; } = () => new ScriptedStt();
        public ITextToSpeech TextToSpeech => Voice;
        public ScriptedStt Mic => Recognizers[^1];

        public ISpeechToText CreateSpeechToText()
        {
            var stt = Next();
            Recognizers.Add(stt);
            return stt;
        }
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static PracticeViewModel Started(out StubLlm llm, out FakeFactory speech, out Clockwork clock, AppSettings? settings = null, bool begin = true)
    {
        llm = new StubLlm();
        speech = new FakeFactory();
        clock = new Clockwork();
        var c = clock;
        var s = settings ?? new AppSettings { SpeakQuestions = false, AutoListen = false };
        var vm = new PracticeViewModel(llm, Prompts, new MemorySettings(s), null, () => 0.0, () => c.Now, speech: speech);
        if (begin) vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        return vm;
    }

    private static string LastCoachPrompt(StubLlm llm) => llm.CoachPrompts.Last();

    // ---- dictation

    [Fact]
    public async Task The_mic_button_starts_listening_and_shows_what_is_heard_before_it_goes_into_the_box()
    {
        var vm = Started(out _, out var speech, out _);

        await vm.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Partial("In my last role I");

        Assert.True(vm.IsListening);
        Assert.Equal("Stop", vm.MicLabel);
        Assert.Equal("In my last role I", vm.PartialText);
        Assert.True(vm.HasPartial);
        Assert.Equal("", vm.AnswerText);                      // not in the box until the phrase is final
        Assert.Equal("Listening… say your answer.", vm.ListeningStatus);

        speech.Mic.Final("In my last role I led a migration.");

        Assert.Equal("In my last role I led a migration.", vm.AnswerText);
        Assert.Equal("", vm.PartialText);
        Assert.Equal("voice", vm.InputMethod);
    }

    [Fact]
    public async Task Each_phrase_is_added_after_the_last_with_one_space()
    {
        var vm = Started(out _, out var speech, out _);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        speech.Mic.Final("First point.");
        speech.Mic.Final("Second point.");

        Assert.Equal("First point. Second point.", vm.AnswerText);
    }

    [Fact]
    public async Task Words_are_inserted_where_the_cursor_is_and_the_view_is_told_where_to_put_it_after()
    {
        var vm = Started(out _, out var speech, out _);
        vm.AnswerText = "I moved the cache to Redis. The result was good.";
        var carets = new List<int>();
        vm.CaretRequested += carets.Add;
        vm.SetCaret("I moved the cache to Redis.".Length);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        speech.Mic.Final("It cut latency in half.");

        Assert.Equal("I moved the cache to Redis. It cut latency in half. The result was good.", vm.AnswerText);
        Assert.Equal(["I moved the cache to Redis. It cut latency in half.".Length], carets);
    }

    [Fact]
    public async Task Typing_and_talking_in_the_same_box_make_the_answer_mixed_and_the_coach_is_told_so()
    {
        var vm = Started(out var llm, out var speech, out var clock);
        vm.AnswerText = "I led the move to services.";
        Assert.Equal("typed", vm.InputMethod);

        await vm.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Final("The hard part was data consistency.");
        Assert.Equal("mixed", vm.InputMethod);
        clock.Advance(75);
        await vm.SubmitCommand.ExecuteAsync(null);

        var prompt = LastCoachPrompt(llm);
        Assert.Contains("input_method=\"mixed\"", prompt);
        Assert.Contains("duration_seconds=\"75\"", prompt);   // speaking time is real, so the coach gets it
    }

    [Fact]
    public async Task A_purely_typed_answer_is_sent_as_typed_with_no_duration()
    {
        var vm = Started(out var llm, out _, out var clock);
        vm.AnswerText = "Typed only.";
        clock.Advance(40);

        await vm.SubmitCommand.ExecuteAsync(null);

        var prompt = LastCoachPrompt(llm);
        Assert.Contains("input_method=\"typed\"", prompt);
        Assert.DoesNotContain("duration_seconds=\"40\"", prompt); // typing time says nothing about delivery
    }

    [Fact]
    public async Task A_dictated_answer_that_is_then_fixed_by_hand_counts_as_mixed_and_one_that_is_cleared_starts_over()
    {
        var vm = Started(out _, out var speech, out _);
        await vm.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Final("We used red is for the cache.");
        Assert.Equal("voice", vm.InputMethod);

        vm.AnswerText = "We used Redis for the cache.";          // fixing a mis-heard word by hand
        Assert.Equal("mixed", vm.InputMethod);

        vm.AnswerText = "";                                       // start again
        Assert.Equal("typed", vm.InputMethod);
    }

    [Fact]
    public async Task The_timer_starts_the_first_time_the_microphone_is_used()
    {
        var vm = Started(out _, out _, out var clock);
        clock.Advance(30);
        vm.Tick();
        Assert.Equal("0:00", vm.TimerText);

        await vm.ToggleMicCommand.ExecuteAsync(null);
        clock.Advance(20);
        vm.Tick();

        Assert.Equal("0:20", vm.TimerText);
    }

    [Fact]
    public async Task Stopping_the_mic_lets_a_record_then_transcribe_provider_deliver_its_words_and_closes_it()
    {
        var vm = Started(out _, out var speech, out _);
        speech.Next = () => new ScriptedStt { SupportsPartials = false, SayOnStop = "Transcribed after stopping." };

        await vm.ToggleMicCommand.ExecuteAsync(null);
        Assert.Equal("Recording… your words appear when you click Stop.", vm.ListeningStatus);
        Assert.Equal("", vm.AnswerText);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        Assert.False(vm.IsListening);
        Assert.Equal("Transcribed after stopping.", vm.AnswerText);
        Assert.True(speech.Mic.Stopped);
        Assert.True(speech.Mic.Disposed);
    }

    [Fact]
    public async Task Submit_closes_the_microphone_first_so_the_last_words_are_in_the_answer()
    {
        var vm = Started(out var llm, out var speech, out _);
        speech.Next = () => new ScriptedStt { SupportsPartials = false, SayOnStop = "All of it was said into the microphone." };
        await vm.ToggleMicCommand.ExecuteAsync(null);

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.False(vm.IsListening);
        Assert.Equal("All of it was said into the microphone.", vm.SubmittedAnswer);
        Assert.Contains("input_method=\"voice\"", LastCoachPrompt(llm));
    }

    // ---- when speech is not available or breaks

    [Fact]
    public async Task Without_a_key_the_mic_button_says_what_to_add_and_typing_still_works()
    {
        var vm = Started(out _, out var speech, out _);
        speech.SpeechToTextReadiness = SpeechReadiness.NotReady("To dictate your answer, add your Azure Speech key and region in Settings. You can still type.");

        await vm.ToggleMicCommand.ExecuteAsync(null);

        Assert.False(vm.IsListening);
        Assert.Empty(speech.Recognizers);
        Assert.Contains("Azure Speech key", vm.SpeechMessage);
        Assert.True(vm.HasSpeechMessage);
        vm.AnswerText = "Typed instead.";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.True(vm.IsFeedback);
    }

    [Fact]
    public async Task A_recognition_error_is_shown_the_text_is_kept_and_the_mic_closes()
    {
        var vm = Started(out _, out var speech, out _);
        await vm.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Final("Some words already in the box.");

        speech.Mic.Fail("Azure rejected the speech key or region. Check both in Settings.");

        Assert.False(vm.IsListening);
        Assert.Equal("Azure rejected the speech key or region. Check both in Settings.", vm.SpeechMessage);
        Assert.Equal("Some words already in the box.", vm.AnswerText);
        Assert.True(vm.IsAnswering);
        Assert.True(speech.Mic.Disposed);
    }

    [Fact]
    public async Task Starting_to_listen_clears_an_old_message_and_listening_again_works_after_an_error()
    {
        var vm = Started(out _, out var speech, out _);
        await vm.ToggleMicCommand.ExecuteAsync(null);
        speech.Mic.Fail("No microphone is available.");
        Assert.True(vm.HasSpeechMessage);

        await vm.ToggleMicCommand.ExecuteAsync(null);

        Assert.True(vm.IsListening);
        Assert.False(vm.HasSpeechMessage);
        Assert.Equal(2, speech.Recognizers.Count);
    }

    [Fact]
    public async Task Leaving_the_screen_closes_the_microphone_and_stops_the_voice()
    {
        var vm = Started(out _, out var speech, out _);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        vm.ExitCommand.Execute(null);

        Assert.False(vm.IsListening);
        Assert.True(speech.Mic.Stopped);
    }

    // ---- spoken questions

    [Fact]
    public void A_new_question_is_spoken_when_the_setting_is_on_with_the_formatting_removed()
    {
        var vm = Started(out var llm, out var speech, out _, new AppSettings { SpeakQuestions = true, AutoListen = false }, begin: false);
        llm.QuestionText = "Tell me about a **hard** call → what did you choose?";

        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));

        Assert.Equal(["Tell me about a hard call , then what did you choose?"], speech.Voice.Spoken);
    }

    [Fact]
    public void Questions_are_not_spoken_when_the_setting_is_off_or_the_voice_is_not_ready()
    {
        var off = Started(out _, out var speechOff, out _);
        Assert.Empty(speechOff.Voice.Spoken);
        Assert.True(off.IsAnswering);

        var vm = Started(out _, out var speech, out _, new AppSettings { SpeakQuestions = true }, begin: false);
        speech.TextToSpeechReadiness = SpeechReadiness.NotReady("To hear questions with the Azure voice, add your key.");
        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));

        Assert.Empty(speech.Voice.Spoken);
        Assert.False(vm.HasSpeechMessage);    // the automatic voice stays quiet; the Repeat button explains when it is pressed
    }

    [Fact]
    public async Task Repeat_says_the_question_again_or_explains_why_it_cannot()
    {
        var vm = Started(out _, out var speech, out _);

        await vm.RepeatQuestionCommand.ExecuteAsync(null);
        Assert.Equal(["Practice question 1?"], speech.Voice.Spoken);

        speech.TextToSpeechReadiness = SpeechReadiness.NotReady("To hear questions with an OpenAI voice, add your OpenAI API key in Settings.");
        await vm.RepeatQuestionCommand.ExecuteAsync(null);

        Assert.Single(speech.Voice.Spoken);
        Assert.Contains("OpenAI API key", vm.SpeechMessage);
    }

    [Fact]
    public async Task A_voice_that_fails_shows_why_and_the_question_stays_on_screen()
    {
        var factory = new FailingVoiceFactory(new FakeFactory(), new ThrowingVoice());
        var broken = new PracticeViewModel(new StubLlm(), Prompts, new MemorySettings(new AppSettings { SpeakQuestions = false }), null, () => 0.0, null, speech: factory);
        broken.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));

        await broken.RepeatQuestionCommand.ExecuteAsync(null);

        Assert.Contains("The voice could not be played", broken.SpeechMessage);
        Assert.Contains("The text is on screen", broken.SpeechMessage);
        Assert.True(broken.IsAnswering);
    }

    private sealed class ThrowingVoice : ITextToSpeech
    {
        public Task SpeakAsync(string text, CancellationToken ct) => throw new InterviewCoach.Core.Speech.SpeechException("The voice could not be played through the speakers.");
        public void Stop() { }
        public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceInfo>>([]);
    }

    private sealed class FailingVoiceFactory(ISpeechFactory inner, ITextToSpeech voice) : ISpeechFactory
    {
        public ITextToSpeech TextToSpeech => voice;
        public ISpeechToText CreateSpeechToText() => inner.CreateSpeechToText();
        public SpeechReadiness TextToSpeechReadiness => SpeechReadiness.Ready;
        public SpeechReadiness SpeechToTextReadiness => SpeechReadiness.Ready;
    }

    [Fact]
    public async Task Starting_the_mic_while_the_question_is_still_being_read_stops_the_voice()
    {
        var slow = new TaskCompletionSource();
        var voice = new HoldingVoice(slow);
        var factory = new HoldingFactory(new FakeFactory(), voice);
        var held = new PracticeViewModel(new StubLlm(), Prompts, new MemorySettings(new AppSettings { SpeakQuestions = false }), null, () => 0.0, null, speech: factory);
        held.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));

        var speaking = held.RepeatQuestionCommand.ExecuteAsync(null);
        Assert.True(held.IsSpeaking);
        await held.ToggleMicCommand.ExecuteAsync(null);   // barge-in

        Assert.False(held.IsSpeaking);
        Assert.True(held.IsListening);
        Assert.Equal(1, voice.StopCount);
        Assert.True(voice.LastToken.IsCancellationRequested);
        slow.SetResult();
        await speaking;
    }

    private sealed class HoldingVoice(TaskCompletionSource release) : ITextToSpeech
    {
        public int StopCount { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public async Task SpeakAsync(string text, CancellationToken ct) { LastToken = ct; await release.Task; }
        public void Stop() => StopCount++;
        public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceInfo>>([]);
    }

    private sealed class HoldingFactory(FakeFactory inner, ITextToSpeech voice) : ISpeechFactory
    {
        public ITextToSpeech TextToSpeech => voice;
        public ISpeechToText CreateSpeechToText() => inner.CreateSpeechToText();
        public SpeechReadiness TextToSpeechReadiness => SpeechReadiness.Ready;
        public SpeechReadiness SpeechToTextReadiness => SpeechReadiness.Ready;
    }

    // ---- auto-listen

    [Fact]
    public void After_the_question_has_been_spoken_the_mic_opens_when_auto_listen_is_on()
    {
        var vm = Started(out _, out var speech, out _, new AppSettings { SpeakQuestions = true, AutoListen = true }, begin: false);

        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));

        Assert.True(vm.IsListening);
        Assert.True(speech.Mic.Started);
    }

    [Fact]
    public void Auto_listen_stays_off_when_it_is_off_or_when_speech_to_text_is_not_ready()
    {
        var off = Started(out _, out var speechOff, out _, new AppSettings { SpeakQuestions = true, AutoListen = false }, begin: false);
        off.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        Assert.False(off.IsListening);
        Assert.Empty(speechOff.Recognizers);

        var vm = Started(out _, out var speech, out _, new AppSettings { SpeakQuestions = true, AutoListen = true }, begin: false);
        speech.SpeechToTextReadiness = SpeechReadiness.NotReady("add a key");
        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        Assert.False(vm.IsListening);
        Assert.False(vm.HasSpeechMessage);     // not an error: the user did not ask for the microphone
    }

    // ---- silence auto-submit

    private static async Task<(PracticeViewModel Vm, StubLlm Llm, FakeFactory Speech, Clockwork Clock)> Listening(bool autoSubmit, int seconds = 6)
    {
        var vm = Started(out var llm, out var speech, out var clock, new AppSettings { SpeakQuestions = false, AutoListen = false, SilenceAutoSubmit = autoSubmit, SilenceSeconds = seconds });
        await vm.ToggleMicCommand.ExecuteAsync(null);
        return (vm, llm, speech, clock);
    }

    private const string TwelveWords = "I led the migration and the hardest part was keeping data consistent.";

    [Fact]
    public async Task A_long_enough_pause_after_at_least_ten_words_submits_the_answer()
    {
        var (vm, llm, speech, clock) = await Listening(autoSubmit: true, seconds: 6);
        speech.Mic.Final(TwelveWords);

        clock.Advance(5); vm.Tick();
        Assert.True(vm.IsAnswering);                  // not yet
        clock.Advance(1); vm.Tick();

        Assert.True(vm.IsFeedback);
        Assert.Single(llm.CoachPrompts);
        Assert.Contains("input_method=\"voice\"", LastCoachPrompt(llm));
        Assert.False(vm.IsListening);
    }

    [Fact]
    public async Task Speech_heard_again_restarts_the_silence_and_a_short_answer_is_never_auto_submitted()
    {
        var (vm, llm, speech, clock) = await Listening(autoSubmit: true, seconds: 6);
        speech.Mic.Final(TwelveWords);
        clock.Advance(5); vm.Tick();
        speech.Mic.Partial("and then");                // still talking
        clock.Advance(5); vm.Tick();
        Assert.True(vm.IsAnswering);

        var (shortVm, shortLlm, shortSpeech, shortClock) = await Listening(autoSubmit: true, seconds: 3);
        shortSpeech.Mic.Final("Too short to submit.");
        shortClock.Advance(60); shortVm.Tick();
        Assert.True(shortVm.IsAnswering);
        Assert.Empty(shortLlm.CoachPrompts);
        Assert.Empty(llm.CoachPrompts);
    }

    [Fact]
    public async Task Silence_does_nothing_when_the_setting_is_off()
    {
        var (vm, llm, speech, clock) = await Listening(autoSubmit: false);
        speech.Mic.Final(TwelveWords);

        clock.Advance(120); vm.Tick();

        Assert.True(vm.IsAnswering);
        Assert.Empty(llm.CoachPrompts);
    }

    // ---- read aloud

    [Fact]
    public async Task The_model_answer_can_be_read_aloud_and_stopped()
    {
        var vm = Started(out _, out var speech, out _);
        vm.AnswerText = "My answer.";
        await vm.SubmitCommand.ExecuteAsync(null);
        var coach = vm.Coach!;
        Assert.True(coach.CanReadAloud);
        Assert.Equal("Read aloud", coach.ReadAloudLabel);

        await vm.ReadAloudCommand.ExecuteAsync(null);

        Assert.Equal(["I cut p99 by caching the hot lookups."], speech.Voice.Spoken);
        Assert.False(coach.IsReading);                // it finished
    }

    [Fact]
    public async Task While_the_model_answer_is_being_read_the_button_offers_to_stop_it()
    {
        var release = new TaskCompletionSource();
        var voice = new HoldingVoice(release);
        var vm2 = new PracticeViewModel(new StubLlm(), Prompts, new MemorySettings(new AppSettings { SpeakQuestions = false }), null, () => 0.0, null,
            speech: new HoldingFactory(new FakeFactory(), voice));
        vm2.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        vm2.AnswerText = "My answer.";
        await vm2.SubmitCommand.ExecuteAsync(null);

        var reading = vm2.ReadAloudCommand.ExecuteAsync(null);
        Assert.True(vm2.Coach!.IsReading);
        Assert.Equal("Stop reading", vm2.Coach.ReadAloudLabel);

        await vm2.ReadAloudCommand.ExecuteAsync(null);     // a second press stops it
        Assert.False(vm2.Coach.IsReading);
        Assert.Equal(1, voice.StopCount);
        release.SetResult();
        await reading;
    }

    [Fact]
    public async Task Learn_mode_and_a_build_without_speech_show_no_read_aloud_button()
    {
        var llm = new StubLlm();
        var plain = new PracticeViewModel(llm, Prompts, new MemorySettings(new AppSettings()), null, () => 0.0);
        plain.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        plain.AnswerText = "My answer.";
        await plain.SubmitCommand.ExecuteAsync(null);

        Assert.False(plain.HasSpeech);
        Assert.False(plain.Coach!.CanReadAloud);
        await plain.ToggleMicCommand.ExecuteAsync(null);   // harmless
        Assert.False(plain.IsListening);
    }

    [Fact]
    public async Task A_new_question_stops_a_question_that_is_still_being_read()
    {
        var release = new TaskCompletionSource();
        var voice = new HoldingVoice(release);
        var vm = new PracticeViewModel(new StubLlm(), Prompts, new MemorySettings(new AppSettings { SpeakQuestions = false }), null, () => 0.0, null,
            speech: new HoldingFactory(new FakeFactory(), voice));
        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        var speaking = vm.RepeatQuestionCommand.ExecuteAsync(null);
        Assert.True(vm.IsSpeaking);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.False(vm.IsSpeaking);
        Assert.True(voice.StopCount >= 1);
        release.SetResult();
        await speaking;
    }
}
