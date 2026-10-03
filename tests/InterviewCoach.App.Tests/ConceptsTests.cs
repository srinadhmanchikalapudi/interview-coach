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

/// <summary>The Concepts page: role, technologies from the saved list or the model, a typed technology, difficulty, and a session with no resume.</summary>
public class ConceptsTests
{
    private sealed class CapturingSettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public int Saves { get; private set; }
        public void Save(AppSettings settings) { Current = settings; Saves++; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private sealed class Harness
    {
        public FakeLlmService Llm { get; } = new();
        public InMemoryTechBankRepository Repo { get; } = new();
        public CapturingSettings Settings { get; }
        public TechBank Bank { get; }
        public ConceptsViewModel Vm { get; }

        public int RoleCalls => Llm.Calls.Count(c => c.SystemPrompt.Contains("List the major technologies a"));

        public Harness(AppSettings? settings = null, bool withBank = true, InMemoryTechBankRepository? repo = null)
        {
            Repo = repo ?? Repo;
            Settings = new CapturingSettings(settings ?? new AppSettings());
            Bank = new TechBank(Repo, Llm, Prompts, () => 0.0);
            Vm = new ConceptsViewModel(withBank ? Bank : null, Settings);
        }
    }

    // ---- the role's technologies

    [Fact]
    public async Task Show_technologies_asks_the_model_once_lists_them_and_says_they_were_saved()
    {
        var h = new Harness();
        h.Vm.Role = "Backend developer";

        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        Assert.Equal(1, h.RoleCalls);
        Assert.Equal(["C#", ".NET", "SQL Server", "Docker", "Git", "REST APIs", "Azure"], h.Vm.Technologies.Select(t => t.Name));
        Assert.True(h.Vm.HasTechnologies);
        Assert.Contains("saved", h.Vm.Status);
        Assert.False(h.Vm.IsLoading);
    }

    [Fact]
    public async Task The_same_role_again_is_instant_and_never_calls_the_model_even_after_a_restart()
    {
        var first = new Harness();
        first.Vm.Role = "Backend developer";
        await first.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        var restarted = new Harness(repo: first.Repo); // a new run over the same saved data
        restarted.Vm.Role = "  backend   DEVELOPER ";
        await restarted.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        Assert.Equal(0, restarted.RoleCalls);
        Assert.Equal(7, restarted.Vm.Technologies.Count);
        Assert.Contains("No model call was needed", restarted.Vm.Status);
    }

    [Fact]
    public async Task Opening_the_page_shows_the_saved_list_for_the_remembered_role_without_any_click_or_call()
    {
        var seed = new Harness();
        seed.Vm.Role = "Data engineer";
        await seed.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        var h = new Harness(new AppSettings { ConceptRole = "Data engineer" }, repo: seed.Repo);
        Assert.False(h.Vm.HasTechnologies);                 // nothing is read before the page opens
        h.Vm.SuggestRole(null);
        await Task.Delay(100);

        Assert.True(h.Vm.HasTechnologies);
        Assert.Equal(0, h.RoleCalls);
    }

    [Fact]
    public async Task Opening_the_page_never_calls_the_model_for_a_role_that_was_not_fetched()
    {
        var h = new Harness();

        h.Vm.SuggestRole("Android developer");
        await Task.Delay(100);

        Assert.Equal("Android developer", h.Vm.Role);       // taken from the profile
        Assert.False(h.Vm.HasTechnologies);
        Assert.Equal(0, h.RoleCalls);
    }

    [Fact]
    public void A_role_already_typed_is_not_replaced_by_the_profiles_role()
    {
        var h = new Harness(new AppSettings { ConceptRole = "Data engineer" });

        h.Vm.SuggestRole("Android developer");

        Assert.Equal("Data engineer", h.Vm.Role);
    }

    [Fact]
    public async Task Ask_again_replaces_the_saved_list_and_calls_the_model_again()
    {
        var h = new Harness();
        h.Vm.Role = "Backend developer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        await h.Vm.RefreshTechnologiesCommand.ExecuteAsync(null);

        Assert.Equal(2, h.RoleCalls);
        Assert.Contains("Fetched", h.Vm.Status);
    }

    [Fact]
    public async Task Changing_the_role_clears_the_old_list_but_keeps_ticks_that_the_new_list_also_has()
    {
        var h = new Harness();
        h.Vm.Role = "Backend developer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);
        h.Vm.Technologies.First(t => t.Name == "Docker").IsChecked = true;

        h.Vm.Role = "DevOps engineer";
        Assert.False(h.Vm.HasTechnologies);                  // the old role's technologies are gone
        Assert.Contains("Show technologies", h.Vm.Status);
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        Assert.True(h.Vm.Technologies.First(t => t.Name == "Docker").IsChecked);
        Assert.Equal(2, h.RoleCalls);                        // a new role is a new fetch
    }

    [Fact]
    public async Task A_failed_fetch_is_explained_and_can_be_tried_again()
    {
        var llm = new FakeLlmService((role, prompt) => throw new LlmException("the model is unavailable"));
        var repo = new InMemoryTechBankRepository();
        var vm = new ConceptsViewModel(new TechBank(repo, llm, Prompts, () => 0.0), new CapturingSettings(new AppSettings()));
        vm.Role = "Backend developer";

        await vm.ShowTechnologiesCommand.ExecuteAsync(null);

        Assert.True(vm.LoadFailed);
        Assert.Contains("the model is unavailable", vm.Status);
        Assert.False(vm.HasTechnologies);
        Assert.False(vm.IsLoading);
        Assert.True(vm.ShowTechnologiesCommand.CanExecute(null));
    }

    [Fact]
    public void Show_technologies_needs_a_role_and_the_question_bank()
    {
        var h = new Harness();
        Assert.False(h.Vm.ShowTechnologiesCommand.CanExecute(null));
        h.Vm.Role = "Backend developer";
        Assert.True(h.Vm.ShowTechnologiesCommand.CanExecute(null));

        var noBank = new Harness(withBank: false);
        noBank.Vm.Role = "Backend developer";
        Assert.False(noBank.Vm.ShowTechnologiesCommand.CanExecute(null));
        Assert.False(noBank.Vm.IsAvailable);
        Assert.Contains("question bank", noBank.Vm.StartHint);
    }

    // ---- choosing

    [Fact]
    public async Task Start_needs_at_least_one_technology_and_Other_counts_when_ticked_and_filled()
    {
        var h = new Harness();
        Assert.False(h.Vm.StartCommand.CanExecute(null));
        Assert.Contains("Tick at least one technology", h.Vm.StartHint);

        h.Vm.Role = "Backend developer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);
        Assert.False(h.Vm.StartCommand.CanExecute(null));    // listed, but none ticked
        h.Vm.Technologies[0].IsChecked = true;
        Assert.True(h.Vm.StartCommand.CanExecute(null));
        Assert.Equal("", h.Vm.StartHint);

        h.Vm.Technologies[0].IsChecked = false;
        h.Vm.OtherTechnologyChecked = true;
        Assert.Contains("Type a technology next to Other", h.Vm.StartHint);
        h.Vm.OtherTechnologies = "Kafka";
        Assert.True(h.Vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void A_technology_can_be_typed_with_no_role_at_all_and_typing_ticks_Other()
    {
        var h = new Harness();

        h.Vm.OtherTechnologies = "Kafka, GraphQL ; kafka\nRust";

        Assert.True(h.Vm.OtherTechnologyChecked);
        Assert.Equal(["Kafka", "GraphQL", "Rust"], h.Vm.SelectedTechnologies);
        Assert.True(h.Vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Select_all_and_Clear_tick_and_untick_every_technology()
    {
        var h = new Harness();
        h.Vm.Role = "Backend developer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);

        h.Vm.SelectAllTechnologiesCommand.Execute(null);
        Assert.Equal(7, h.Vm.SelectedTechnologies.Count);
        h.Vm.ClearTechnologiesCommand.Execute(null);
        Assert.Empty(h.Vm.SelectedTechnologies);
    }

    [Fact]
    public void Medium_is_the_default_difficulty_with_a_description_for_each()
    {
        var h = new Harness();

        Assert.Equal(Difficulty.Medium, h.Vm.Difficulty);
        Assert.Equal(["Beginner", "Medium", "Advanced"], h.Vm.DifficultyOptions.Select(o => o.Label));
        Assert.Equal(Difficulty.Medium, h.Vm.DifficultyOptions.Single(o => o.IsSelected).Difficulty);
        Assert.Equal(Difficulties.Description(Difficulty.Medium), h.Vm.DifficultyDescription);

        h.Vm.DifficultyOptions.First(o => o.Difficulty == Difficulty.Advanced).IsSelected = true;
        Assert.Equal(Difficulty.Advanced, h.Vm.Difficulty);
        Assert.Equal(Difficulties.Description(Difficulty.Advanced), h.Vm.DifficultyDescription);
        Assert.Single(h.Vm.DifficultyOptions, o => o.IsSelected);
    }

    // ---- starting a session

    [Fact]
    public async Task Start_in_Learn_hands_over_a_resume_free_profile_the_difficulty_level_and_the_technologies()
    {
        var h = new Harness();
        LearnSessionRequest? request = null;
        h.Vm.LearnRequested += r => request = r;
        h.Vm.Role = "Data engineer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);
        h.Vm.Technologies[0].IsChecked = true;
        h.Vm.Technologies[1].IsChecked = true;
        h.Vm.OtherTechnologies = "Kafka";
        h.Vm.DifficultyOptions.First(o => o.Difficulty == Difficulty.Advanced).IsSelected = true;

        h.Vm.StartCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal("Data engineer", request.Profile.JobRole);
        Assert.Equal(Seniority.Senior, request.Profile.Seniority);
        Assert.True(ConceptSession.IsConceptProfile(request.Profile));
        Assert.Equal([QuestionType.TechnicalConcept], request.Types);
        Assert.Equal(["C#", ".NET", "Kafka"], request.Technologies);
        Assert.Null(request.AnswerWords);
    }

    [Fact]
    public async Task Start_in_Practice_raises_the_practice_request_and_the_answer_length_is_passed_on()
    {
        var h = new Harness();
        LearnSessionRequest? learn = null, practice = null;
        h.Vm.LearnRequested += r => learn = r;
        h.Vm.PracticeRequested += r => practice = r;
        h.Vm.OtherTechnologies = "Kafka";
        h.Vm.SelectPracticeCommand.Execute(null);
        h.Vm.AnswerLengthChoice = AnswerLengthChoice.Short;
        Assert.Equal("Start Practice", h.Vm.StartLabel);

        h.Vm.StartCommand.Execute(null);

        Assert.Null(learn);
        Assert.NotNull(practice);
        Assert.Equal(80, practice.AnswerWords);
        Assert.Equal(ConceptSession.DefaultRole, practice.Profile.JobRole);   // no role: the neutral one
        Assert.Equal(Seniority.Mid, practice.Profile.Seniority);
    }

    [Fact]
    public void The_role_and_difficulty_are_remembered_between_runs_and_not_saved_while_loading()
    {
        var settings = new AppSettings { ConceptRole = "Data engineer", ConceptDifficulty = Difficulty.Beginner };
        var h = new Harness(settings);

        Assert.Equal("Data engineer", h.Vm.Role);
        Assert.Equal(Difficulty.Beginner, h.Vm.Difficulty);
        Assert.Equal(0, h.Settings.Saves);                     // restoring the choices is not a change

        h.Vm.DifficultyOptions.First(o => o.Difficulty == Difficulty.Advanced).IsSelected = true;

        Assert.Equal(Difficulty.Advanced, h.Settings.Current.ConceptDifficulty);
        Assert.Equal("Data engineer", h.Settings.Current.ConceptRole);
    }

    // ---- a real session from the page

    private static async Task<(MainViewModel Main, ConceptsViewModel Concepts, LearnViewModel Learn, PracticeViewModel Practice, HomeViewModel Home)> NewMainAsync()
    {
        var settings = new CapturingSettings(new AppSettings());
        var llm = new FakeLlmService();
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank);
        await home.InitializeAsync();
        var learn = new LearnViewModel(llm, Prompts, settings, bank, () => 0.0);
        var practice = new PracticeViewModel(llm, Prompts, settings, bank, () => 0.0);
        var concepts = new ConceptsViewModel(bank, settings);
        var main = new MainViewModel(home, new SettingsViewModel(settings, llm, bank, new ScriptedDialogs()), learn, settings, null, practice, concepts);
        return (main, concepts, learn, practice, home);
    }

    [Fact]
    public async Task The_sidebar_opens_Concepts_prefilled_with_the_profiles_role_and_a_session_returns_to_it()
    {
        var (main, concepts, learn, _, home) = await NewMainAsync();
        Assert.True(main.HasConcepts);
        Assert.True(main.IsHomeSelected);

        main.ShowConceptsCommand.Execute(null);
        Assert.Same(concepts, main.CurrentPage);
        Assert.True(main.IsConceptsSelected);
        Assert.False(main.IsHomeSelected);
        Assert.Equal(home.JobRole, concepts.Role);

        concepts.OtherTechnologies = "Redis";
        concepts.StartCommand.Execute(null);
        Assert.Same(learn, main.CurrentPage);
        Assert.Equal("Redis", learn.Technology);
        Assert.False(learn.CanTailor);                        // there is no resume to tailor an answer to

        learn.ExitCommand.Execute(null);
        Assert.Same(concepts, main.CurrentPage);              // back where it started, not Home
    }

    [Fact]
    public async Task A_practice_session_from_Concepts_asks_about_the_technology_and_returns_to_Concepts()
    {
        var (main, concepts, _, practice, _) = await NewMainAsync();
        main.ShowConceptsCommand.Execute(null);
        concepts.OtherTechnologies = "Redis";
        concepts.SelectPracticeCommand.Execute(null);

        concepts.StartCommand.Execute(null);

        Assert.Same(practice, main.CurrentPage);
        Assert.True(practice.IsAnswering);
        Assert.Equal("Redis", practice.Technology);
        practice.ExitCommand.Execute(null);
        Assert.Same(concepts, main.CurrentPage);
    }

    [Fact]
    public async Task A_session_started_from_Home_still_returns_to_Home()
    {
        var (main, _, learn, _, home) = await NewMainAsync();
        main.ShowConceptsCommand.Execute(null);
        main.ShowHomeCommand.Execute(null);

        home.StartCommand.Execute(null);
        Assert.Same(learn, main.CurrentPage);
        learn.ExitCommand.Execute(null);

        Assert.Same(home, main.CurrentPage);
    }

    [Fact]
    public async Task A_resume_based_session_still_offers_Tailor_to_my_resume()
    {
        var (_, _, learn, _, home) = await NewMainAsync();
        home.AnyType = false;
        home.TypeOptions.First(o => o.Type == QuestionType.TechnicalConcept).IsChecked = true;

        home.StartCommand.Execute(null);

        Assert.True(learn.CanTailor);
    }

    // ---- the screen

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 1800));
            view.Arrange(new Rect(0, 0, 1000, 1800));
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
    public async Task The_page_shows_the_four_steps_the_technologies_the_difficulty_chips_and_binds_cleanly()
    {
        var h = new Harness();
        h.Vm.Role = "Backend developer";
        await h.Vm.ShowTechnologiesCommand.ExecuteAsync(null);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new ConceptsView { DataContext = h.Vm };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("Technology concepts", texts);
            Assert.Contains("Job role", texts);
            Assert.Contains("Technologies", texts);
            Assert.Contains("Difficulty", texts);
            Assert.Contains("How to practise", texts);
            var chips = Descendants<CheckBox>(view).Where(Shown).SelectMany(b => Texts(b)).ToList();
            Assert.Contains("C#", chips);
            Assert.Contains("Docker", chips);
            Assert.Contains("Other", chips);
            var radios = Descendants<RadioButton>(view).Where(Shown).SelectMany(b => Texts(b)).ToList();
            Assert.Contains("Beginner", radios);
            Assert.Contains("Medium", radios);
            Assert.Contains("Advanced", radios);
            var buttons = Descendants<Button>(view).Where(Shown).SelectMany(b => Texts(b)).ToList();
            Assert.Contains("Show technologies", buttons);
            Assert.Contains("Ask again", buttons);
            Assert.Contains("Start Learn", buttons);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public void Before_a_role_is_looked_up_there_are_no_technology_chips_or_list_buttons()
    {
        WpfHost.Run(() =>
        {
            var view = new ConceptsView { DataContext = new Harness().Vm };
            Layout(view);

            Assert.DoesNotContain("Ask again", Descendants<Button>(view).Where(Shown).SelectMany(b => Texts(b)));
            Assert.DoesNotContain("Select all", Descendants<Button>(view).Where(Shown).SelectMany(b => Texts(b)));
        });
    }
}
