using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

/// <summary>The bar above the page (a missing key, a problem that was reported) and the keyboard shortcuts.</summary>
public class NoticeAndShortcutTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public void Save(AppSettings settings) { Current = settings; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static readonly string[] KeyVariables = ["ANTHROPIC_API_KEY", "OPENAI_API_KEY", "OPENROUTER_API_KEY"];

    /// <summary>Runs with no key in the environment, so what a developer has set on this machine does not decide the result.</summary>
    private static async Task WithoutEnvironmentKeys(Func<Task> test)
    {
        var saved = KeyVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in KeyVariables) Environment.SetEnvironmentVariable(v, null);
            await test();
        }
        finally
        {
            foreach (var (v, value) in saved) Environment.SetEnvironmentVariable(v, value);
        }
    }

    private static async Task<(MainViewModel Main, MemorySettings Settings, HomeViewModel Home, LearnViewModel Learn, PracticeViewModel Practice, MockViewModel Mock)> NewMainAsync(AppSettings settings)
    {
        var s = new MemorySettings(settings);
        var llm = new FakeLlmService();
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var dialogs = new ScriptedDialogs();
        var home = new HomeViewModel(repo, new StubExtractor(), dialogs, bank, s, new InMemoryMockHistory());
        await home.InitializeAsync();
        var learn = new LearnViewModel(llm, Prompts, s, bank, () => 0.0);
        var practice = new PracticeViewModel(llm, Prompts, s, bank, () => 0.0);
        var mock = new MockViewModel(llm, Prompts, s, null, dialogs, new InMemoryMockHistory());
        var history = new HistoryViewModel(new InMemoryMockHistory(), repo, dialogs, s);
        var main = new MainViewModel(home, new SettingsViewModel(s, llm, bank, dialogs), learn, s, null, practice, null, mock, history);
        return (main, s, home, learn, practice, mock);
    }

    // ---- the key

    [Fact]
    public async Task Without_a_key_a_bar_says_so_and_offers_Settings()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings());

            var notice = main.Notice;

            Assert.NotNull(notice);
            Assert.True(main.HasNotice);
            Assert.Equal(NoticeLevel.Warning, notice.Level);
            Assert.Contains("No Anthropic key yet", notice.Text);
            Assert.Contains("Demo mode", notice.Text);
            Assert.Equal("Open Settings", notice.ActionLabel);
        });
    }

    [Fact]
    public async Task The_bar_names_the_provider_that_is_selected()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (router, _, _, _, _, _) = await NewMainAsync(new AppSettings { Provider = LlmProvider.OpenRouter });
            var (compatible, _, _, _, _, _) = await NewMainAsync(new AppSettings { Provider = LlmProvider.OpenAiCompatible });

            Assert.Contains("No OpenRouter key yet", router.Notice!.Text);
            Assert.Contains("No OpenAI-compatible key yet", compatible.Notice!.Text);
        });
    }

    [Fact]
    public async Task With_a_key_in_Demo_mode_or_with_a_local_server_there_is_no_bar()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            Assert.Null((await NewMainAsync(new AppSettings { AnthropicApiKey = "sk-ant-test" })).Main.Notice);
            Assert.Null((await NewMainAsync(new AppSettings { DemoMode = true })).Main.Notice);
            Assert.Null((await NewMainAsync(new AppSettings { Provider = LlmProvider.OpenAiCompatible, OpenAiBaseUrl = "http://localhost:11434/v1" })).Main.Notice);
            Assert.Null((await NewMainAsync(new AppSettings { Provider = LlmProvider.OpenRouter, OpenRouterApiKey = "sk-or-test" })).Main.Notice);
            Assert.False((await NewMainAsync(new AppSettings { AnthropicApiKey = "k" })).Main.HasNotice);
        });
    }

    [Fact]
    public async Task A_key_in_the_environment_counts()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-from-env");
            var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings());

            Assert.Null(main.Notice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }

    [Fact]
    public async Task The_bar_button_opens_Settings_and_the_bar_is_not_shown_there_but_returns_after()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, _, home, _, _, _) = await NewMainAsync(new AppSettings());

            main.RunNoticeActionCommand.Execute(null);

            Assert.True(main.IsSettingsSelected);
            Assert.Null(main.Notice);                                      // no point nagging on the page where the key goes
            main.ShowHomeCommand.Execute(null);
            Assert.Same(home, main.CurrentPage);
            Assert.NotNull(main.Notice);                                   // still no key
        });
    }

    [Fact]
    public async Task Saving_a_key_removes_the_bar_at_once()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, settings, _, _, _, _) = await NewMainAsync(new AppSettings());
            var changed = new List<string?>();
            main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(main.HasNotice);

            var updated = settings.Current.Clone();
            updated.AnthropicApiKey = "sk-ant-new";
            settings.Save(updated);

            Assert.False(main.HasNotice);
            Assert.Contains(nameof(MainViewModel.Notice), changed);
        });
    }

    [Fact]
    public async Task Dismissing_the_key_bar_hides_it_for_this_run()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings());

            main.DismissNoticeCommand.Execute(null);

            Assert.Null(main.Notice);
            main.ShowSettingsCommand.Execute(null);
            main.ShowHomeCommand.Execute(null);
            Assert.Null(main.Notice);
        });
    }

    // ---- a problem that was reported

    [Fact]
    public async Task A_reported_problem_is_shown_as_an_error_bar_until_it_is_dismissed()
    {
        var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings { AnthropicApiKey = "k" });

        main.ReportError("Something unexpected went wrong: boom");

        Assert.True(main.HasNotice);
        Assert.Equal(NoticeLevel.Error, main.Notice!.Level);
        Assert.Equal("Something unexpected went wrong: boom", main.Notice.Text);
        Assert.Null(main.Notice.ActionLabel);
        main.ShowSettingsCommand.Execute(null);
        Assert.True(main.HasNotice);                                       // it stays across pages

        main.DismissNoticeCommand.Execute(null);

        Assert.False(main.HasNotice);
    }

    [Fact]
    public async Task A_reported_problem_comes_before_the_key_bar_and_dismissing_it_brings_the_key_bar_back()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings());
            main.ReportError("Disk full");
            Assert.Equal("Disk full", main.Notice!.Text);

            main.DismissNoticeCommand.Execute(null);

            Assert.Contains("No Anthropic key yet", main.Notice!.Text);
        });
    }

    // ---- the shortcuts

    [Fact]
    public async Task F1_shows_and_hides_the_shortcut_list_and_going_to_another_page_closes_it()
    {
        var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings { AnthropicApiKey = "k" });
        Assert.False(main.ShowShortcuts);

        main.ToggleShortcutsCommand.Execute(null);
        Assert.True(main.ShowShortcuts);
        main.ToggleShortcutsCommand.Execute(null);
        Assert.False(main.ShowShortcuts);

        main.ToggleShortcutsCommand.Execute(null);
        main.CloseShortcutsCommand.Execute(null);
        Assert.False(main.ShowShortcuts);

        main.ToggleShortcutsCommand.Execute(null);
        main.ShowSettingsCommand.Execute(null);
        Assert.False(main.ShowShortcuts);
    }

    private static MainWindow NewWindow(MainViewModel main) => new(main);

    [Fact]
    public async Task The_window_binds_Ctrl_1_to_5_to_the_pages_F1_to_the_list_and_Escape_to_closing_it()
    {
        var (main, _, home, _, _, _) = await NewMainAsync(new AppSettings { AnthropicApiKey = "k" });

        WpfHost.Run(() =>
        {
            var window = NewWindow(main);
            Layout((FrameworkElement)window.Content);                      // the commands are bound once the window has been laid out
            var bindings = window.InputBindings.OfType<KeyBinding>().ToList();
            ICommand CommandFor(Key key, ModifierKeys modifiers) => bindings.Single(b => b.Key == key && b.Modifiers == modifiers).Command;

            Assert.Same(main.ShowHomeCommand, CommandFor(Key.D1, ModifierKeys.Control));
            Assert.Same(main.ShowConceptsCommand, CommandFor(Key.D2, ModifierKeys.Control));
            Assert.Same(main.ShowLibraryCommand, CommandFor(Key.D3, ModifierKeys.Control));
            Assert.Same(main.ShowHistoryCommand, CommandFor(Key.D4, ModifierKeys.Control));
            Assert.Same(main.ShowSettingsCommand, CommandFor(Key.D5, ModifierKeys.Control));
            Assert.Same(main.ToggleShortcutsCommand, CommandFor(Key.F1, ModifierKeys.None));
            Assert.Same(main.CloseShortcutsCommand, CommandFor(Key.Escape, ModifierKeys.None));

            CommandFor(Key.D5, ModifierKeys.Control).Execute(null);
            Assert.True(main.IsSettingsSelected);
            CommandFor(Key.D1, ModifierKeys.Control).Execute(null);
            Assert.Same(home, main.CurrentPage);
        });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_pages_bind_their_own_shortcuts_to_their_own_commands()
    {
        var (_, _, _, learn, practice, mock) = await NewMainAsync(new AppSettings { AnthropicApiKey = "k" });

        WpfHost.Run(() =>
        {
            var learnView = new LearnView { DataContext = learn };
            var practiceView = new PracticeView { DataContext = practice };
            var mockView = new MockView { DataContext = mock };
            Layout(learnView);
            Layout(practiceView);
            Layout(mockView);

            static KeyBinding Find(UserControl view, Key key, ModifierKeys modifiers)
                => view.InputBindings.OfType<KeyBinding>().Single(b => b.Key == key && b.Modifiers == modifiers);

            Assert.Same(learn.NextCommand, Find(learnView, Key.N, ModifierKeys.Control).Command);
            Assert.Same(practice.NextCommand, Find(practiceView, Key.N, ModifierKeys.Control).Command);
            Assert.Same(practice.RepeatQuestionCommand, Find(practiceView, Key.R, ModifierKeys.Control).Command);
            Assert.Same(practice.ToggleMicCommand, Find(practiceView, Key.F2, ModifierKeys.None).Command);
            Assert.Same(mock.RepeatCommand, Find(mockView, Key.R, ModifierKeys.Control).Command);
            Assert.Same(mock.Composer.ToggleMicCommand, Find(mockView, Key.F2, ModifierKeys.None).Command);
        });
        await Task.CompletedTask;
    }

    // ---- the bar and the list on screen

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1180, 820));
            view.Arrange(new Rect(0, 0, 1180, 820));
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

    [Fact]
    public async Task The_window_shows_the_key_bar_with_its_button_and_the_sidebar_has_History()
    {
        await WithoutEnvironmentKeys(async () =>
        {
            var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings());
            WpfHost.TakeBindingErrors();

            WpfHost.Run(() =>
            {
                var window = NewWindow(main);
                var root = (FrameworkElement)window.Content;
                Layout(root);

                var texts = Texts(root);
                Assert.Contains(texts, t => t.StartsWith("No Anthropic key yet"));
                var navigation = Descendants<ToggleButton>(root).Select(b => b.Content as string).ToList();
                Assert.Contains("History", navigation);
                Assert.Contains("Concepts", navigation);
                var buttons = Descendants<Button>(root).Where(Shown).Select(b => b.Content as string).ToList();
                Assert.Contains("Open Settings", buttons);
                Assert.DoesNotContain("Keyboard shortcuts", texts);          // the list is closed
            });

            Assert.Equal("", WpfHost.TakeBindingErrors());
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_window_has_no_bar_when_all_is_well_and_shows_the_shortcut_list_on_request()
    {
        var (main, _, _, _, _, _) = await NewMainAsync(new AppSettings { AnthropicApiKey = "k" });
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var window = NewWindow(main);
            var root = (FrameworkElement)window.Content;
            Layout(root);
            Assert.DoesNotContain(Texts(root), t => t.StartsWith("No Anthropic key"));

            main.ToggleShortcutsCommand.Execute(null);
            Layout(root);

            var texts = Texts(root);
            Assert.Contains("Keyboard shortcuts", texts);
            Assert.Contains("Ctrl+1 to Ctrl+5", texts);
            Assert.Contains("F2", texts);
            Assert.Contains("Ctrl+Enter", texts);

            main.ReportError("Something unexpected went wrong: boom");
            Layout(root);
            Assert.Contains("Something unexpected went wrong: boom", Texts(root));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
        await Task.CompletedTask;
    }
}
