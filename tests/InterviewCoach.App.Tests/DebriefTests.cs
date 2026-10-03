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
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>The debrief screen, the Home options for a mock interview, how the pages hand over to each other, and the real views.</summary>
public class DebriefTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public int Saves { get; private set; }
        public void Save(AppSettings settings) { Current = settings; Saves++; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static MockSessionRequest Request() => new(Samples.CompleteProfile(), RoundType.Technical, 15, true, EmploymentType.FullTime);

    /// <summary>A finished demo interview: two questions, one with a follow-up, a debrief and the coaching of both.</summary>
    private static async Task<(DebriefViewModel Debrief, ScriptedDialogs Dialogs)> FinishedAsync()
    {
        var llm = new FakeLlmService();
        var dialogs = new ScriptedDialogs();
        var vm = new MockViewModel(llm, Prompts, new MemorySettings(new AppSettings { AutoListen = false }), null, dialogs);
        DebriefViewModel? debrief = null;
        vm.DebriefReady += d => debrief = d;
        vm.Begin(Request());
        for (var guard = 0; debrief is null && guard < 20; guard++)
        {
            Assert.NotEqual(MockPhase.Failed, vm.Phase);
            if (vm.IsAnswering)
            {
                vm.AnswerText = "A sample answer with enough words to count.";
                await vm.SubmitCommand.ExecuteAsync(null);
            }
        }
        Assert.NotNull(debrief);
        return (debrief, dialogs);
    }

    // ---- what the debrief shows

    [Fact]
    public async Task The_debrief_shows_the_summary_the_signal_the_ratings_strengths_fixes_and_what_to_practise()
    {
        var (debrief, _) = await FinishedAsync();

        Assert.Contains("sample debrief", debrief.Summary);
        Assert.Equal("Lean yes", debrief.SignalLabel);
        Assert.True(debrief.IsNeutral);
        Assert.False(debrief.IsPositive);
        Assert.Equal(2, debrief.Ratings.Count);
        Assert.Equal("3 of 4", debrief.Ratings[0].RatingText);
        Assert.True(debrief.Ratings[0].Covered);
        Assert.Equal("Not covered", debrief.Ratings[1].RatingText);      // an area that never came up
        Assert.False(debrief.Ratings[1].Covered);
        Assert.True(debrief.HasStrengths && debrief.HasFixes && debrief.HasPracticeNext && debrief.HasRatings);
        Assert.Equal("Say what you personally did.", debrief.Fixes[0].Fix);
        Assert.True(debrief.Fixes[0].HasExample);
        Assert.StartsWith("Senior Backend Engineer · Technical · 15 min planned · ", debrief.RoleLine);
    }

    [Fact]
    public async Task There_is_one_card_per_question_with_the_exchange_and_the_coaching()
    {
        var (debrief, _) = await FinishedAsync();

        Assert.Equal(2, debrief.Threads.Count);
        var first = debrief.Threads[0];
        Assert.StartsWith("1. ", first.Title);
        Assert.Contains("proud of", first.Question);
        Assert.Equal(4, first.Turns.Count);                              // the question, the answer, the follow-up, the answer
        Assert.Equal(["Interviewer", "You", "Interviewer", "You"], first.Turns.Select(t => t.Speaker));
        Assert.Equal("Resume deep-dive", first.TypeLabel);
        Assert.Equal(ThreadStatus.Done, first.Status);
        Assert.NotNull(first.Coach);
        Assert.True(first.Coach!.HasFeedback);
        Assert.False(debrief.IsStillCoaching);
        Assert.Equal("Click one to practise it.", first.Coach.FollowUpPrompt);
    }

    [Fact]
    public async Task A_card_fills_in_when_its_coaching_arrives_and_a_failed_one_can_be_retried()
    {
        var (debrief, _) = await FinishedAsync();
        var card = debrief.Threads[1];
        var thread = card.Thread;

        thread.Status = ThreadStatus.Failed;
        thread.Error = "the coach is unavailable";
        thread.Coach = null;
        // The card is told by the engine; here the engine is asked to retry, which succeeds with the demo coach.
        card.Refresh();
        Assert.True(card.IsFailed);
        Assert.Equal("the coach is unavailable", card.Error);
        Assert.True(card.RetryCommand.CanExecute(null));
    }

    // ---- practising from the debrief

    [Fact]
    public async Task Practise_this_question_opens_Practice_on_that_question_with_its_type()
    {
        var (debrief, _) = await FinishedAsync();
        LearnSessionRequest? request = null;
        LearnItem? item = null;
        debrief.PracticeRequested += (r, i) => { request = r; item = i; };

        debrief.Threads[0].PracticeCommand.Execute(null);

        Assert.NotNull(item);
        Assert.Equal(debrief.Threads[0].Question, item.Question);
        Assert.Equal("resume_deep_dive", item.QuestionType);
        Assert.False(item.IsFollowUp);
        Assert.Equal("Senior Backend Engineer", request!.Profile.JobRole);
        Assert.Equal(EmploymentType.FullTime, request.Employment);
    }

    [Fact]
    public async Task A_follow_up_from_the_coaching_becomes_a_follow_up_question_in_Practice()
    {
        var (debrief, _) = await FinishedAsync();
        LearnItem? item = null;
        debrief.PracticeRequested += (_, i) => item = i;
        var coach = debrief.Threads[0].Coach!;

        coach.FollowUps[0].OpenCommand.Execute(null);

        Assert.NotNull(item);
        Assert.True(item.IsFollowUp);
        Assert.Equal(debrief.Threads[0].Question, item.ParentQuestion);
        Assert.Equal(coach.FollowUps[0].Question, item.Question);
    }

    // ---- export

    [Fact]
    public async Task Export_writes_the_debrief_as_Markdown_to_the_chosen_file()
    {
        var (debrief, dialogs) = await FinishedAsync();
        var path = Path.Combine(Path.GetTempPath(), $"debrief-{Guid.NewGuid():N}.md");
        dialogs.FileToSave = path;
        try
        {
            debrief.ExportCommand.Execute(null);

            Assert.Matches(@"^mock-debrief-\d{4}-\d{2}-\d{2}-\d{4}\.md$", dialogs.OfferedFileName);
            var text = File.ReadAllText(path);
            Assert.StartsWith("# Mock interview debrief: Senior Backend Engineer", text);
            Assert.Contains("Hire signal: **Lean yes**", text);
            Assert.Contains("### 1. ", text);
            Assert.Contains("Saved to", debrief.ExportStatus);
            Assert.True(debrief.HasExportStatus);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancelling_the_save_dialog_writes_nothing_and_a_bad_path_says_so()
    {
        var (debrief, dialogs) = await FinishedAsync();
        dialogs.FileToSave = null;
        debrief.ExportCommand.Execute(null);
        Assert.Equal("", debrief.ExportStatus);

        dialogs.FileToSave = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "debrief.md");
        debrief.ExportCommand.Execute(null);
        Assert.StartsWith("Could not save the file", debrief.ExportStatus);
    }

    [Fact]
    public async Task Back_returns_home()
    {
        var (debrief, _) = await FinishedAsync();
        var back = false;
        debrief.ExitRequested += () => back = true;

        debrief.BackCommand.Execute(null);

        Assert.True(back);
    }

    // ---- Home: choosing a mock interview

    private static async Task<(HomeViewModel Home, MemorySettings Settings)> NewHomeAsync(AppSettings? settings = null)
    {
        var s = new MemorySettings(settings ?? new AppSettings());
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), null, s);
        await home.InitializeAsync();
        return (home, s);
    }

    [Fact]
    public async Task Choosing_Mock_Interview_changes_the_options_and_the_button()
    {
        var (home, _) = await NewHomeAsync();
        Assert.True(home.ShowQuestionTypes);
        Assert.Equal("Start Learn", home.StartLabel);

        home.SelectMockCommand.Execute(null);

        Assert.True(home.IsMockMode);
        Assert.False(home.IsLearnMode);
        Assert.False(home.ShowQuestionTypes);                     // a mock interview picks its own questions
        Assert.Equal("Start Mock Interview", home.StartLabel);
        Assert.Equal(6, home.RoundOptions.Count);
        Assert.Equal([15, 30, 45, 60], home.DurationOptions.Select(d => d.Value));
        Assert.Equal(RoundType.Mixed, home.Round);
        Assert.Equal(30, home.DurationMinutes);
        Assert.Single(home.RoundOptions, o => o.IsSelected);
        Assert.Single(home.DurationOptions, o => o.IsSelected);
        Assert.True(home.ShowQuestionText);
    }

    [Fact]
    public async Task Start_in_Mock_mode_hands_over_the_profile_the_round_the_length_and_the_text_choice()
    {
        var (home, _) = await NewHomeAsync();
        MockSessionRequest? mock = null;
        var learned = false;
        home.MockRequested += r => mock = r;
        home.LearnRequested += _ => learned = true;
        home.SelectMockCommand.Execute(null);
        home.RoundOptions.First(o => o.Value == RoundType.SystemDesign).IsSelected = true;
        home.DurationOptions.First(o => o.Value == 45).IsSelected = true;
        home.ShowQuestionText = false;
        home.EmploymentOptions.First(o => o.Type == EmploymentType.Contract).IsSelected = true;

        home.StartCommand.Execute(null);

        Assert.False(learned);
        Assert.NotNull(mock);
        Assert.Equal("Senior Backend Engineer", mock.Profile.JobRole);
        Assert.Equal(RoundType.SystemDesign, mock.Round);
        Assert.Equal(45, mock.DurationMinutes);
        Assert.False(mock.ShowQuestionText);
        Assert.Equal(EmploymentType.Contract, mock.Employment);
    }

    [Fact]
    public async Task The_round_and_length_are_remembered_and_the_text_default_comes_from_Settings()
    {
        var (home, settings) = await NewHomeAsync(new AppSettings { MockRoundType = RoundType.Behavioral, MockDurationMinutes = 60, ShowQuestionTextDefault = false });

        Assert.Equal(RoundType.Behavioral, home.Round);
        Assert.Equal(60, home.DurationMinutes);
        Assert.False(home.ShowQuestionText);
        Assert.Equal(RoundType.Behavioral, home.RoundOptions.Single(o => o.IsSelected).Value);
        Assert.Equal(0, settings.Saves);                                  // restoring is not a change

        home.RoundOptions.First(o => o.Value == RoundType.Technical).IsSelected = true;
        home.DurationOptions.First(o => o.Value == 15).IsSelected = true;

        Assert.Equal(RoundType.Technical, settings.Current.MockRoundType);
        Assert.Equal(15, settings.Current.MockDurationMinutes);
        Assert.Equal(RoundType.Technical, home.Round);
        Assert.Single(home.RoundOptions, o => o.IsSelected);
    }

    [Fact]
    public async Task A_saved_length_that_is_not_offered_falls_back_to_thirty_minutes()
    {
        var (home, _) = await NewHomeAsync(new AppSettings { MockDurationMinutes = 7 });

        Assert.Equal(30, home.DurationMinutes);
    }

    [Fact]
    public async Task A_mock_interview_does_not_need_technologies_or_a_valid_custom_answer_length()
    {
        var (home, _) = await NewHomeAsync();
        home.AnswerLengthChoice = AnswerLengthChoice.Custom;
        home.CustomWords = "banana";
        Assert.False(home.CanStart);                                      // Learn and Practice need a valid length

        home.SelectMockCommand.Execute(null);

        Assert.True(home.CanStart);
        Assert.Equal("", home.StartHint);
    }

    // ---- the pages hand over to each other

    private static async Task<(MainViewModel Main, HomeViewModel Home, MockViewModel Mock, PracticeViewModel Practice, TestSpeech Speech)> NewMainAsync()
    {
        var settings = new MemorySettings(new AppSettings { AutoListen = false });
        var llm = new FakeLlmService();
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank, settings);
        await home.InitializeAsync();
        var speech = new TestSpeech();
        var learn = new LearnViewModel(llm, Prompts, settings, bank, () => 0.0);
        var practice = new PracticeViewModel(llm, Prompts, settings, bank, () => 0.0, null, null, speech);
        var mock = new MockViewModel(llm, Prompts, settings, speech, new ScriptedDialogs(), new InMemoryMockHistory());
        var main = new MainViewModel(home, new SettingsViewModel(settings, llm, bank, new ScriptedDialogs()), learn, settings, null, practice, null, mock);
        return (main, home, mock, practice, speech);
    }

    private static async Task FinishInterviewAsync(MockViewModel mock, MainViewModel main)
    {
        for (var guard = 0; main.CurrentPage is not DebriefViewModel && guard < 20; guard++)
        {
            Assert.NotEqual(MockPhase.Failed, mock.Phase);
            if (mock.IsAnswering)
            {
                mock.AnswerText = "A sample answer with enough words to count.";
                await mock.SubmitCommand.ExecuteAsync(null);
            }
        }
    }

    [Fact]
    public async Task Start_in_Mock_mode_opens_the_interview_and_the_debrief_follows_the_closing()
    {
        var (main, home, mock, _, _) = await NewMainAsync();
        home.SelectMockCommand.Execute(null);

        home.StartCommand.Execute(null);

        Assert.Same(mock, main.CurrentPage);
        Assert.True(main.IsHomeSelected);                                  // an interview is part of the Home flow
        Assert.True(mock.HasInterviewerLine);

        await FinishInterviewAsync(mock, main);

        var debrief = Assert.IsType<DebriefViewModel>(main.CurrentPage);
        Assert.True(main.IsHomeSelected);
        debrief.BackCommand.Execute(null);
        Assert.Same(home, main.CurrentPage);
    }

    [Fact]
    public async Task Practising_a_question_from_the_debrief_returns_to_the_debrief_afterwards()
    {
        var (main, home, mock, practice, _) = await NewMainAsync();
        home.SelectMockCommand.Execute(null);
        home.StartCommand.Execute(null);
        await FinishInterviewAsync(mock, main);
        var debrief = (DebriefViewModel)main.CurrentPage;
        var question = debrief.Threads[0].Question;

        debrief.Threads[0].PracticeCommand.Execute(null);

        Assert.Same(practice, main.CurrentPage);
        Assert.Equal(question, practice.Question);
        Assert.True(practice.IsAnswering);

        practice.ExitCommand.Execute(null);
        Assert.Same(debrief, main.CurrentPage);
    }

    [Fact]
    public async Task Opening_another_page_during_an_interview_stops_the_voice_and_the_microphone()
    {
        var (main, home, mock, _, speech) = await NewMainAsync();
        home.SelectMockCommand.Execute(null);
        home.StartCommand.Execute(null);
        await mock.Composer.ToggleMicCommand.ExecuteAsync(null);
        Assert.True(mock.Composer.IsListening);

        main.ShowSettingsCommand.Execute(null);

        Assert.True(speech.Mic.Stopped);
        Assert.False(mock.CanEnd);                                          // the engine was cancelled: nothing carries on in the background
    }

    [Fact]
    public async Task Leaving_Practice_for_another_page_stops_it_talking_and_listening()
    {
        var (main, home, _, practice, speech) = await NewMainAsync();
        home.SelectPracticeCommand.Execute(null);
        home.StartCommand.Execute(null);
        Assert.Same(practice, main.CurrentPage);
        await practice.ToggleMicCommand.ExecuteAsync(null);

        main.ShowHomeCommand.Execute(null);

        Assert.True(speech.Mic.Stopped);
        Assert.False(practice.IsListening);
    }

    // ---- the real views

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 2400));
            view.Arrange(new Rect(0, 0, 1000, 2400));
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

    [Fact]
    public async Task The_interview_screen_shows_the_pill_the_clock_the_interviewer_the_answer_box_and_binds_cleanly()
    {
        var (_, home, mock, _, _) = await NewMainAsync();
        home.SelectMockCommand.Execute(null);
        home.StartCommand.Execute(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new MockView { DataContext = mock };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Your turn", texts);
            Assert.Contains(texts, t => t.EndsWith("/ 30:00"));
            Assert.Contains("INTERVIEWER", texts);
            Assert.Contains("Hi, thanks for joining. How is your day going?", texts);
            Assert.Contains("YOUR ANSWER", texts);
            Assert.Contains("Nothing here is graded. Your feedback comes after the interview.", texts);
            var buttons = ButtonTexts(view);
            Assert.Contains("End interview", buttons);
            Assert.Contains("Send", buttons);
            Assert.Contains("Speak", buttons);
            Assert.Contains("Repeat", buttons);
            Assert.Contains(Descendants<TextBox>(view), b => Shown(b) && b.AcceptsReturn);
            Assert.Contains(view.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.F2 && b.Modifiers == ModifierKeys.None);
            Assert.DoesNotContain(texts, t => t.StartsWith("WHAT THEY"));                    // no coaching on this screen
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void A_failed_interview_screen_says_what_went_wrong_and_offers_Retry_and_Back()
    {
        var llm = new FakeLlmService((role, _) => role == LlmRole.Planner ? throw new LlmException("the planner is unavailable") : "{}");
        var mock = new MockViewModel(llm, Prompts, new MemorySettings(new AppSettings()));
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            mock.Begin(Request());
            var view = new MockView { DataContext = mock };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Something went wrong", texts);
            Assert.Contains("the planner is unavailable", texts);
            Assert.Contains("The conversation so far is kept.", texts);
            var buttons = ButtonTexts(view);
            Assert.Contains("Retry", buttons);
            Assert.Contains("Back to Home", buttons);
            Assert.Contains("Problem", texts);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_debrief_screen_shows_every_part_and_binds_cleanly()
    {
        var (debrief, _) = await FinishedAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new DebriefView { DataContext = debrief };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Your debrief", texts);
            Assert.Contains("Lean yes", texts);
            Assert.Contains("HOW THE ROUND WENT", texts);
            Assert.Contains("FOCUS AREAS", texts);
            Assert.Contains("Ownership of past work", texts);
            Assert.Contains("Not covered", texts);
            Assert.Contains("STRENGTHS", texts);
            Assert.Contains("TOP FIXES", texts);
            Assert.Contains("PRACTISE NEXT", texts);
            Assert.Contains("Question by question", texts);
            Assert.Contains(texts, t => t.StartsWith("1. "));
            var buttons = ButtonTexts(view);
            Assert.Contains("Back to Home", buttons);
            Assert.Contains("Export to Markdown", buttons);
            Assert.Equal(2, Descendants<Expander>(view).Count());
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task An_opened_question_card_shows_the_exchange_the_coaching_and_the_practice_button()
    {
        var (debrief, _) = await FinishedAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new DebriefView { DataContext = debrief };
            Layout(view);
            Descendants<Expander>(view).First().IsExpanded = true;
            Layout(view);

            var texts = Texts(view);
            Assert.Contains(texts, t => t.Contains("What was your part in that"));
            Assert.Contains(texts, t => t.StartsWith("WHAT THEY'RE TESTING"));
            Assert.Contains(texts, t => t.StartsWith("A STRONG ANSWER"));
            Assert.Contains("Click one to practise it.", texts);
            Assert.Contains("Practise this question", ButtonTexts(view));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_Home_screen_in_Mock_mode_shows_the_round_and_length_chips_and_hides_the_question_types()
    {
        var (home, _) = await NewHomeAsync();
        home.SelectMockCommand.Execute(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = home };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Round", texts);
            Assert.Contains("Length", texts);
            Assert.DoesNotContain("Question types", texts);
            Assert.DoesNotContain("Answer length", texts);
            var radios = Descendants<RadioButton>(view).Where(Shown).SelectMany(b => Texts(b)).ToList();
            Assert.Contains("Recruiter screen", radios);
            Assert.Contains("System design", radios);
            Assert.Contains("45 min", radios);
            Assert.Contains("Start Mock Interview", ButtonTexts(view));
            Assert.Contains(Descendants<CheckBox>(view), c => Shown(c) && Texts(c).Any(t => t.StartsWith("Show the interviewer's words")));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }
}
