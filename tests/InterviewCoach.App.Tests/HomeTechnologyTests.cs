using System.IO;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

public class HomeTechnologyTests
{
    /// <summary>Reads technologies out of the job description text in the prompt, and can be made to fail or to wait.</summary>
    private sealed class JdAwareLlm : ILlmService
    {
        public int TagCalls { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource<bool>? Gate { get; set; }

        public async Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
        {
            TagCalls++;
            var gate = Gate;
            Gate = null; // only the first call waits
            if (gate is not null) await gate.Task;
            if (Fail) throw new LlmException("model unavailable");

            var technologies = systemPrompt.Contains("Redis") ? new List<string> { "Redis", "Docker" } : ["C#", ".NET", "SQL Server"];
            return (T)(object)new TechTagsDto { Technologies = technologies };
        }

        public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private static async Task<(HomeViewModel Vm, JdAwareLlm Llm, InMemoryProfileRepository Repo)> CreateAsync(
        bool withBank = true, params CandidateProfile[] profiles)
    {
        var repo = new InMemoryProfileRepository();
        foreach (var p in profiles) await repo.SaveAsync(p);
        var llm = new JdAwareLlm();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var bank = withBank ? new TechBank(new InMemoryTechBankRepository(), llm, prompts) : null;
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), bank);
        await vm.InitializeAsync();
        return (vm, llm, repo);
    }

    private static CandidateProfile Profile(string name = "p", string jd = "We use C#, .NET and SQL Server.") =>
        new() { Name = name, JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = jd, ResumeText = "cv" };

    private static async Task SettleAsync() => await Task.Delay(30); // let the fire-and-forget load finish

    // ---- the option itself

    [Fact]
    public async Task The_option_is_offered_only_when_the_technology_bank_is_available()
    {
        var (withBank, _, _) = await CreateAsync(withBank: true);
        var (withoutBank, _, _) = await CreateAsync(withBank: false);

        Assert.True(withBank.HasTechnologyOption);
        Assert.False(withoutBank.HasTechnologyOption);
    }

