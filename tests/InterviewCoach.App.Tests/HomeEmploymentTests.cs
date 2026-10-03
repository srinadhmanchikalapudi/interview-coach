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

/// <summary>The Role type control: Full-time or Contract, chosen before a session starts.</summary>
public class HomeEmploymentTests
{
    private sealed class CapturingSettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public int Saves { get; private set; }
        public void Save(AppSettings settings) { Current = settings; Saves++; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private static async Task<(HomeViewModel Vm, CapturingSettings Settings)> CreateAsync(EmploymentType? stored = null, bool withProfile = true)
    {
        var repo = new InMemoryProfileRepository();
        if (withProfile) await repo.SaveAsync(Samples.CompleteProfile());
        var settings = new CapturingSettings(new AppSettings { EmploymentType = stored ?? EmploymentType.FullTime, DemoMode = true });
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank: null, settings);
        await vm.InitializeAsync();
        return (vm, settings);
    }

    private static IEnumerable<QuestionType> Offered(HomeViewModel vm) => vm.TypeOptions.Select(o => o.Type);

    // ---- the control

    [Fact]
    public async Task Full_time_is_chosen_to_begin_with()
    {
        var (vm, _) = await CreateAsync();

        Assert.Equal(EmploymentType.FullTime, vm.EmploymentType);
        Assert.Equal(["Full-time", "Contract"], vm.EmploymentOptions.Select(o => o.Label));
        Assert.Equal([true, false], vm.EmploymentOptions.Select(o => o.IsSelected));
        Assert.Contains("culture fit", vm.EmploymentDescription);
    }

    [Fact]
    public async Task Choosing_a_chip_switches_the_kind_of_interview_and_unselects_the_other()
    {
        var (vm, _) = await CreateAsync();

        vm.EmploymentOptions[1].IsSelected = true;   // what clicking the Contract chip does

        Assert.Equal(EmploymentType.Contract, vm.EmploymentType);
        Assert.Equal([false, true], vm.EmploymentOptions.Select(o => o.IsSelected));
        Assert.Contains("availability and rate", vm.EmploymentDescription);
    }

    [Fact]
    public async Task Setting_the_kind_in_code_keeps_the_chips_in_step()
    {
        var (vm, _) = await CreateAsync();

        vm.EmploymentType = EmploymentType.Contract;

        Assert.Equal([false, true], vm.EmploymentOptions.Select(o => o.IsSelected));
    }

    [Fact]
    public async Task Choosing_the_chip_that_is_already_selected_changes_nothing()
    {
        var (vm, settings) = await CreateAsync();
        var typesBefore = vm.TypeOptions.ToList();

        vm.EmploymentOptions[0].IsSelected = true;

        Assert.Equal(EmploymentType.FullTime, vm.EmploymentType);
        Assert.Equal(0, settings.Saves);
        Assert.Equal(typesBefore, vm.TypeOptions.ToList());
    }

    // ---- the question types follow the kind of interview

    [Fact]
    public async Task A_full_time_interview_offers_motivation_and_fit_and_not_engagement()
    {
        var (vm, _) = await CreateAsync();

        Assert.Equal(8, vm.TypeOptions.Count);
        Assert.Contains(QuestionType.MotivationFit, Offered(vm));
        Assert.DoesNotContain(QuestionType.Engagement, Offered(vm));
    }

    [Fact]
    public async Task Switching_to_contract_swaps_that_type_for_availability_and_engagement()
    {
        var (vm, _) = await CreateAsync();

        vm.EmploymentType = EmploymentType.Contract;

        Assert.Equal(8, vm.TypeOptions.Count);
        Assert.Contains(QuestionType.Engagement, Offered(vm));
        Assert.DoesNotContain(QuestionType.MotivationFit, Offered(vm));
        Assert.Equal("Availability and engagement", vm.TypeOptions.Last().Label);
    }

    [Fact]
    public async Task The_swap_keeps_the_list_object_the_screen_is_bound_to_and_the_order_of_the_rest()
    {
        var (vm, _) = await CreateAsync();
        var list = vm.TypeOptions;
        var common = Offered(vm).Where(t => t.OnlyFor() is null).ToList();

        vm.EmploymentType = EmploymentType.Contract;
        vm.EmploymentType = EmploymentType.FullTime;

        Assert.Same(list, vm.TypeOptions);
        Assert.Equal(common, Offered(vm).Where(t => t.OnlyFor() is null));
        Assert.Equal(QuestionType.MotivationFit, vm.TypeOptions.Last().Type);
    }

