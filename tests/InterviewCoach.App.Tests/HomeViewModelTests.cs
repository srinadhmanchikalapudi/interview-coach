using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Tests;

public class HomeViewModelTests
{
    private static async Task<(HomeViewModel Vm, InMemoryProfileRepository Repo, ScriptedDialogs Dialogs)> CreateAsync(params CandidateProfile[] existing)
    {
        var repo = new InMemoryProfileRepository();
        foreach (var p in existing) await repo.SaveAsync(p);
        var dialogs = new ScriptedDialogs();
        var vm = new HomeViewModel(repo, new StubExtractor(), dialogs);
        await vm.InitializeAsync();
        return (vm, repo, dialogs);
    }

    // ---- question type filter

    [Fact]
    public async Task Starts_with_any_type_and_nothing_checked()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.True(vm.AnyType);
        Assert.Empty(vm.AllowedTypes);
        Assert.All(vm.TypeOptions, o => Assert.False(o.IsChecked));
    }

    [Fact]
    public async Task Checking_a_type_unchecks_Any_and_unchecking_the_last_one_restores_it()
    {
        var (vm, _, _) = await CreateAsync();
        var behavioral = vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral);
        var design = vm.TypeOptions.Single(o => o.Type == QuestionType.SystemDesign);

        behavioral.IsChecked = true;
        design.IsChecked = true;
        Assert.False(vm.AnyType);
        Assert.Equal(new[] { QuestionType.Behavioral, QuestionType.SystemDesign }.Order(), vm.AllowedTypes.Order());

        behavioral.IsChecked = false;
        Assert.False(vm.AnyType);
        Assert.Equal([QuestionType.SystemDesign], vm.AllowedTypes);

        design.IsChecked = false;
        Assert.True(vm.AnyType);
        Assert.Empty(vm.AllowedTypes);
    }

    [Fact]
    public async Task Checking_Any_clears_the_specific_types()
    {
        var (vm, _, _) = await CreateAsync();
        vm.TypeOptions[0].IsChecked = true;
        vm.TypeOptions[1].IsChecked = true;

        vm.AnyType = true;

        Assert.All(vm.TypeOptions, o => Assert.False(o.IsChecked));
        Assert.Empty(vm.AllowedTypes);
    }

    [Fact]
    public async Task Unchecking_Any_with_nothing_else_chosen_puts_it_back()
    {
        var (vm, _, _) = await CreateAsync();

        vm.AnyType = false;

        Assert.True(vm.AnyType);
    }

    [Fact]
    public async Task Scenario_is_offered_as_a_question_type()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Contains(vm.TypeOptions, o => o.Type == QuestionType.Scenario && o.Label == "Scenario-based");
        Assert.Equal(8, vm.TypeOptions.Count); // seven common types plus the one that belongs to full-time interviews
    }

    // ---- answer length

    [Fact]
    public async Task Answer_length_defaults_to_interviewer_norms()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Equal(AnswerLengthChoice.InterviewerNorm, vm.AnswerLengthChoice);
        Assert.Null(vm.AnswerWords);
        Assert.False(vm.IsCustomLength);
    }

    [Theory]
    [InlineData(AnswerLengthChoice.Short, 80)]
    [InlineData(AnswerLengthChoice.Medium, 150)]
    [InlineData(AnswerLengthChoice.Long, 250)]
    public async Task Presets_map_to_word_counts_and_describe_the_speaking_time(AnswerLengthChoice choice, int words)
    {
        var (vm, _, _) = await CreateAsync();

        vm.AnswerLengthChoice = choice;

        Assert.Equal(words, vm.AnswerWords);
        Assert.Contains($"About {words} words", vm.AnswerLengthHint);
    }

    [Fact]
    public async Task Custom_word_count_is_used_when_valid()
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());

        vm.AnswerLengthChoice = AnswerLengthChoice.Custom;
        vm.CustomWords = " 120 ";

        Assert.True(vm.IsCustomLength);
        Assert.Equal(120, vm.AnswerWords);
        Assert.True(vm.CanStart);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10")]
    [InlineData("9999")]
    [InlineData("-5")]
    public async Task Invalid_custom_word_counts_block_Start_and_say_why(string text)
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());

        vm.AnswerLengthChoice = AnswerLengthChoice.Custom;
        vm.CustomWords = text;

        Assert.Null(vm.AnswerWords);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("number from 30 to 600", vm.StartHint);
    }

    [Fact]
    public async Task Start_passes_the_chosen_length_to_the_session()
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());
        vm.AnswerLengthChoice = AnswerLengthChoice.Short;
        int? requested = null;
        vm.LearnRequested += r => requested = r.AnswerWords;

        vm.StartCommand.Execute(null);

        Assert.Equal(80, requested);
    }

    // ---- starting a session

    [Fact]
    public async Task Start_is_disabled_with_no_profile_and_says_why()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains("profile", vm.StartHint);
    }

    [Fact]
    public async Task Start_is_enabled_for_a_saved_complete_profile()
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());

        Assert.True(vm.CanStart);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("", vm.StartHint);
    }

    [Fact]
    public async Task Start_is_disabled_for_an_incomplete_profile_and_names_what_is_missing()
    {
        var incomplete = Samples.CompleteProfile();
        incomplete.ResumeText = "";
        var (vm, _, _) = await CreateAsync(incomplete);

        Assert.False(vm.CanStart);
        Assert.Contains("resume", vm.StartHint);
    }

    [Fact]
    public async Task Editing_disables_Start_until_saved_because_sessions_use_the_saved_copy()
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());

        vm.JobRole = "Staff Engineer";
        Assert.False(vm.CanStart);
        Assert.Contains("Save", vm.StartHint);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Start_hands_over_the_saved_profile_and_chosen_types()
    {
        var (vm, _, _) = await CreateAsync(Samples.CompleteProfile());
        vm.TypeOptions.Single(o => o.Type == QuestionType.Behavioral).IsChecked = true;
        CandidateProfile? profile = null;
        IReadOnlyCollection<QuestionType>? types = null;
        int? words = -1;
        vm.LearnRequested += r => (profile, types, words) = (r.Profile, r.Types, r.AnswerWords);

        vm.StartCommand.Execute(null);

        Assert.Equal("Senior Backend Engineer", profile!.JobRole);
        Assert.NotEqual(0, profile.Id);
        Assert.Equal([QuestionType.Behavioral], types);
        Assert.Null(words); // default answer length: the Coach follows interviewer norms
    }

    // ---- unsaved changes

    [Fact]
    public async Task Switching_profiles_with_unsaved_changes_asks_and_stays_when_declined()
    {
        var (vm, _, dialogs) = await CreateAsync(Samples.CompleteProfile("first"), Samples.CompleteProfile("second"));
        var current = vm.SelectedItem;
        var other = vm.Profiles.First(p => p != current);
        vm.Name = "edited name";
        dialogs.ConfirmAnswer = false;

        vm.SelectedItem = other;

        Assert.Equal(current, vm.SelectedItem);
        Assert.Equal("edited name", vm.Name);
        Assert.Single(dialogs.Confirmations);
    }

    [Fact]
    public async Task Switching_profiles_discards_changes_when_confirmed()
    {
        var (vm, _, dialogs) = await CreateAsync(Samples.CompleteProfile("first"), Samples.CompleteProfile("second"));
        var current = vm.SelectedItem;
        var other = vm.Profiles.First(p => p != current);
        vm.Name = "edited name";
        dialogs.ConfirmAnswer = true;

        vm.SelectedItem = other;

        Assert.Equal(other, vm.SelectedItem);
        Assert.Equal(other.Title, vm.Name);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Switching_without_changes_does_not_ask()
    {
        var (vm, _, dialogs) = await CreateAsync(Samples.CompleteProfile("first"), Samples.CompleteProfile("second"));
        var other = vm.Profiles.First(p => p != vm.SelectedItem);

        vm.SelectedItem = other;

        Assert.Empty(dialogs.Confirmations);
        Assert.Equal(other, vm.SelectedItem);
    }

    // ---- saving

    [Fact]
    public async Task New_profile_is_saved_and_selected()
    {
        var (vm, repo, _) = await CreateAsync();

        vm.NewCommand.Execute(null);
        vm.JobRole = "SRE";
        vm.JobDescription = "jd";
        vm.ResumeText = "cv";
        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(await repo.ListAsync());
        Assert.Equal("SRE", saved.Name); // blank name falls back to the role
        Assert.Equal(saved.Id, vm.SelectedItem!.Id);
        Assert.False(vm.IsDirty);
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task Loading_a_file_fills_the_resume_and_marks_the_profile_changed()
    {
        var (vm, _, dialogs) = await CreateAsync(Samples.CompleteProfile());
        dialogs.FileToPick = @"C:\anywhere\cv.txt";

        await vm.LoadResumeFileCommand.ExecuteAsync(null);

        Assert.Equal("extracted text", vm.ResumeText);
        Assert.True(vm.IsDirty);
        Assert.Contains("cv.txt", vm.Status);
    }

    [Fact]
    public async Task Cancelling_the_file_picker_changes_nothing()
    {
        var (vm, _, dialogs) = await CreateAsync(Samples.CompleteProfile());
        dialogs.FileToPick = null;

        await vm.LoadResumeFileCommand.ExecuteAsync(null);

        Assert.Equal("Built the ranking service.", vm.ResumeText);
        Assert.False(vm.IsDirty);
    }
}