    [Fact]
    public async Task Ticking_it_unticks_Any_and_reads_the_saved_job_description()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile());

        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.False(vm.AnyType);
        Assert.False(vm.IsLoadingTechnologies);
        Assert.Equal(["C#", ".NET", "SQL Server"], vm.Technologies.Select(t => t.Name));
        Assert.All(vm.Technologies, t => Assert.False(t.IsChecked));  // you choose; nothing is ticked for you
        Assert.Equal(1, llm.TagCalls);
        Assert.Equal("", vm.TechnologyStatus);
    }

    [Fact]
    public async Task Unticking_it_with_nothing_else_picked_goes_back_to_Any()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;

        vm.TechnologyMode = false;

        Assert.True(vm.AnyType);
        Assert.Empty(vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Ticking_Any_clears_both_the_types_and_the_technology_option()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral).IsChecked = true;

        vm.AnyType = true;

        Assert.False(vm.TechnologyMode);
        Assert.All(vm.TypeOptions, o => Assert.False(o.IsChecked));
        Assert.Empty(vm.AllowedTypes);
    }

    [Fact]
    public async Task Ticking_a_normal_type_while_the_technology_option_is_on_keeps_both()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;

        vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral).IsChecked = true;

        Assert.True(vm.TechnologyMode);
        Assert.False(vm.AnyType);
        Assert.Equal([QuestionType.Behavioral], vm.AllowedTypes);
        Assert.Equal(["C#"], vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Unticking_the_last_normal_type_keeps_the_technology_option_instead_of_falling_back_to_Any()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        var behavioral = vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral);
        behavioral.IsChecked = true;
        vm.TechnologyMode = true;

        behavioral.IsChecked = false;

        Assert.True(vm.TechnologyMode);
        Assert.False(vm.AnyType);
    }

    // ---- choosing technologies and starting

    [Fact]
    public async Task Start_needs_at_least_one_technology_and_says_so()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.False(vm.CanStart);
        Assert.Contains("Tick at least one technology", vm.StartHint);

        vm.Technologies[1].IsChecked = true;

        Assert.True(vm.CanStart);
        Assert.Equal("", vm.StartHint);
        Assert.Equal([".NET"], vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Start_hands_over_the_ticked_technologies_with_no_other_types()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;
        vm.Technologies[2].IsChecked = true;
        IReadOnlyCollection<QuestionType>? types = null;
        IReadOnlyList<string>? technologies = null;
        vm.LearnRequested += r => (types, technologies) = (r.Types, r.Technologies);

        vm.StartLearnCommand.Execute(null);

        Assert.Empty(types!);
        Assert.Equal(["C#", "SQL Server"], technologies);
    }

    [Fact]
    public async Task Start_hands_over_technologies_together_with_other_ticked_types()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TypeOptions.Single(o => o.Type == QuestionType.Scenario).IsChecked = true;
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;
        IReadOnlyCollection<QuestionType>? types = null;
        IReadOnlyList<string>? technologies = null;
        vm.LearnRequested += r => (types, technologies) = (r.Types, r.Technologies);

        vm.StartLearnCommand.Execute(null);

        Assert.Equal([QuestionType.Scenario], types);
        Assert.Equal(["C#"], technologies);
    }

    [Fact]
    public async Task Ticked_technologies_are_ignored_once_the_option_is_switched_off()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;
        vm.TechnologyMode = false;
        IReadOnlyList<string>? technologies = null;
        vm.LearnRequested += r => technologies = r.Technologies;

        vm.StartLearnCommand.Execute(null);

        Assert.Empty(technologies!);
    }

    [Fact]
    public async Task Start_waits_while_the_job_description_is_still_being_read()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile());
        var gate = new TaskCompletionSource<bool>();
        llm.Gate = gate;

        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.True(vm.IsLoadingTechnologies);
        Assert.False(vm.CanStart);
        Assert.Contains("Reading the job description", vm.StartHint);

        gate.SetResult(true);
        await SettleAsync();
        Assert.False(vm.IsLoadingTechnologies);
        Assert.Equal(3, vm.Technologies.Count);
    }

    // ---- Other: technologies that were not detected

    [Fact]
    public async Task Typing_a_name_under_Other_ticks_it_and_makes_it_a_selected_technology()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        Assert.False(vm.OtherTechnologyChecked);

        vm.OtherTechnologies = "Kafka";

        Assert.True(vm.OtherTechnologyChecked);          // typing a name means it is wanted
        Assert.Equal(["Kafka"], vm.SelectedTechnologies);
        Assert.True(vm.CanStart);                        // Other alone is enough to start
    }

    [Fact]
    public async Task Detected_and_typed_technologies_are_combined_without_duplicates()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies.Single(t => t.Name == "C#").IsChecked = true;

        vm.OtherTechnologies = "Kafka, c#, GraphQL";    // c# is already ticked above

        Assert.Equal(["C#", "Kafka", "GraphQL"], vm.SelectedTechnologies);
    }

    [Theory]
    [InlineData("Kafka, GraphQL", new[] { "Kafka", "GraphQL" })]
    [InlineData("Kafka;GraphQL", new[] { "Kafka", "GraphQL" })]
    [InlineData("Kafka\nGraphQL", new[] { "Kafka", "GraphQL" })]
    [InlineData("  Kafka  ,, ,  GraphQL  ", new[] { "Kafka", "GraphQL" })]
    [InlineData("kafka, KAFKA, Kafka", new[] { "kafka" })]
    [InlineData("   ", new string[0])]
    public async Task Names_are_split_trimmed_and_deduplicated(string typed, string[] expected)
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();

        vm.OtherTechnologies = typed;

        Assert.Equal(expected, vm.OtherTechnologyNames);
    }

    [Fact]
    public async Task A_very_long_name_is_shortened_and_the_list_is_capped()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();

        vm.OtherTechnologies = new string('x', 200) + ", " + string.Join(", ", Enumerable.Range(1, 20).Select(i => "Tech" + i));

        Assert.Equal(TechBank.MaxTechnologies, vm.OtherTechnologyNames.Count);
        Assert.True(vm.OtherTechnologyNames[0].Length <= 60);
    }

    [Fact]
    public async Task Unticking_Other_ignores_what_was_typed_but_keeps_it_for_later()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.OtherTechnologies = "Kafka";

        vm.OtherTechnologyChecked = false;

        Assert.Empty(vm.SelectedTechnologies);
        Assert.False(vm.CanStart);
        Assert.Equal("Kafka", vm.OtherTechnologies);

        vm.OtherTechnologyChecked = true;
        Assert.Equal(["Kafka"], vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Other_ticked_with_nothing_typed_and_nothing_else_picked_explains_what_to_do()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();

        vm.OtherTechnologyChecked = true;

        Assert.False(vm.CanStart);
        Assert.Contains("Type a technology next to Other", vm.StartHint);
    }

    [Fact]
    public async Task An_empty_Other_does_not_block_a_session_that_has_a_detected_technology_ticked()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;

        vm.OtherTechnologyChecked = true;

        Assert.True(vm.CanStart);
        Assert.Equal(["C#"], vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Other_works_when_no_technologies_were_detected_or_reading_failed()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile());
        llm.Fail = true;
        vm.TechnologyMode = true;
        await SettleAsync();
        Assert.True(vm.TechnologyLoadFailed);
        Assert.Empty(vm.Technologies);

        vm.OtherTechnologies = "Terraform";

        Assert.Equal(["Terraform"], vm.SelectedTechnologies);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Start_hands_over_the_typed_technologies_too()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[1].IsChecked = true;
        vm.OtherTechnologies = "Kafka";
        IReadOnlyList<string>? technologies = null;
        vm.LearnRequested += r => technologies = r.Technologies;

        vm.StartLearnCommand.Execute(null);

        Assert.Equal([".NET", "Kafka"], technologies);
    }

    [Fact]
    public async Task Typed_names_are_ignored_once_By_technology_is_switched_off()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.OtherTechnologies = "Kafka";

        vm.TechnologyMode = false;

        Assert.Empty(vm.SelectedTechnologies);
    }

    [Fact]
    public async Task Typed_names_survive_switching_profiles()
    {
        var (vm, _, _) = await CreateAsync(true, Profile("first", "We use C#."), Profile("second", "We run Redis and Docker."));
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.OtherTechnologies = "Kafka";

        vm.SelectedItem = vm.Profiles.First(p => p != vm.SelectedItem);
        await SettleAsync();

        Assert.Equal(["Kafka"], vm.SelectedTechnologies); // it is not part of any job description, so it stays
    }

    // ---- the saved job description is the source

    [Fact]
    public async Task An_unsaved_new_profile_asks_to_be_saved_first()
    {
        var (vm, llm, _) = await CreateAsync(true);
        vm.NewCommand.Execute(null);

        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.Empty(vm.Technologies);
        Assert.Contains("Save the profile", vm.TechnologyStatus);
        Assert.Equal(0, llm.TagCalls);
    }

    [Fact]
    public async Task A_profile_without_a_job_description_says_so_and_makes_no_model_call()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile(jd: ""));

        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.Empty(vm.Technologies);
        Assert.Contains("no job description", vm.TechnologyStatus);
        Assert.Equal(0, llm.TagCalls);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task Switching_profile_shows_that_profiles_technologies()
    {
        var (vm, _, _) = await CreateAsync(true, Profile("first", "We use C#."), Profile("second", "We run Redis and Docker."));
        vm.TechnologyMode = true;
        await SettleAsync();
        var shown = vm.Technologies.Select(t => t.Name).ToList();

        vm.SelectedItem = vm.Profiles.First(p => p != vm.SelectedItem);
        await SettleAsync();

        Assert.NotEqual(shown, vm.Technologies.Select(t => t.Name));
        Assert.Contains(vm.Technologies, t => t.Name is "Redis" or "C#");
    }

    [Fact]
    public async Task Saving_a_new_job_description_refreshes_the_list_and_keeps_ticks_that_still_apply()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile(jd: "We use C#."));
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies.Single(t => t.Name == "C#").IsChecked = true;
        Assert.Equal(1, llm.TagCalls);

        vm.JobDescription = "We use C# and Redis."; // Redis in the text makes the fake model answer Redis, Docker
        await vm.SaveCommand.ExecuteAsync(null);
        await SettleAsync();

        Assert.Equal(2, llm.TagCalls);                                                     // a changed job description is read again
        Assert.Equal(["Redis", "Docker"], vm.Technologies.Select(t => t.Name));
        Assert.Empty(vm.SelectedTechnologies);                                             // C# is gone, so its tick is too
    }

    [Fact]
    public async Task Reselecting_the_same_unchanged_profile_does_not_read_the_job_description_again()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();

        vm.TechnologyMode = false;
        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.Equal(1, llm.TagCalls); // remembered by the job description's fingerprint
        Assert.Equal(3, vm.Technologies.Count);
    }

    [Fact]
    public async Task Unsaved_edits_block_Start_because_sessions_use_the_saved_profile()
    {
        var (vm, _, _) = await CreateAsync(true, Profile());
        vm.TechnologyMode = true;
        await SettleAsync();
        vm.Technologies[0].IsChecked = true;
        Assert.True(vm.CanStart);

        vm.JobDescription += " More text.";

        Assert.False(vm.CanStart);
        Assert.Contains("Save the profile first", vm.StartHint);
    }

    // ---- failures and races

    [Fact]
    public async Task A_failed_read_shows_the_reason_and_Try_again_works()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile());
        llm.Fail = true;

        vm.TechnologyMode = true;
        await SettleAsync();

        Assert.True(vm.TechnologyLoadFailed);
        Assert.False(vm.IsLoadingTechnologies);
        Assert.Contains("model unavailable", vm.TechnologyStatus);
        Assert.Empty(vm.Technologies);

        llm.Fail = false;
        await vm.ReloadTechnologiesCommand.ExecuteAsync(null);

        Assert.False(vm.TechnologyLoadFailed);
        Assert.Equal(3, vm.Technologies.Count);
    }

    [Fact]
    public async Task A_slow_read_for_a_profile_you_have_left_does_not_overwrite_the_current_list()
    {
        var (vm, llm, _) = await CreateAsync(true, Profile("first", "We use C#."), Profile("second", "We run Redis and Docker."));
        var gate = new TaskCompletionSource<bool>();
        llm.Gate = gate; // the first read will wait

        vm.TechnologyMode = true;          // starts reading the first profile's job description and waits
        await SettleAsync();
        vm.SelectedItem = vm.Profiles.First(p => p != vm.SelectedItem);   // move to the other profile, which reads at once
        await SettleAsync();
        var current = vm.Technologies.Select(t => t.Name).ToList();
        Assert.NotEmpty(current);

        gate.SetResult(true);              // now the stale first read finishes
        await SettleAsync();

        Assert.Equal(current, vm.Technologies.Select(t => t.Name));
        Assert.False(vm.IsLoadingTechnologies);
    }
}
