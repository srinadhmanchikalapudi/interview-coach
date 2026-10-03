using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>The real Practice and Settings screens with speech: what is shown, what is hidden, and that every binding resolves.</summary>
public class VoiceViewTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public void Save(AppSettings settings) { Current = settings; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private sealed class Mic : ISpeechToText
    {
        public bool SupportsPartials => true;
        public event EventHandler<string>? PartialRecognized;
        public event EventHandler<string>? FinalRecognized;
        public event EventHandler<string>? Error;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Partial(string t) => PartialRecognized?.Invoke(this, t);
        public void Final(string t) => FinalRecognized?.Invoke(this, t);
        public void Fail(string t) => Error?.Invoke(this, t);
    }

    private sealed class Factory : ISpeechFactory
    {
        public FakeTextToSpeech Voice { get; } = new() { PerCharacter = TimeSpan.Zero };
        public Mic? Current { get; private set; }
        public SpeechReadiness TextToSpeechReadiness { get; set; } = SpeechReadiness.Ready;
        public SpeechReadiness SpeechToTextReadiness { get; set; } = SpeechReadiness.Ready;
        public ITextToSpeech TextToSpeech => Voice;
        public ISpeechToText CreateSpeechToText() => Current = new Mic();
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static PracticeViewModel Started(ISpeechFactory? speech)
    {
        var vm = new PracticeViewModel(new StubLlm(), Prompts, new MemorySettings(new AppSettings { SpeakQuestions = false, AutoListen = false }), null, () => 0.0, speech: speech);
        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        return vm;
    }