    [Fact]
    public async Task Ticks_on_the_common_types_survive_a_switch()
    {
        var (vm, _) = await CreateAsync();
        vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral).IsChecked = true;
        vm.TypeOptions.Single(o => o.Type == QuestionType.SystemDesign).IsChecked = true;

        vm.EmploymentType = EmploymentType.Contract;

        Assert.False(vm.AnyType);
        Assert.Equal(new[] { QuestionType.SystemDesign, QuestionType.Behavioral }.Order(), vm.AllowedTypes.Order());
    }

    [Fact]
    public async Task A_tick_on_the_type_that_goes_away_is_dropped_and_other_ticks_stay()
    {
        var (vm, _) = await CreateAsync();
        vm.TypeOptions.Single(o => o.Type == QuestionType.MotivationFit).IsChecked = true;
        vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral).IsChecked = true;

        vm.EmploymentType = EmploymentType.Contract;

        Assert.Equal([QuestionType.Behavioral], vm.AllowedTypes);
    }

    [Fact]
    public async Task If_the_only_tick_goes_away_the_choice_falls_back_to_Any()
    {
        var (vm, _) = await CreateAsync();
        vm.TypeOptions.Single(o => o.Type == QuestionType.MotivationFit).IsChecked = true;
        Assert.False(vm.AnyType);

        vm.EmploymentType = EmploymentType.Contract;

        Assert.True(vm.AnyType);
        Assert.Empty(vm.AllowedTypes);
    }

    [Fact]
    public async Task Any_stays_Any_across_a_switch()
    {
        var (vm, _) = await CreateAsync();

        vm.EmploymentType = EmploymentType.Contract;

        Assert.True(vm.AnyType);
    }

    [Fact]
    public async Task Ticking_the_contract_type_after_switching_works_like_any_other_type()
    {
        var (vm, _) = await CreateAsync();
        vm.EmploymentType = EmploymentType.Contract;

        vm.TypeOptions.Single(o => o.Type == QuestionType.Engagement).IsChecked = true;

        Assert.False(vm.AnyType);
        Assert.Equal([QuestionType.Engagement], vm.AllowedTypes);
    }

    // ---- remembered between runs

    [Fact]
    public async Task The_saved_kind_of_interview_is_restored_with_its_own_types()
    {
        var (vm, _) = await CreateAsync(stored: EmploymentType.Contract);

        Assert.Equal(EmploymentType.Contract, vm.EmploymentType);
        Assert.Equal([false, true], vm.EmploymentOptions.Select(o => o.IsSelected));
        Assert.Contains(QuestionType.Engagement, Offered(vm));
        Assert.DoesNotContain(QuestionType.MotivationFit, Offered(vm));
    }

    [Fact]
    public async Task Changing_the_kind_saves_it_without_disturbing_other_settings()
    {
        var (vm, settings) = await CreateAsync();

        vm.EmploymentType = EmploymentType.Contract;

        Assert.Equal(1, settings.Saves);
        Assert.Equal(EmploymentType.Contract, settings.Current.EmploymentType);
        Assert.True(settings.Current.DemoMode); // untouched
    }

    [Fact]
    public async Task Without_a_settings_store_the_choice_still_works_for_the_session()
    {
        var repo = new InMemoryProfileRepository();
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());

        vm.EmploymentType = EmploymentType.Contract;

        Assert.Contains(QuestionType.Engagement, Offered(vm));
    }

    // ---- starting a session

    [Fact]
    public async Task Start_hands_over_the_kind_of_interview()
    {
        var (vm, _) = await CreateAsync();
        vm.EmploymentType = EmploymentType.Contract;
        vm.TypeOptions.Single(o => o.Type == QuestionType.Engagement).IsChecked = true;
        LearnSessionRequest? request = null;
        vm.LearnRequested += r => request = r;

        vm.StartCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal(EmploymentType.Contract, request.Employment);
        Assert.Equal([QuestionType.Engagement], request.Types);
    }

    [Fact]
    public async Task Full_time_is_handed_over_by_default()
    {
        var (vm, _) = await CreateAsync();
        LearnSessionRequest? request = null;
        vm.LearnRequested += r => request = r;

        vm.StartCommand.Execute(null);

        Assert.Equal(EmploymentType.FullTime, request!.Employment);
    }

    [Fact]
    public async Task Switching_does_not_get_in_the_way_of_starting()
    {
        var (vm, _) = await CreateAsync();
        Assert.True(vm.CanStart);

        vm.EmploymentType = EmploymentType.Contract;

        Assert.True(vm.CanStart);
        Assert.Equal("", vm.StartHint);
    }

    [Fact]
    public async Task Ticked_technologies_survive_a_switch()
    {
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, prompts);
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank, new CapturingSettings(new AppSettings()));
        await vm.InitializeAsync();
        vm.TechnologyMode = true;
        await Task.Delay(30);
        vm.Technologies[0].IsChecked = true;

        vm.EmploymentType = EmploymentType.Contract;

        Assert.True(vm.TechnologyMode);
        Assert.Equal([vm.Technologies[0].Name], vm.SelectedTechnologies);
    }

    // ---- the screen and the engine

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1000, 900));
            view.Arrange(new Rect(0, 0, 1000, 900));
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

    [Fact]
    public async Task The_home_screen_shows_Role_type_chips_and_clicking_Contract_changes_the_types_offered()
    {
        var (vm, _) = await CreateAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = vm };
            Layout(view);

            Assert.Contains(Descendants<TextBlock>(view), t => t.Text == "Role type");
            var fullTime = Descendants<RadioButton>(view).Single(r => r.Content as string == "Full-time");
            var contract = Descendants<RadioButton>(view).Single(r => r.Content as string == "Contract");
            Assert.True(fullTime.IsChecked);
            Assert.False(contract.IsChecked);
            Assert.Contains(Descendants<CheckBox>(view), c => c.Content as string == "Motivation and fit");
            Assert.DoesNotContain(Descendants<CheckBox>(view), c => c.Content as string == "Availability and engagement");

            contract.IsChecked = true;   // what a click does
            Layout(view);

            Assert.Equal(EmploymentType.Contract, vm.EmploymentType);
            Assert.False(Descendants<RadioButton>(view).Single(r => r.Content as string == "Full-time").IsChecked);
            Assert.Contains(Descendants<CheckBox>(view), c => c.Content as string == "Availability and engagement");
            Assert.DoesNotContain(Descendants<CheckBox>(view), c => c.Content as string == "Motivation and fit");
            Assert.Contains(Descendants<TextBlock>(view), t => t.Text.Contains("availability and rate"));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_Role_type_chips_come_back_selected_every_time_the_home_view_is_created()
    {
        var (vm, _) = await CreateAsync(stored: EmploymentType.Contract);

        WpfHost.Run(() =>
        {
            for (var visit = 1; visit <= 3; visit++)
            {
                var view = new HomeView { DataContext = vm };
                Layout(view);

                Assert.False(Descendants<RadioButton>(view).Single(r => r.Content as string == "Full-time").IsChecked, $"visit {visit}");
                Assert.True(Descendants<RadioButton>(view).Single(r => r.Content as string == "Contract").IsChecked, $"visit {visit}");
            }
        });
    }

    [Fact]
    public void A_Learn_session_started_for_a_contract_role_tells_the_model_so()
    {
        WpfHost.Run(() =>
        {
            var llm = new FakeLlmService();
            var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
            var learn = new LearnViewModel(llm, prompts, new CapturingSettings(new AppSettings()));

            learn.Begin(Samples.CompleteProfile(), [], employment: EmploymentType.Contract);

            Assert.All(llm.Calls, c => Assert.Contains("Contract", c.SystemPrompt));
            Assert.Contains(llm.Calls, c => c.SystemPrompt.Contains("Employment type: Contract"));
            Assert.Contains(llm.Calls, c => c.SystemPrompt.Contains("<employment_type>\nContract\n</employment_type>".Replace("\n", Environment.NewLine))
                                          || c.SystemPrompt.Contains("<employment_type>\nContract\n</employment_type>"));
        });
    }
}
