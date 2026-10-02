using System.IO;
using System.Windows;
using System.Windows.Controls;
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

public class BankUiTests
{
    private sealed class CapturingSettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public void Save(AppSettings settings) { Current = settings; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));
    private static readonly QuestionType[] TechnicalOnly = [QuestionType.TechnicalConcept];

    private static LearnViewModel NewLearn(out InMemoryTechBankRepository repo, bool reuse = true)
    {
        var llm = new FakeLlmService();
        repo = new InMemoryTechBankRepository();
        return new LearnViewModel(llm, Prompts, new CapturingSettings(new AppSettings { ReuseGeneralAnswers = reuse }),
            new TechBank(repo, llm, Prompts, () => 0.0), random: () => 0.0);
    }

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 800));
            view.Arrange(new Rect(0, 0, 1000, 800));
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

    // The views are not shown in a window here, so IsVisible is always false; look at the Visibility of the card instead.
    private static Visibility CardVisibility(DependencyObject root, Func<string, bool> textMatches)
    {
        var label = Descendants<TextBlock>(root).First(t => textMatches(t.Text));
        DependencyObject? node = label;
        while (node is not null and not Border { Style: not null }) node = VisualTreeHelper.GetParent(node);
        Assert.NotNull(node);
        return ((Border)node).Visibility;
    }

    private static CandidateProfile Profile() => Samples.CompleteProfile();

    // ---- Learn screen

    [Fact]
    public void A_technical_question_is_flagged_as_a_general_answer_with_its_technology()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearn(out _);

            vm.Begin(Profile(), TechnicalOnly);

            Assert.True(vm.IsGenericAnswer);
            Assert.True(vm.HasTechnology);
            Assert.Equal("C#", vm.Technology);
            Assert.Contains("general answer about C#", vm.GenericAnswerNote);
            Assert.True(vm.PersonalizeCommand.CanExecute(null));
        });
    }

    [Fact]
    public void The_view_shows_the_technology_chip_and_the_note_with_a_tailor_button_and_binds_cleanly()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = NewLearn(out _);
            var view = new LearnView { DataContext = vm };
            vm.Begin(Profile(), TechnicalOnly);
            Layout(view);

            var texts = Descendants<TextBlock>(view).Select(t => t.Text).ToList();
            Assert.Contains("C#", texts);                                   // the technology chip
            Assert.Contains(texts, t => t.StartsWith("This is a general answer about C#"));
            Assert.Contains(Descendants<Button>(view), b => b.Content as string == "Tailor to my resume");
            Assert.Equal(Visibility.Visible, CardVisibility(view, t => t.StartsWith("This is a general answer")));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Tailoring_replaces_the_general_answer_and_hides_the_note()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = NewLearn(out var repo);
            var view = new LearnView { DataContext = vm };
            vm.Begin(Profile(), TechnicalOnly);
            Layout(view);
            var before = repo.GetStatsAsync().Result;

            vm.PersonalizeCommand.Execute(null);
            Layout(view);

            Assert.False(vm.IsGenericAnswer);
            Assert.False(vm.PersonalizeCommand.CanExecute(null));
            Assert.Equal(Visibility.Collapsed, CardVisibility(view, t => t.StartsWith("This is a general answer")));
            Assert.Equal(before, repo.GetStatsAsync().Result); // the saved general answer is untouched
            Assert.True(vm.Coach is not null);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Questions_written_by_the_model_have_no_note_and_no_tailor_button()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearn(out var repo);
            var view = new LearnView { DataContext = vm };

            vm.Begin(Profile(), [QuestionType.Behavioral]);
            Layout(view);

            Assert.False(vm.IsGenericAnswer);
            Assert.False(vm.HasTechnology);
            Assert.Equal(Visibility.Collapsed, CardVisibility(view, t => t.StartsWith("This is a general answer")));
            Assert.Equal(new TechBankStats(0, 0), repo.GetStatsAsync().Result);
        });
    }

    [Fact]
    public void Turning_the_setting_off_means_every_question_is_written_from_the_resume()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearn(out var repo, reuse: false);

            vm.Begin(Profile(), TechnicalOnly);

            Assert.False(vm.IsGenericAnswer);
            Assert.Equal(new TechBankStats(0, 0), repo.GetStatsAsync().Result);
        });
    }

    [Fact]
    public void A_follow_up_to_a_general_answer_keeps_the_note_and_the_technology()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearn(out _);
            vm.Begin(Profile(), TechnicalOnly);

            vm.Coach!.FollowUps[0].OpenCommand.Execute(null);

            Assert.True(vm.IsFollowUp);
            Assert.True(vm.IsGenericAnswer);
            Assert.Equal("C#", vm.Technology);
        });
    }

    // ---- Home: By technology

    private static HomeViewModel NewHome(bool withBank, out InMemoryProfileRepository repo)
    {
        repo = new InMemoryProfileRepository();
        repo.SaveAsync(Samples.CompleteProfile()).GetAwaiter().GetResult();
        var llm = new FakeLlmService();   // the demo model finds C#, .NET and SQL Server in any job description
        var bank = withBank ? new TechBank(new InMemoryTechBankRepository(), llm, Prompts) : null;
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void The_home_view_offers_By_technology_and_lists_the_technologies_when_ticked()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = NewHome(withBank: true, out _);
            var view = new HomeView { DataContext = vm };
            Layout(view);

            var option = Descendants<CheckBox>(view).Single(c => c.Content as string == "By technology…");
            Assert.Equal(Visibility.Visible, option.Visibility);
            Assert.Equal(Visibility.Collapsed, CardVisibility(view, t => t == "TECHNOLOGIES IN THIS JOB DESCRIPTION")); // nothing yet

            vm.TechnologyMode = true;
            Layout(view);

            Assert.Equal(Visibility.Visible, CardVisibility(view, t => t == "TECHNOLOGIES IN THIS JOB DESCRIPTION"));
            var names = Descendants<CheckBox>(view).Select(c => c.Content as string).ToList();
            Assert.Contains("C#", names);
            Assert.Contains(".NET", names);
            Assert.Contains("SQL Server", names);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Ticking_a_technology_box_in_the_view_selects_it_and_enables_start()
    {
        WpfHost.Run(() =>
        {
            var vm = NewHome(withBank: true, out _);
            var view = new HomeView { DataContext = vm };
            vm.TechnologyMode = true;
            Layout(view);
            Assert.False(vm.CanStart);

            var box = Descendants<CheckBox>(view).Single(c => c.Content as string == ".NET");
            box.IsChecked = true;   // what a click does

            Assert.Equal([".NET"], vm.SelectedTechnologies);
            Assert.True(vm.CanStart);
        });
    }

    [Fact]
    public void Without_the_bank_the_option_is_hidden()
    {
        WpfHost.Run(() =>
        {
            var vm = NewHome(withBank: false, out _);
            var view = new HomeView { DataContext = vm };
            Layout(view);

            var option = Descendants<CheckBox>(view).Single(c => c.Content as string == "By technology…");

            Assert.Equal(Visibility.Collapsed, option.Visibility);
        });
    }

    // ---- Settings

    private static SettingsViewModel NewSettings(out CapturingSettings store, out InMemoryTechBankRepository repo, out ScriptedDialogs dialogs, bool withBank = true)
    {
        store = new CapturingSettings(new AppSettings());
        repo = new InMemoryTechBankRepository();
        dialogs = new ScriptedDialogs();
        var llm = new FakeLlmService();
        return new SettingsViewModel(store, llm, withBank ? new TechBank(repo, llm, Prompts) : null, dialogs);
    }

    [Fact]
    public async Task Settings_shows_how_much_is_saved()
    {
        var vm = NewSettings(out _, out var repo, out _);
        Assert.Contains("Nothing saved yet", vm.BankStats);

        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "Q1?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "Q2?", "f");
        await repo.SaveAnswerAsync(question.Id, 0, new CoachOutput { ModelAnswer = "a" });
        await vm.RefreshBankStatsAsync();

        Assert.Equal("2 technical questions and 1 general answer saved.", vm.BankStats);
    }

    [Fact]
    public async Task Clearing_asks_first_and_does_nothing_when_declined()
    {
        var vm = NewSettings(out _, out var repo, out var dialogs);
        await repo.AddQuestionAsync("C#", Seniority.Senior, "Q?", "f");
        dialogs.ConfirmAnswer = false;

        await vm.ClearBankCommand.ExecuteAsync(null);

        Assert.Equal(1, (await repo.GetStatsAsync()).Questions);
        Assert.Single(dialogs.Confirmations);
    }

    [Fact]
    public async Task Clearing_removes_everything_when_confirmed_and_updates_the_line()
    {
        var vm = NewSettings(out _, out var repo, out var dialogs);
        var q = await repo.AddQuestionAsync("C#", Seniority.Senior, "Q?", "f");
        await repo.SaveAnswerAsync(q.Id, 0, new CoachOutput { ModelAnswer = "a" });
        dialogs.ConfirmAnswer = true;

        await vm.ClearBankCommand.ExecuteAsync(null);

        Assert.Equal(new TechBankStats(0, 0), await repo.GetStatsAsync());
        Assert.Contains("Nothing saved yet", vm.BankStats);
        Assert.Contains("cleared", vm.StatusMessage);
    }

    [Fact]
    public void The_reuse_setting_and_prompt_caching_are_saved_and_default_to_on()
    {
        var vm = NewSettings(out var store, out _, out _);
        Assert.True(vm.ReuseGeneralAnswers);
        Assert.True(vm.PromptCaching);

        vm.ReuseGeneralAnswers = false;
        vm.PromptCaching = false;
        vm.SaveCommand.Execute(null);

        Assert.False(store.Current.ReuseGeneralAnswers);
        Assert.False(store.Current.PromptCaching);
    }

    [Fact]
    public void Without_a_bank_the_settings_section_is_hidden_and_clearing_is_a_no_op()
    {
        var vm = NewSettings(out _, out _, out _, withBank: false);

        Assert.False(vm.HasBank);
        vm.ClearBankCommand.Execute(null);
    }

    [Fact]
    public void The_settings_view_binds_cleanly_with_the_new_options()
    {
        WpfHost.TakeBindingErrors();

        var text = WpfHost.Run(() =>
        {
            var vm = NewSettings(out _, out _, out _);
            var view = new SettingsView { DataContext = vm };
            Layout(view);
            return string.Join("\n", Descendants<TextBlock>(view).Select(t => t.Text).Concat(Descendants<CheckBox>(view).Select(c => c.Content as string ?? "")));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
        Assert.Contains("Saved technical questions", text);
        Assert.Contains("Reuse saved general answers", text);
        Assert.Contains("Use prompt caching", text);
    }
}
