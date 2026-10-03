using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.Controls;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>
/// Loads the real views with real view models and the real App styles. Catches XAML that does not parse,
/// missing resources, and data bindings that point at properties that do not exist or cannot work.
/// </summary>
public class ViewSmokeTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    // Items in an ItemsControl are generated lazily, a little after the first layout pass. A real window would get
    // there on the next idle tick of its dispatcher, so do the same here before looking at the visual tree.
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

    private static IEnumerable<string> AllText(DependencyObject root) =>
        Descendants<TextBlock>(root).Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : t.Text);

    [Fact]
    public async Task Home_view_loads_with_an_open_profile_and_has_no_binding_errors()
    {
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        HomeViewModel? vm = null;
        WpfHost.Run(() => vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs()));
        await vm!.InitializeAsync();
        WpfHost.TakeBindingErrors();

        var text = WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = vm };
            Layout(view);
            return string.Join("\n", AllText(view));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
        Assert.Contains("Start a session", text);
        Assert.Contains("Learn", text);
        Assert.Contains("Behavioral", text);
        Assert.Contains("Acme backend", text);
    }

    [Fact]
    public async Task Home_view_loads_with_no_profiles()
    {
        var vm = new HomeViewModel(new InMemoryProfileRepository(), new StubExtractor(), new ScriptedDialogs());
        await vm.InitializeAsync();
        WpfHost.TakeBindingErrors();

        var text = WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = vm };
            Layout(view);
            return string.Join("\n", AllText(view));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
        Assert.Contains("Create a profile to get started", text);
    }

    [Fact]
    public async Task The_Learn_card_is_shown_as_selected_every_time_the_home_view_is_created()
    {
        // Leaving Home and coming back creates a new HomeView. The card used to depend on radio-group state shared between
        // instances and came back unselected.
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());
        await vm.InitializeAsync();

        WpfHost.Run(() =>
        {
            for (var visit = 1; visit <= 3; visit++)
            {
                var view = new HomeView { DataContext = vm };
                Layout(view);

                // Only the four mode cards, not the Role type chips, which are radio buttons too.
                var cards = Descendants<RadioButton>(view).Where(r => r.Template.FindName("Card", r) is Border).ToList();
                var selected = Assert.Single(cards, c => c.IsChecked == true);
                Assert.True(selected.IsEnabled, $"visit {visit}: the selected card must be the usable one");
                Assert.Equal(2, ((Border)selected.Template.FindName("Card", selected)).BorderThickness.Left); // drawn as selected
                Assert.Equal(Visibility.Visible, ((Border)selected.Template.FindName("Check", selected)).Visibility);
                Assert.Equal(4, cards.Count);
                Assert.All(cards, c => Assert.True(c.IsEnabled)); // Mock Interview, Learn, Practice and Revisit are all available
            }
        });
    }

    [Fact]
    public async Task The_job_description_and_resume_boxes_use_the_theme_style_so_they_follow_dark_mode()
    {
        // A style that does not inherit the theme's TextBox style falls back to the classic white box in dark mode.
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());
        await vm.InitializeAsync();

        WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = vm };
            Layout(view);

            var multiline = Descendants<TextBox>(view).Where(t => t.AcceptsReturn).ToList();
            Assert.Equal(2, multiline.Count);
            var themed = (Style)Application.Current.FindResource(typeof(TextBox));
            Assert.All(multiline, box => Assert.Same(themed, box.Style.BasedOn));
        });
    }

    [Fact]
    public void Settings_view_loads_and_has_no_binding_errors()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = new SettingsViewModel(new MemorySettings(new AppSettings()), new FakeLlmService());
            var view = new SettingsView { DataContext = vm };
            Layout(view);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Password_box_passes_typed_text_to_the_view_model()
    {
        // Regression test: the first version never attached its PasswordChanged handler, so typed keys were ignored.
        WpfHost.Run(() =>
        {
            var vm = new SettingsViewModel(new MemorySettings(new AppSettings()), new FakeLlmService());
            var view = new SettingsView { DataContext = vm };
            Layout(view);

            var box = Descendants<PasswordBox>(view).First();
            box.Password = "sk-ant-typed";

            Assert.Equal("sk-ant-typed", vm.AnthropicApiKey);
        });
    }

    // Without a bank, every question is written by the (demo) model, so the cards below are the ordinary tailored ones.
    private static LearnViewModel NewLearnVm() => new(
        new FakeLlmService(), new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), new MemorySettings(new AppSettings()));

    private static LearnViewModel NewLearnVmWithBank(out InMemoryTechBankRepository repo, bool reuse = true)
    {
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        repo = new InMemoryTechBankRepository();
        return new LearnViewModel(llm, prompts, new MemorySettings(new AppSettings { ReuseGeneralAnswers = reuse }), new TechBank(repo, llm, prompts, () => 0.0), () => 0.0);
    }

    [Fact]
    public void Learn_view_shows_the_question_and_every_coach_card()
    {
        WpfHost.TakeBindingErrors();

        var (text, highlighted) = WpfHost.Run(() =>
        {
            var vm = NewLearnVm();
            var view = new LearnView { DataContext = vm };
            vm.Begin(Samples.CompleteProfile(), []);
            Layout(view);

            var runs = Descendants<HighlightedTextBlock>(view).SelectMany(t => t.Inlines.OfType<Run>()).Where(r => r.Background is not null);
            return (string.Join("\n", AllText(view)), runs.Select(r => r.Text).ToList());
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
        Assert.Contains("Can you tell me a bit about yourself", text);       // the question
        Assert.Contains("WHAT THEY'RE TESTING", text);
        Assert.Contains("A STRONG ANSWER, SAID OUT LOUD", text);
        Assert.Contains("words, about", text);                               // word count and spoken time under the answer
        Assert.Contains("Interviewers typically expect", text);               // the demo question has a known type
        Assert.Contains("THE SHAPE OF IT", text);
        Assert.Contains("WHERE THEY'LL GO NEXT", text);
        Assert.Contains("Why that approach over the alternatives?", text);   // a follow-up
        Assert.Contains("[Company]", highlighted);                           // placeholders are highlighted
        Assert.Contains("[your actual before/after metric]", highlighted);
    }

    [Fact]
    public void Learn_view_hides_the_feedback_and_delivery_cards_in_learn_mode()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearnVm();
            var view = new LearnView { DataContext = vm };
            vm.Begin(Samples.CompleteProfile(), []);
            Layout(view);

            var titles = Descendants<TextBlock>(view)
                .Where(t => t.Text is "HOW YOUR ANSWER LANDED" or "DELIVERY")
                .ToList();

            Assert.All(titles, t => Assert.False(t.IsVisible));
        });
    }

    [Fact]
    public void Clicking_a_follow_up_shows_its_coaching_and_a_way_back()
    {
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var vm = NewLearnVm();
            var view = new LearnView { DataContext = vm };
            vm.Begin(Samples.CompleteProfile(), []);
            Layout(view);
            var mainQuestion = vm.Question;

            var label = Descendants<TextBlock>(view).FirstOrDefault(t => t.Text == "How did you know it worked?");
            Assert.True(label is not null, "follow-up text not found. Texts: " + string.Join(" | ", AllText(view)));
            DependencyObject? node = label;
            while (node is not null and not Button) node = VisualTreeHelper.GetParent(node);
            ((Button)node!).Command.Execute(null);
            Layout(view);

            Assert.True(vm.IsFollowUp);
            Assert.Equal("How did you know it worked?", vm.Question);
            Assert.True(vm.CanGoBack);
            Assert.Contains("Follow-up to: " + mainQuestion, string.Join("\n", AllText(view)));

            vm.BackCommand.Execute(null);
            Layout(view);
            Assert.Equal(mainQuestion, vm.Question);
            Assert.False(vm.CanGoBack);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Next_question_keeps_producing_different_questions()
    {
        WpfHost.Run(() =>
        {
            var vm = NewLearnVm();
            vm.Begin(Samples.CompleteProfile(), []);
            var seen = new List<string> { vm.Question };

            for (var i = 0; i < 5; i++)
            {
                vm.NextCommand.Execute(null);
                seen.Add(vm.Question);
            }

            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.All(seen, q => Assert.False(string.IsNullOrWhiteSpace(q)));
        });
    }
}