    // ---- helpers shared with the other view tests

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 1600));
            view.Arrange(new Rect(0, 0, 1000, 1600));
            view.UpdateLayout();
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static bool Shown(UIElement e)
    {
        for (DependencyObject? node = e; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static List<string> Texts(DependencyObject root)
        => Descendants<TextBlock>(root).Where(Shown).Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : t.Text).ToList();

    private static List<string> ButtonTexts(DependencyObject root) => Descendants<Button>(root).Where(Shown).SelectMany(b => Texts(b)).ToList();

    // ---- Practice

    [Fact]
    public void The_answer_screen_offers_the_microphone_and_repeat_when_speech_is_built_in_and_binds_cleanly()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new PracticeView { DataContext = Started(new Factory()) };
            Layout(view);

            var buttons = ButtonTexts(view);
            Assert.Contains("Speak", buttons);
            Assert.Contains("Repeat", buttons);
            Assert.Contains("Submit", buttons);
            var f2 = Assert.Single(view.InputBindings.OfType<KeyBinding>());
            Assert.Equal(Key.F2, f2.Key);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Without_speech_there_is_no_microphone_or_repeat_button()
    {
        WpfHost.Run(() =>
        {
            var view = new PracticeView { DataContext = Started(null) };
            Layout(view);

            var buttons = ButtonTexts(view);
            Assert.DoesNotContain("Speak", buttons);
            Assert.DoesNotContain("Repeat", buttons);
            Assert.Contains("Submit", buttons);
        });
    }

    [Fact]
    public void While_listening_the_button_says_stop_and_the_partial_words_and_status_are_shown()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var speech = new Factory();
            var vm = Started(speech);
            var view = new PracticeView { DataContext = vm };
            vm.ToggleMicCommand.Execute(null);
            speech.Current!.Partial("I led the migration");
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Stop", ButtonTexts(view));
            Assert.Contains("… I led the migration", texts);
            Assert.Contains("Listening… say your answer.", texts);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void A_speech_problem_is_shown_in_words_under_the_box_and_the_box_stays()
    {
        WpfHost.Run(() =>
        {
            var speech = new Factory { SpeechToTextReadiness = SpeechReadiness.NotReady("To dictate your answer, add your Azure Speech key and region in Settings. You can still type.") };
            var vm = Started(speech);
            var view = new PracticeView { DataContext = vm };
            vm.ToggleMicCommand.Execute(null);
            Layout(view);

            Assert.Contains(Texts(view), t => t.StartsWith("To dictate your answer"));
            Assert.Contains(Descendants<TextBox>(view), b => Shown(b) && b.AcceptsReturn);
        });
    }

    [Fact]
    public void Dictated_words_go_into_the_box_and_the_cursor_moves_to_the_end_of_them()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var speech = new Factory();
            var vm = Started(speech);
            var view = new PracticeView { DataContext = vm };
            Layout(view);
            var box = Descendants<TextBox>(view).Single(Shown);
            box.Text = "Start. End.";
            box.CaretIndex = "Start.".Length;               // the view tells the model where the cursor is

            vm.ToggleMicCommand.Execute(null);
            speech.Current!.Final("Middle part.");
            Layout(view);

            Assert.Equal("Start. Middle part. End.", box.Text);
            Assert.Equal("Start. Middle part.".Length, box.CaretIndex);
            Assert.Equal("mixed", vm.InputMethod);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_model_answer_card_has_a_read_aloud_button_only_when_speech_is_built_in()
    {
        var withSpeech = Started(new Factory());
        withSpeech.AnswerText = "My answer.";
        await withSpeech.SubmitCommand.ExecuteAsync(null);
        var without = Started(null);
        without.AnswerText = "My answer.";
        await without.SubmitCommand.ExecuteAsync(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var on = new PracticeView { DataContext = withSpeech };
            var off = new PracticeView { DataContext = without };
            Layout(on);
            Layout(off);

            Assert.Contains("Read aloud", ButtonTexts(on));
            Assert.DoesNotContain("Read aloud", ButtonTexts(off));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    // ---- Settings

    private static SettingsViewModel NewSettings(out MemorySettings store, out Factory speech, AppSettings? settings = null)
    {
        store = new MemorySettings(settings ?? new AppSettings());
        speech = new Factory();
        return new SettingsViewModel(store, new FakeLlmService(), speech: speech);
    }

    [Fact]
    public void The_speech_section_no_longer_says_voice_is_on_its_way_and_offers_the_voice_tests_and_binds_cleanly()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = NewSettings(out _, out _);
            var view = new SettingsView { DataContext = vm };
            Layout(view);

            var texts = Texts(view);
            Assert.DoesNotContain(texts, t => t.Contains("on its way"));
            var buttons = ButtonTexts(view);
            Assert.Contains("Load voices", buttons);
            Assert.Contains("Test voice", buttons);
            Assert.Contains("Test microphone", buttons);
            Assert.Contains(Descendants<CheckBox>(view), c => Shown(c) && Texts(c).Contains("Speak each Practice question aloud"));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void The_voice_and_the_speak_questions_choice_are_saved_with_the_other_settings()
    {
        var vm = NewSettings(out var store, out _);
        vm.Voice = "en-GB-RyanNeural";
        vm.SpeakQuestions = false;

        vm.SaveCommand.Execute(null);

        Assert.Equal("en-GB-RyanNeural", store.Current.Voice);
        Assert.False(store.Current.SpeakQuestions);

        vm.Voice = "  ";
        vm.SaveCommand.Execute(null);
        Assert.Null(store.Current.Voice);
    }

    [Fact]
    public async Task Load_voices_fills_the_list_and_keeps_the_saved_voice()
    {
        var vm = NewSettings(out _, out _, new AppSettings { Voice = "demo" });

        await vm.LoadVoicesCommand.ExecuteAsync(null);

        Assert.Single(vm.Voices);
        Assert.Equal("demo", vm.Voice);
        Assert.Contains("1 voices", vm.SpeechStatus);
    }

    [Fact]
    public async Task Test_voice_speaks_a_sample_and_says_what_to_add_when_the_voice_is_not_ready()
    {
        var vm = NewSettings(out _, out var speech);
        await vm.TestVoiceCommand.ExecuteAsync(null);
        Assert.Single(speech.Voice.Spoken);
        Assert.Contains("heard that", vm.SpeechStatus);

        speech.TextToSpeechReadiness = SpeechReadiness.NotReady("To hear questions with an OpenAI voice, add your OpenAI API key in Settings.");
        await vm.TestVoiceCommand.ExecuteAsync(null);
        Assert.Single(speech.Voice.Spoken);
        Assert.Contains("OpenAI API key", vm.SpeechStatus);
    }

    [Fact]
    public async Task Test_microphone_listens_then_shows_what_was_heard_or_that_nothing_was()
    {
        var vm = NewSettings(out _, out var speech);

        await vm.TestMicCommand.ExecuteAsync(null);
        Assert.True(vm.IsListeningTest);
        Assert.Equal("Stop and show what was heard", vm.MicTestLabel);
        speech.Current!.Final("Testing one two three.");
        await vm.TestMicCommand.ExecuteAsync(null);
        Assert.False(vm.IsListeningTest);
        Assert.Equal("Heard: Testing one two three.", vm.SpeechStatus);

        await vm.TestMicCommand.ExecuteAsync(null);
        await vm.TestMicCommand.ExecuteAsync(null);
        Assert.StartsWith("Nothing was heard", vm.SpeechStatus);
    }

    [Fact]
    public async Task A_speech_test_with_unsaved_changes_asks_for_a_save_first_because_it_uses_the_saved_settings()
    {
        var vm = NewSettings(out _, out var speech);
        vm.TextToSpeech = TtsProvider.OpenAi;

        await vm.TestVoiceCommand.ExecuteAsync(null);

        Assert.Equal("Click Save first. The speech test uses your saved settings.", vm.SpeechStatus);
        Assert.Empty(speech.Voice.Spoken);

        vm.SaveCommand.Execute(null);
        await vm.TestVoiceCommand.ExecuteAsync(null);
        Assert.Single(speech.Voice.Spoken);
    }

    [Fact]
    public async Task A_microphone_error_during_the_test_is_shown()
    {
        var vm = NewSettings(out _, out var speech);
        await vm.TestMicCommand.ExecuteAsync(null);

        speech.Current!.Fail("No microphone was found.");

        Assert.Equal("No microphone was found.", vm.SpeechStatus);
    }
}
