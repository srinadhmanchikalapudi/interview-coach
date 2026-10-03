using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

public class PracticeTests
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

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static LearnSessionRequest Request(params QuestionType[] types)
        => new(Samples.CompleteProfile(), types.Length == 0 ? [QuestionType.Behavioral] : types, null, [], EmploymentType.FullTime);

    private static PracticeViewModel NewVm(out StubLlm llm, out Clockwork clock)
    {
        llm = new StubLlm();
        clock = new Clockwork();
        var c = clock;
        return new PracticeViewModel(llm, Prompts, new MemorySettings(new AppSettings()), null, () => 0.0, () => c.Now);
    }

    private static PracticeViewModel Started(out StubLlm llm, out Clockwork clock)
    {
        var vm = NewVm(out llm, out clock);
        vm.Begin(Request());
        return vm;
    }

    // ---- the first screen

    [Fact]
    public void A_session_opens_on_a_question_and_an_empty_answer_box_with_no_coaching()
    {
        var vm = Started(out var llm, out _);

        Assert.True(vm.IsAnswering);
        Assert.Equal("Practice question 1?", vm.Question);
        Assert.Equal("Behavioral", vm.TypeLabel);
        Assert.Equal("", vm.AnswerText);
        Assert.Equal("0 words", vm.WordCountText);
        Assert.Equal("0:00", vm.TimerText);
        Assert.Null(vm.Coach);
        Assert.False(vm.IsFeedback);
        Assert.Empty(llm.CoachPrompts);                         // nothing is coached before a submit
        Assert.False(vm.TryAgainCommand.CanExecute(null));
    }

    // ---- the answer box: word count and timer

    [Fact]
    public void The_word_count_follows_what_is_typed()
    {
        var vm = Started(out _, out _);

        vm.AnswerText = "one";
        Assert.Equal("1 word", vm.WordCountText);
        vm.AnswerText = "one two  three\nfour";
        Assert.Equal(4, vm.WordCount);
        Assert.Equal("4 words", vm.WordCountText);
    }

    [Fact]
    public void The_timer_starts_at_the_first_keystroke_not_when_the_question_appears()
    {
        var vm = Started(out _, out var clock);

        clock.Advance(40);
        vm.Tick();
        Assert.Equal("0:00", vm.TimerText);           // thinking time before typing does not count

        vm.AnswerText = "I";
        clock.Advance(95);
        vm.Tick();

        Assert.Equal("1:35", vm.TimerText);
        Assert.Equal(95, vm.ElapsedSeconds);
    }

    [Fact]
    public void The_timer_turns_amber_past_two_thirty_and_red_past_three_thirty_on_a_behavioral_question()
    {
        var vm = Started(out _, out var clock);
        vm.AnswerText = "I started";

        clock.Advance(149); vm.Tick();
        Assert.Equal(TimerLevel.Normal, vm.TimerLevel);
        clock.Advance(1); vm.Tick();
        Assert.Equal(TimerLevel.Amber, vm.TimerLevel);
        Assert.True(vm.IsTimerAmber);
        clock.Advance(59); vm.Tick();
        Assert.Equal(TimerLevel.Amber, vm.TimerLevel);
        clock.Advance(1); vm.Tick();
        Assert.Equal(TimerLevel.Red, vm.TimerLevel);
        Assert.True(vm.IsTimerRed);
        Assert.Equal("3:30", vm.TimerText);
    }

    [Fact]
    public void Project_questions_get_the_same_colours_and_concept_questions_never_do()
    {
        var project = NewVm(out var llm, out var clock);
        llm.QuestionType = "resume_deep_dive";
        project.Begin(Request(QuestionType.ResumeDeepDive));
        project.AnswerText = "Well";
        clock.Advance(220); project.Tick();
        Assert.Equal(TimerLevel.Red, project.TimerLevel);

        var concept = NewVm(out var llm2, out var clock2);
        llm2.QuestionType = "technical_concept";
        concept.Begin(Request(QuestionType.TechnicalConcept));
        concept.AnswerText = "A struct";
        clock2.Advance(400); concept.Tick();
        Assert.Equal(TimerLevel.Normal, concept.TimerLevel);
        Assert.Equal("6:40", concept.TimerText);
    }

    // ---- submitting

    [Fact]
    public async Task An_empty_submit_says_what_to_do_and_sends_nothing()
    {
        var vm = Started(out var llm, out _);

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.HasEmptyMessage);
        Assert.Equal("Say or type something first. 'I don't know' is a fine answer too.", vm.EmptyMessage);
        Assert.True(vm.IsAnswering);
        Assert.Empty(llm.CoachPrompts);

        vm.AnswerText = "I don't know";
        Assert.False(vm.HasEmptyMessage);   // the message goes as soon as something is written
    }

    [Fact]
    public async Task A_spaces_only_answer_counts_as_empty()
    {
        var vm = Started(out var llm, out _);
        vm.AnswerText = "   \n  ";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.HasEmptyMessage);
        Assert.Empty(llm.CoachPrompts);
    }

    [Fact]
    public async Task Submitting_shows_the_answer_and_the_feedback_that_quotes_it()
    {
        var vm = Started(out var llm, out var clock);
        vm.AnswerText = "We cut p99 to 80ms by caching.";
        clock.Advance(95);

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsFeedback);
        Assert.False(vm.IsAnswering);
        Assert.Equal("We cut p99 to 80ms by caching.", vm.SubmittedAnswer);
        Assert.Equal("7 words  ·  1:35", vm.SubmittedMeta);
        Assert.True(vm.ShowSubmittedAnswer);
        Assert.NotNull(vm.Coach);
        Assert.True(vm.Coach!.HasFeedback);
        Assert.Equal("cut p99 to 80ms", vm.Coach.Feedback[0].Quote);
        Assert.True(vm.Coach.HasDelivery);
        var prompt = Assert.Single(llm.CoachPrompts);
        Assert.Contains("Mode: practice", prompt);
        Assert.Contains("duration_seconds=\"(none)\"", prompt);   // the timer shows typing time, which the Coach is not told
        Assert.Contains("input_method=\"typed\"", prompt);
        Assert.Equal("Click one to answer it.", vm.Coach.FollowUpPrompt);
        Assert.True(vm.TryAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_timer_stops_when_the_answer_is_sent()
    {
        var vm = Started(out _, out var clock);
        vm.AnswerText = "answer";
        clock.Advance(30);
        await vm.SubmitCommand.ExecuteAsync(null);

        clock.Advance(500);
        vm.Tick();

        Assert.Equal("0:30", vm.TimerText);
    }

    // ---- trying again

    [Fact]
    public async Task Trying_again_gives_an_empty_box_resets_the_timer_and_offers_the_last_answer()
    {
        var vm = Started(out var llm, out var clock);
        vm.AnswerText = "first answer";
        clock.Advance(60);
        await vm.SubmitCommand.ExecuteAsync(null);

        vm.TryAgainCommand.Execute(null);

        Assert.True(vm.IsAnswering);
        Assert.Equal("Practice question 1?", vm.Question);      // the same question
        Assert.Equal("", vm.AnswerText);
        Assert.Equal("Attempt 2", vm.AttemptLabel);
        Assert.True(vm.HasAttemptLabel);
        Assert.Equal("0:00", vm.TimerText);
        Assert.Null(vm.Coach);
        Assert.True(vm.CanUsePreviousAnswer);

        vm.UsePreviousAnswerCommand.Execute(null);
        Assert.Equal("first answer", vm.AnswerText);

        vm.AnswerText = "first answer, improved";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("<previous_attempt>", llm.CoachPrompts.Last());
        Assert.Contains("first answer", llm.CoachPrompts.Last());
        Assert.Equal(1, llm.Calls.Count(c => c.Role == LlmRole.QuestionGenerator)); // the same question, not a new one
    }

    [Fact]
    public async Task Submitting_the_same_answer_again_says_so_and_sends_nothing()
    {
        var vm = Started(out var llm, out _);
        vm.AnswerText = "We cut p99 to 80ms by caching.";
        await vm.SubmitCommand.ExecuteAsync(null);
        vm.TryAgainCommand.Execute(null);
        vm.UsePreviousAnswerCommand.Execute(null);     // the same text back in the box

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.HasEmptyMessage);
        Assert.Equal(PracticeViewModel.UnchangedAnswerMessage, vm.EmptyMessage);
        Assert.True(vm.IsAnswering);
        Assert.Single(llm.CoachPrompts);

        vm.AnswerText += " Then we measured it.";
        Assert.False(vm.HasEmptyMessage);               // the message goes as soon as something is changed
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.True(vm.IsFeedback);
        Assert.Equal(2, llm.CoachPrompts.Count());
    }

    [Fact]
    public void A_quote_that_is_the_word_null_is_not_shown_as_a_quotation()
    {
        var coach = new CoachOutputViewModel(
            new CoachOutput { Feedback = [new FeedbackPoint { Kind = "fix", Point = "Add more.", Quote = "null" }], ModelAnswer = "x", Shape = "a" }, _ => { });

        Assert.False(coach.Feedback[0].HasQuote);
    }

    // ---- follow-ups and the next question

    [Fact]
    public async Task Choosing_a_follow_up_makes_it_the_question_with_a_fresh_box_and_no_coaching_yet()
    {
        var vm = Started(out var llm, out _);
        vm.AnswerText = "We added a cache.";
        await vm.SubmitCommand.ExecuteAsync(null);

        vm.Coach!.FollowUps[0].OpenCommand.Execute(null);

        Assert.True(vm.IsAnswering);
        Assert.Equal("Why Redis?", vm.Question);
        Assert.True(vm.IsFollowUp);
        Assert.Equal("Practice question 1?", vm.ParentQuestion);
        Assert.Equal("", vm.AnswerText);
        Assert.Null(vm.Coach);
        Assert.Single(llm.CoachPrompts);                       // only the first answer has been coached

        vm.AnswerText = "Memcached had no per-key expiry.";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("Interviewer: Practice question 1?", llm.CoachPrompts.Last());
        Assert.Contains("You: We added a cache.", llm.CoachPrompts.Last());
    }

    [Fact]
    public async Task Next_question_brings_a_new_question_and_a_clean_box()
    {
        var vm = Started(out _, out var clock);
        vm.AnswerText = "an answer";
        clock.Advance(20);
        await vm.SubmitCommand.ExecuteAsync(null);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsAnswering);
        Assert.Equal("Practice question 2?", vm.Question);
        Assert.Equal("", vm.AnswerText);
        Assert.Equal("0:00", vm.TimerText);
        Assert.False(vm.HasAttemptLabel);
        Assert.Null(vm.Coach);
    }

    [Fact]
    public async Task A_new_question_asks_the_view_to_scroll_to_the_top()
    {
        var vm = Started(out _, out _);
        var raised = 0;
        vm.QuestionChanged += () => raised++;

        vm.AnswerText = "x";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(0, raised);                       // feedback appears under the same question
        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(raised >= 1);                      // the new question starts at the top
    }

    // ---- when something fails the answer is never lost

    [Fact]
    public async Task If_the_coach_fails_the_answer_stays_on_screen_and_retry_sends_it_again()
    {
        var vm = Started(out var llm, out _);
        llm.FailCoach = true;
        vm.AnswerText = "an answer that must survive";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailed);
        Assert.True(vm.FailedAfterAnswer);
        Assert.Contains("the coach is unavailable", vm.ErrorText);
        Assert.Equal("an answer that must survive", vm.SubmittedAnswer);
        Assert.True(vm.ShowSubmittedAnswer);
        Assert.Equal("Practice question 1?", vm.Question);

        llm.FailCoach = false;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.True(vm.IsFeedback);
        Assert.False(vm.IsFailed);
        Assert.Equal("an answer that must survive", vm.SubmittedAnswer);
    }

    [Fact]
    public async Task After_a_failure_Edit_my_answer_goes_back_to_the_box_with_the_text_still_there()
    {
        var vm = Started(out var llm, out _);
        llm.FailCoach = true;
        vm.AnswerText = "my first wording";
        await vm.SubmitCommand.ExecuteAsync(null);

        vm.EditAnswerCommand.Execute(null);

        Assert.True(vm.IsAnswering);
        Assert.Equal("my first wording", vm.AnswerText);

        llm.FailCoach = false;
        vm.AnswerText = "my better wording";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("my better wording", vm.SubmittedAnswer);
    }

    [Fact]
    public async Task An_unexpected_error_is_shown_instead_of_vanishing()
    {
        var vm = Started(out var llm, out _);
        llm.ThrowUnexpected = true;
        vm.AnswerText = "an answer";

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.IsFailed);
        Assert.Contains("something unexpected", vm.ErrorText);
    }

    // ---- other ways in and out

    [Fact]
    public void A_session_can_start_from_a_question_read_in_Learn_mode_with_no_model_call()
    {
        var vm = NewVm(out var llm, out _);
        var read = new LearnItem
        {
            Question = "What is a struct?", QuestionType = "technical_concept", Technology = "C#",
            Coach = new CoachOutput { ModelAnswer = "A struct is a value type." },
        };

        vm.BeginFrom(Request(QuestionType.TechnicalConcept), read);

        Assert.True(vm.IsAnswering);
        Assert.Equal("What is a struct?", vm.Question);
        Assert.Equal("C#", vm.Technology);
        Assert.Empty(llm.Calls);
        Assert.Null(vm.Coach);               // the model answer the user just read is not shown while they try
    }

    [Fact]
    public void Back_to_Home_asks_to_leave_and_stops_whatever_is_running()
    {
        var vm = Started(out _, out _);
        var raised = 0;
        vm.ExitRequested += () => raised++;

        vm.ExitCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    // ---- the screens

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 1400));
            view.Arrange(new Rect(0, 0, 1000, 1400));
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

    // The view is not in a window here, so IsVisible is always false; walk up and check each Visibility instead.
    private static bool Shown(UIElement e)
    {
        for (DependencyObject? node = e; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static List<string> Texts(DependencyObject root)
        => Descendants<TextBlock>(root).Where(Shown).Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : t.Text).ToList();

    [Fact]
    public void The_answer_box_screen_shows_the_question_the_box_the_counters_and_a_submit_button_and_binds_cleanly()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = Started(out _, out _);
            var view = new PracticeView { DataContext = vm };
            vm.AnswerText = "We cut p99";
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("PRACTICE", texts);
            Assert.Contains("Practice question 1?", texts);
            Assert.Contains("YOUR ANSWER", texts);
            Assert.Contains("3 words", texts);
            Assert.Contains("0:00", texts);
            Assert.Contains(Descendants<TextBox>(view), b => Shown(b) && b.Text == "We cut p99" && b.AcceptsReturn);
            Assert.Contains(Descendants<Button>(view), b => Shown(b) && Texts(b).Contains("Submit"));
            Assert.DoesNotContain("Try again", Descendants<Button>(view).Where(Shown).SelectMany(b => Texts(b)));   // nothing to try again yet
            Assert.DoesNotContain(texts, t => t.StartsWith("WHAT THEY"));                                              // no coaching yet
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_feedback_screen_shows_the_answer_the_landed_card_the_model_answer_and_the_actions()
    {
        var vm = Started(out _, out _);
        vm.AnswerText = "We cut p99 to 80ms by caching.";
        await vm.SubmitCommand.ExecuteAsync(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new PracticeView { DataContext = vm };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("We cut p99 to 80ms by caching.", texts);                                  // what you wrote
            Assert.Contains(texts, t => t.StartsWith("HOW YOUR ANSWER"));                                // the feedback card
            Assert.Contains(texts, t => t.Contains("You gave a number."));
            Assert.Contains(texts, t => t.StartsWith("A STRONG ANSWER"));                                // the model answer
            Assert.Contains("Click one to answer it.", texts);
            Assert.DoesNotContain(Descendants<TextBox>(view), Shown);                                  // the box is gone once it was sent
            var actions = Descendants<Button>(view).Where(Shown).SelectMany(b => Texts(b)).ToList();
            Assert.Contains("Try again", actions);
            Assert.Contains("Next question", actions);
            Assert.Contains("Back to Home", actions);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_failure_screen_keeps_the_answer_and_offers_retry_and_edit()
    {
        var vm = Started(out var llm, out _);
        llm.FailCoach = true;
        vm.AnswerText = "an answer that must survive";
        await vm.SubmitCommand.ExecuteAsync(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new PracticeView { DataContext = vm };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Something went wrong", texts);
            Assert.Contains("an answer that must survive", texts);
            Assert.Contains("Your answer is kept.", texts);
            var buttons = Descendants<Button>(view).Where(Shown).Select(b => b.Content as string).ToList();
            Assert.Contains("Retry", buttons);
            Assert.Contains("Edit my answer", buttons);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Ctrl_Enter_in_the_answer_box_submits()
    {
        WpfHost.Run(() =>
        {
            var vm = Started(out _, out _);
            var view = new PracticeView { DataContext = vm };
            Layout(view);

            var box = Descendants<TextBox>(view).Single(Shown);
            var binding = Assert.Single(box.InputBindings.OfType<System.Windows.Input.KeyBinding>());

            Assert.Equal(System.Windows.Input.Key.Enter, binding.Key);
            Assert.Equal(System.Windows.Input.ModifierKeys.Control, binding.Modifiers);
            Assert.Same(vm.SubmitCommand, binding.Command);
        });
    }

    // ---- Home: choosing Practice

    private static async Task<HomeViewModel> NewHomeAsync()
    {
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());
        await home.InitializeAsync();
        return home;
    }

    [Fact]
    public async Task Learn_is_chosen_to_begin_with_and_the_button_says_so()
    {
        var home = await NewHomeAsync();

        Assert.True(home.IsLearnMode);
        Assert.False(home.IsPracticeMode);
        Assert.Equal("Start Learn", home.StartLabel);
    }

    [Fact]
    public async Task Choosing_Practice_changes_the_button_and_Start_begins_a_Practice_session()
    {
        var home = await NewHomeAsync();
        LearnSessionRequest? practice = null, learn = null;
        home.PracticeRequested += r => practice = r;
        home.LearnRequested += r => learn = r;

        home.SelectPracticeCommand.Execute(null);
        Assert.True(home.IsPracticeMode);
        Assert.False(home.IsLearnMode);
        Assert.Equal("Start Practice", home.StartLabel);
        home.StartCommand.Execute(null);

        Assert.NotNull(practice);
        Assert.Null(learn);
        Assert.Equal("Acme backend", practice.Profile.Name);

        home.SelectLearnCommand.Execute(null);
        home.StartCommand.Execute(null);
        Assert.NotNull(learn);
    }

    [Fact]
    public async Task The_chosen_mode_is_announced_to_the_screen()
    {
        var home = await NewHomeAsync();
        var changed = new List<string?>();
        home.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        home.SelectPracticeCommand.Execute(null);

        Assert.Contains(nameof(HomeViewModel.IsPracticeMode), changed);
        Assert.Contains(nameof(HomeViewModel.IsLearnMode), changed);
        Assert.Contains(nameof(HomeViewModel.StartLabel), changed);
    }

    [Fact]
    public async Task The_Practice_card_is_usable_and_follows_the_mode_each_time_the_view_is_created()
    {
        var home = await NewHomeAsync();
        home.SelectPracticeCommand.Execute(null);

        WpfHost.Run(() =>
        {
            for (var visit = 1; visit <= 3; visit++)
            {
                var view = new HomeView { DataContext = home };
                Layout(view);

                var cards = Descendants<RadioButton>(view).Where(r => r.Template.FindName("Card", r) is Border).ToList();
                var selected = Assert.Single(cards, c => c.IsChecked == true);
                Assert.Contains("Practice", Texts(selected));
                Assert.True(selected.IsEnabled);
                Assert.NotNull(selected.Command);
                Assert.Contains("Start Practice", Descendants<Button>(view).SelectMany(b => Texts(b)));
            }
        });
    }

    // ---- getting there

    private static async Task<(MainViewModel Main, HomeViewModel Home, LearnViewModel Learn, PracticeViewModel Practice)> NewMainAsync()
    {
        var settings = new MemorySettings(new AppSettings());
        var llm = new FakeLlmService();
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank);
        await home.InitializeAsync();
        var learn = new LearnViewModel(llm, Prompts, settings, bank, () => 0.0);
        var practice = new PracticeViewModel(llm, Prompts, settings, bank, () => 0.0);
        var main = new MainViewModel(home, new SettingsViewModel(settings, llm, bank, new ScriptedDialogs()), learn, settings, null, practice);
        return (main, home, learn, practice);
    }

    [Fact]
    public async Task Start_in_Practice_mode_opens_the_Practice_screen_and_Back_returns_Home()
    {
        var (main, home, _, practice) = await NewMainAsync();
        home.SelectPracticeCommand.Execute(null);

        home.StartCommand.Execute(null);

        Assert.Same(practice, main.CurrentPage);
        Assert.True(practice.HasQuestion);
        Assert.True(main.IsHomeSelected);             // a session belongs to the Home flow

        practice.ExitCommand.Execute(null);
        Assert.Same(home, main.CurrentPage);
    }

    [Fact]
    public async Task Try_it_myself_in_Learn_opens_Practice_on_the_same_question()
    {
        var (main, home, learn, practice) = await NewMainAsync();
        Assert.False(learn.TryItMyselfCommand.CanExecute(null));    // nothing to try yet
        home.StartCommand.Execute(null);                            // Learn mode
        Assert.Same(learn, main.CurrentPage);
        Assert.True(learn.TryItMyselfCommand.CanExecute(null));
        var question = learn.Question;

        learn.TryItMyselfCommand.Execute(null);

        Assert.Same(practice, main.CurrentPage);
        Assert.Equal(question, practice.Question);
        Assert.True(practice.IsAnswering);
        Assert.Null(practice.Coach);
    }
}
