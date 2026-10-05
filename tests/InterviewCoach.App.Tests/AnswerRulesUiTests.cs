using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Tests;

/// <summary>The "How should answers sound?" box on the Home screen.</summary>
public class AnswerRulesUiTests
{
    private static async Task<(HomeViewModel Vm, InMemoryProfileRepository Repo)> CreateAsync(params CandidateProfile[] existing)
    {
        var repo = new InMemoryProfileRepository();
        foreach (var p in existing) await repo.SaveAsync(p);
        var vm = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs());
        await vm.InitializeAsync();
        return (vm, repo);
    }

    private static CandidateProfile WithRules(string rules)
    {
        var p = Samples.CompleteProfile();
        p.AnswerRules = rules;
        return p;
    }

    [Fact]
    public async Task The_rules_of_the_selected_profile_are_shown_and_a_new_profile_starts_without_any()
    {
        var (vm, _) = await CreateAsync(WithRules("Use STAR."));

        vm.SelectedItem = vm.Profiles.First();
        Assert.Equal("Use STAR.", vm.AnswerRules);

        vm.NewCommand.Execute(null);
        Assert.Equal("", vm.AnswerRules);
    }

    [Fact]
    public async Task Changing_the_rules_marks_the_profile_changed_and_saving_keeps_them()
    {
        var (vm, repo) = await CreateAsync(Samples.CompleteProfile());
        vm.SelectedItem = vm.Profiles.First();
        Assert.False(vm.IsDirty);

        vm.AnswerRules = "Keep it concise.";
        Assert.True(vm.IsDirty);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);
        Assert.Equal("Keep it concise.", Assert.Single(await repo.ListAsync()).AnswerRules);
    }

    [Fact]
    public async Task Reverting_puts_the_saved_rules_back()
    {
        var (vm, _) = await CreateAsync(WithRules("Use STAR."));
        vm.SelectedItem = vm.Profiles.First();

        vm.AnswerRules = "something else";
        vm.RevertCommand.Execute(null);

        Assert.Equal("Use STAR.", vm.AnswerRules);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task A_session_starts_with_the_saved_rules()
    {
        var (vm, _) = await CreateAsync(WithRules("Use STAR."));
        vm.SelectedItem = vm.Profiles.First();
        LearnSessionRequest? request = null;
        vm.LearnRequested += r => request = r;

        vm.SelectLearnCommand.Execute(null);
        vm.StartCommand.Execute(null);

        Assert.Equal("Use STAR.", request!.Profile.AnswerRules);
    }

    [Fact]
    public async Task An_example_adds_its_sentence_on_a_new_line_once()
    {
        var (vm, _) = await CreateAsync(Samples.CompleteProfile());
        vm.SelectedItem = vm.Profiles.First();
        var star = vm.AnswerRuleExamples.Single(e => e.Label == "STAR").Rule;
        var concise = vm.AnswerRuleExamples.Single(e => e.Label == "Concise").Rule;

        vm.AddAnswerRuleCommand.Execute(star);
        Assert.Equal(star, vm.AnswerRules);

        vm.AddAnswerRuleCommand.Execute(concise);
        vm.AddAnswerRuleCommand.Execute(star);                       // already there
        Assert.Equal(star + Environment.NewLine + concise, vm.AnswerRules);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task An_example_that_would_pass_the_limit_is_not_added()
    {
        var (vm, _) = await CreateAsync(Samples.CompleteProfile());
        vm.SelectedItem = vm.Profiles.First();
        vm.AnswerRules = new string('a', CandidateProfile.MaxAnswerRulesLength - 5);

        vm.AddAnswerRuleCommand.Execute(vm.AnswerRuleExamples[0].Rule);

        Assert.Equal(CandidateProfile.MaxAnswerRulesLength - 5, vm.AnswerRules.Length);
    }

    [Fact]
    public async Task The_counter_shows_the_length_against_the_limit()
    {
        var (vm, _) = await CreateAsync(Samples.CompleteProfile());
        vm.SelectedItem = vm.Profiles.First();

        vm.AnswerRules = "Use STAR.";

        Assert.Equal($"9 / {CandidateProfile.MaxAnswerRulesLength:N0} characters", vm.AnswerRulesStats);
    }

    [Fact]
    public void Every_example_fits_and_has_a_label()
    {
        Assert.All(AnswerRuleExample.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Label));
            Assert.InRange(e.Rule.Length, 10, 200);
        });
        Assert.Equal(AnswerRuleExample.All.Count, AnswerRuleExample.All.Select(e => e.Rule).Distinct().Count());
    }
}
