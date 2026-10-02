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

public class LibraryTests
{
    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class ThrowingHistory : ILearnHistory
    {
        public Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default) => throw new IOException("the file is locked");
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private static CoachOutput Answer(string text = "A struct is a value type.", params FollowUp[] followUps) => new()
    {
        WhatTheyreTesting = "the signal",
        ModelAnswer = text,
        Shape = "Direct answer -> why",
        FollowUps = [.. followUps],
    };

    private static LearnHistoryEntry Entry(
        string question, string type = "technical_concept", string? technology = "C#", bool general = true, string? profile = null,
        string? parent = null, string answer = "A struct is a value type.", params FollowUp[] followUps) => new()
    {
        Question = question, QuestionType = type, Technology = technology, Seniority = "Senior", Source = "fundamentals",
        IsFollowUp = parent is not null, ParentQuestion = parent, IsGeneral = general, ProfileName = profile, Coach = Answer(answer, followUps),
    };

    private static async Task<(LibraryViewModel Vm, InMemoryLearnHistory History, Clockwork Clock, ScriptedDialogs Dialogs)> NewAsync(
        Func<Clockwork, InMemoryLearnHistory, Task>? seed = null)
    {
        var clock = new Clockwork();
        var history = new InMemoryLearnHistory(() => clock.Now);
        if (seed is not null) await seed(clock, history);
        var dialogs = new ScriptedDialogs();
        var vm = new LibraryViewModel(history, dialogs, () => clock.Now.ToLocalTime());
        await vm.LoadAsync();
        return (vm, history, clock, dialogs);
    }

    // Five entries of four types, seen at five different times (the first one recorded is the oldest).
    private static async Task SeedAsync(Clockwork clock, InterviewCoach.Infrastructure.Fakes.InMemoryLearnHistory h)
    {
        await h.RecordAsync(Entry("What is a struct?", answer: "A struct is a value type that lives on the stack."));
        clock.Advance(TimeSpan.FromMinutes(10));
        await h.RecordAsync(Entry("What is a hook?", technology: "React", answer: "A hook lets a function component keep state."));
        clock.Advance(TimeSpan.FromMinutes(10));
        await h.RecordAsync(Entry("Tell me about a hard deadline.", type: "behavioral", technology: null, general: false, profile: "Claims platform", answer: "We cut scope and shipped the core."));
        clock.Advance(TimeSpan.FromMinutes(10));
        await h.RecordAsync(Entry("Design a rate limiter.", type: "system_design", technology: null, general: false, profile: "Claims platform", answer: "A token bucket per client."));
        clock.Advance(TimeSpan.FromMinutes(10));
        await h.RecordAsync(Entry("What is a class?", answer: "A class is a reference type."));
    }

    private static string[] Questions(LibraryViewModel vm) => vm.Rows.Select(r => r.Question).ToArray();

    // ---- what is shown

    [Fact]
    public async Task Everything_that_was_recorded_is_listed_with_the_newest_first()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        Assert.Equal(["What is a class?", "Design a rate limiter.", "Tell me about a hard deadline.", "What is a hook?", "What is a struct?"], Questions(vm));
        Assert.Equal("5 entries", vm.CountText);
        Assert.True(vm.HasEntries);
        Assert.False(vm.IsEmpty);
        Assert.True(vm.NothingSelected);
    }

    [Fact]
    public async Task With_nothing_recorded_the_screen_says_so()
    {
        var (vm, _, _, _) = await NewAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasEntries);
        Assert.Empty(vm.Rows);
        Assert.Equal("", vm.CountText);
    }

    [Fact]
    public async Task A_problem_reading_the_library_is_shown_instead_of_crashing()
    {
        var vm = new LibraryViewModel(new ThrowingHistory());

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.Contains("the file is locked", vm.LoadError);
        Assert.False(vm.IsEmpty);   // not "nothing yet": something went wrong
        Assert.False(vm.IsLoading);
    }

    // ---- filtering by question type

    [Fact]
    public async Task The_type_filter_offers_All_and_only_the_types_that_are_there_in_the_usual_order_with_counts()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        Assert.Equal(["All  5", "Technical concept  3", "System design  1", "Behavioral  1"], vm.TypeFilters.Select(f => f.Text).ToArray());
        Assert.True(vm.TypeFilters[0].IsSelected);
        Assert.Single(vm.TypeFilters, f => f.IsSelected);
    }

    [Fact]
    public async Task Choosing_a_type_shows_only_that_type_and_All_brings_everything_back()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.TypeFilters.Single(f => f.TypeId == "technical_concept").IsSelected = true;

        Assert.Equal(["What is a class?", "What is a hook?", "What is a struct?"], Questions(vm));
        Assert.Equal("3 of 5 entries", vm.CountText);
        Assert.Single(vm.TypeFilters, f => f.IsSelected); // choosing one chip clears the others

        vm.TypeFilters[0].IsSelected = true;

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("5 entries", vm.CountText);
    }

    [Fact]
    public async Task The_chosen_type_is_kept_when_the_library_is_read_again()
    {
        var (vm, history, clock, _) = await NewAsync(SeedAsync);
        vm.TypeFilters.Single(f => f.TypeId == "behavioral").IsSelected = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await history.RecordAsync(Entry("Tell me about a conflict.", type: "behavioral", technology: null));

        await vm.LoadAsync();

        Assert.Equal(["Tell me about a conflict.", "Tell me about a hard deadline."], Questions(vm));
        Assert.Equal("Behavioral  2", vm.TypeFilters.Single(f => f.IsSelected).Text);
    }

    [Fact]
    public async Task A_chosen_type_that_no_longer_exists_falls_back_to_All()
    {
        var (vm, history, _, _) = await NewAsync(SeedAsync);
        vm.TypeFilters.Single(f => f.TypeId == "system_design").IsSelected = true;
        await history.DeleteAsync((await history.ListAsync()).Single(e => e.QuestionType == "system_design").Id);

        await vm.LoadAsync();

        Assert.True(vm.TypeFilters[0].IsSelected);
        Assert.Equal(4, vm.Rows.Count);
    }

    // ---- searching

    [Fact]
    public async Task Search_matches_the_question_the_technology_the_type_or_the_answer_ignoring_case()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.Search = "REACT";
        Assert.Equal(["What is a hook?"], Questions(vm));                       // technology

        vm.Search = "token bucket";
        Assert.Equal(["Design a rate limiter."], Questions(vm));               // answer text, every word must match

        vm.Search = "behavioral";
        Assert.Equal(["Tell me about a hard deadline."], Questions(vm));       // type label

        vm.Search = "struct stack";
        Assert.Equal(["What is a struct?"], Questions(vm));                    // question and answer words together

        vm.Search = "nothing like this";
        Assert.Empty(vm.Rows);
        Assert.True(vm.NoMatches);

        vm.Search = "";
        Assert.Equal(5, vm.Rows.Count);
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public async Task Search_and_the_type_filter_work_together()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.TypeFilters.Single(f => f.TypeId == "technical_concept").IsSelected = true;
        vm.Search = "value type";

        Assert.Equal(["What is a struct?"], Questions(vm));
    }

    // ---- sorting

    [Fact]
    public async Task Sorting_by_oldest_by_question_and_by_type()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.Sort = LibrarySort.Oldest;
        Assert.Equal(["What is a struct?", "What is a hook?", "Tell me about a hard deadline.", "Design a rate limiter.", "What is a class?"], Questions(vm));

        vm.Sort = LibrarySort.Question;
        Assert.Equal(["Design a rate limiter.", "Tell me about a hard deadline.", "What is a class?", "What is a hook?", "What is a struct?"], Questions(vm));

        vm.Sort = LibrarySort.Type; // by the label ("Behavioral", "System design", "Technical concept"), newest first inside a type
        Assert.Equal(["Tell me about a hard deadline.", "Design a rate limiter.", "What is a class?", "What is a hook?", "What is a struct?"], Questions(vm));

        vm.Sort = LibrarySort.Newest;
        Assert.Equal("What is a class?", Questions(vm)[0]);
    }

    [Fact]
    public async Task Sorting_applies_inside_a_filtered_list()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);
        vm.TypeFilters.Single(f => f.TypeId == "technical_concept").IsSelected = true;

        vm.Sort = LibrarySort.Question;

        Assert.Equal(["What is a class?", "What is a hook?", "What is a struct?"], Questions(vm));
    }

    [Fact]
    public async Task A_question_seen_again_moves_to_the_top()
    {
        var (vm, history, clock, _) = await NewAsync(SeedAsync);
        clock.Advance(TimeSpan.FromHours(1));
        await history.RecordAsync(Entry("What is a struct?", answer: "A struct is a value type, copied on assignment."));

        await vm.LoadAsync();

        Assert.Equal("What is a struct?", Questions(vm)[0]);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("Seen 2 times", vm.Rows[0].Seen);
    }

    // ---- opening one

    [Fact]
    public async Task Choosing_a_question_shows_its_answer_and_where_it_came_from()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.Selected = vm.Rows.Single(r => r.Question == "Tell me about a hard deadline.");

        Assert.True(vm.HasDetail);
        Assert.False(vm.NothingSelected);
        Assert.Equal("Tell me about a hard deadline.", vm.DetailQuestion);
        Assert.Equal("Behavioral", vm.DetailTypeLabel);
        Assert.False(vm.HasDetailTechnology);
        Assert.Equal("We cut scope and shipped the core.", vm.Detail!.ModelAnswer);
        Assert.Contains("Tailored to your resume for Claims platform", vm.DetailMeta);
        Assert.StartsWith("Last seen ", vm.DetailMeta);
    }

    [Fact]
    public async Task A_general_answer_says_it_was_written_without_the_resume()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        vm.Selected = vm.Rows.Single(r => r.Question == "What is a hook?");

        Assert.Equal("React", vm.DetailTechnology);
        Assert.True(vm.HasDetailTechnology);
        Assert.Contains("General answer, written without your resume", vm.DetailMeta);
    }

    [Fact]
    public async Task The_answer_is_shown_without_a_warm_up_even_if_it_was_saved_with_one()
    {
        var (vm, _, _, _) = await NewAsync(async (_, h) =>
            await h.RecordAsync(Entry("What is a struct?", answer: "Sure. Short version: a struct is a value type, a class is a reference type.")));

        vm.Selected = vm.Rows[0];

        Assert.Equal("A struct is a value type, a class is a reference type.", vm.Detail!.ModelAnswer);
    }

    [Fact]
    public async Task A_question_seen_more_than_once_says_how_many_times()
    {
        var (vm, history, clock, _) = await NewAsync(SeedAsync);
        clock.Advance(TimeSpan.FromHours(1));
        await history.RecordAsync(Entry("What is a class?"));
        await history.RecordAsync(Entry("What is a class?"));
        await vm.LoadAsync();

        vm.Selected = vm.Rows[0];

        Assert.Contains("Seen 3 times", vm.DetailMeta);
    }

    [Fact]
    public async Task A_follow_up_shows_the_question_it_belongs_to()
    {
        var (vm, _, _, _) = await NewAsync(async (_, h) =>
            await h.RecordAsync(Entry("Why that approach?", parent: "What is a struct?")));

        vm.Selected = vm.Rows[0];

        Assert.True(vm.DetailIsFollowUp);
        Assert.Equal("What is a struct?", vm.DetailParent);
        Assert.True(vm.Rows[0].IsFollowUp);
    }

    [Fact]
    public async Task The_open_question_stays_open_while_it_still_matches_and_closes_when_it_does_not()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);
        vm.Selected = vm.Rows.Single(r => r.Question == "What is a hook?");

        vm.Search = "hook";
        Assert.Equal("What is a hook?", vm.Selected?.Question);
        Assert.True(vm.HasDetail);

        vm.Search = "struct";
        Assert.Null(vm.Selected);
        Assert.Null(vm.Detail);
        Assert.False(vm.HasDetail);
    }

    // ---- follow-ups inside an answer

    private static readonly FollowUp WhyThat = new() { Question = "Why that approach?", Hint = "Name one alternative." };

    [Fact]
    public async Task Opening_a_follow_up_that_was_seen_shows_its_saved_answer_even_if_the_list_was_filtered()
    {
        var (vm, _, _, _) = await NewAsync(async (clock, h) =>
        {
            await h.RecordAsync(Entry("What is a struct?", followUps: WhyThat));
            clock.Advance(TimeSpan.FromMinutes(1));
            await h.RecordAsync(Entry("Why that approach?", parent: "What is a struct?", answer: "Because copying is cheap for small values."));
        });
        vm.Selected = vm.Rows.Single(r => !r.IsFollowUp);
        vm.Search = "struct"; // hides the follow-up from the list (its text does not mention a struct)

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.Equal("Why that approach?", vm.Selected?.Question);
        Assert.True(vm.DetailIsFollowUp);
        Assert.Equal("Because copying is cheap for small values.", vm.Detail!.ModelAnswer);
        Assert.Equal("", vm.Search);   // the filter was cleared so the follow-up is visible in the list
    }

    [Fact]
    public async Task Opening_a_follow_up_that_was_never_opened_in_Learn_mode_explains_why_nothing_is_saved()
    {
        var (vm, _, _, _) = await NewAsync(async (_, h) => await h.RecordAsync(Entry("What is a struct?", followUps: WhyThat)));
        vm.Selected = vm.Rows[0];

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.Equal("What is a struct?", vm.Selected?.Question); // stays where it was
        Assert.True(vm.HasMessage);
        Assert.Contains("not opened this follow-up in Learn mode", vm.Message);
    }

    [Fact]
    public async Task The_same_follow_up_under_another_question_is_not_mistaken_for_this_ones()
    {
        var (vm, _, _, _) = await NewAsync(async (_, h) =>
        {
            await h.RecordAsync(Entry("What is a struct?", followUps: WhyThat));
            await h.RecordAsync(Entry("Why that approach?", parent: "What is a class?")); // belongs to a different parent
        });
        vm.Selected = vm.Rows.Single(r => r.Question == "What is a struct?");

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.True(vm.HasMessage);
        Assert.False(vm.DetailIsFollowUp);
    }

    // ---- removing and leaving

    [Fact]
    public async Task Removing_asks_first_and_does_nothing_when_declined()
    {
        var (vm, history, _, dialogs) = await NewAsync(SeedAsync);
        vm.Selected = vm.Rows[0];
        dialogs.ConfirmAnswer = false;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(5, (await history.ListAsync()).Count);
        Assert.Single(dialogs.Confirmations);
        Assert.NotNull(vm.Selected);
    }

    [Fact]
    public async Task Removing_takes_the_entry_out_of_the_list_and_closes_it()
    {
        var (vm, history, _, _) = await NewAsync(SeedAsync);
        vm.Selected = vm.Rows.Single(r => r.Question == "What is a hook?");

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.DoesNotContain("What is a hook?", Questions(vm));
        Assert.Equal(4, (await history.ListAsync()).Count);
        Assert.Null(vm.Selected);
        Assert.Equal("4 entries", vm.CountText);
    }

    [Fact]
    public async Task Remove_is_only_possible_with_something_open()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);

        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        vm.Selected = vm.Rows[0];
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Back_asks_to_return_to_Home()
    {
        var (vm, _, _, _) = await NewAsync();
        var raised = 0;
        vm.ExitRequested += () => raised++;

        vm.BackCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    // ---- when it was last seen

    [Fact]
    public async Task The_time_is_shown_as_today_yesterday_or_a_date()
    {
        var (vm, history, clock, _) = await NewAsync(async (c, h) =>
        {
            await h.RecordAsync(Entry("Old?"));        // recorded at the start
            c.Advance(TimeSpan.FromDays(1));
            await h.RecordAsync(Entry("Yesterday?"));
            c.Advance(TimeSpan.FromDays(1));
            await h.RecordAsync(Entry("Today?"));
        });

        var today = vm.Rows.Single(r => r.Question == "Today?").When;
        var yesterday = vm.Rows.Single(r => r.Question == "Yesterday?").When;
        var older = vm.Rows.Single(r => r.Question == "Old?").When;

        Assert.StartsWith("today, ", today);
        Assert.StartsWith("yesterday, ", yesterday);
        Assert.Matches(@"^\d{1,2} \w{3} 2026$", older);
    }

    // ---- Learn mode feeds it

    [Fact]
    public void A_Learn_session_adds_what_it_shows_to_the_library()
    {
        var history = new InMemoryLearnHistory();
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var learn = new LearnViewModel(llm, prompts, new MemorySettings(new AppSettings()),
            new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0), () => 0.0, history);

        WpfHost.Run(() => learn.Begin(Samples.CompleteProfile(), [QuestionType.TechnicalConcept]));

        var entry = Assert.Single(history.ListAsync().GetAwaiter().GetResult());
        Assert.Equal(learn.Question, entry.Question);
        Assert.Equal("technical_concept", entry.QuestionType);
    }

    // ---- the real view

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1100, 800));
            view.Arrange(new Rect(0, 0, 1100, 800));
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

    private static IEnumerable<string> Texts(DependencyObject root)
        => Descendants<TextBlock>(root).Where(Shown).Select(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : t.Text);

    [Fact]
    public async Task The_view_lists_the_entries_with_filters_and_sorting_and_binds_cleanly()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new LibraryView { DataContext = vm };
            Layout(view);

            var texts = Texts(view).ToList();
            Assert.Contains("Library", texts);
            Assert.Contains("What is a class?", texts);
            Assert.Contains("Tell me about a hard deadline.", texts);
            Assert.Contains("Pick a question on the left to read its answer again.", texts);
            Assert.DoesNotContain("Nothing here yet", texts);

            // The type chips: All plus the three types present.
            var chips = Descendants<RadioButton>(view).Where(Shown).Select(r => r.Content as string).ToList();
            Assert.Equal(["All  5", "Technical concept  3", "System design  1", "Behavioral  1"], chips);
            Assert.Equal(1, Descendants<RadioButton>(view).Count(r => r.IsChecked == true));

            Assert.Equal(4, Descendants<ComboBox>(view).Single().Items.Count);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task Opening_an_entry_in_the_view_shows_its_question_and_the_full_answer_cards()
    {
        var (vm, _, _, _) = await NewAsync(SeedAsync);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new LibraryView { DataContext = vm };
            vm.Selected = vm.Rows.Single(r => r.Question == "What is a hook?");
            Layout(view);

            var texts = Texts(view).ToList();
            Assert.Contains(texts, t => t == "A hook lets a function component keep state.");   // the spoken answer card
            Assert.Contains(texts, t => t.StartsWith("WHAT THEY"));
            Assert.Contains(texts, t => t.StartsWith("Last seen "));
            Assert.Contains(texts, t => t == "Remove from library");
            Assert.DoesNotContain("Pick a question on the left to read its answer again.", texts);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_empty_library_shows_a_message_and_no_list()
    {
        var (vm, _, _, _) = await NewAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new LibraryView { DataContext = vm };
            Layout(view);

            var texts = Texts(view).ToList();
            Assert.Contains("Nothing here yet", texts);
            Assert.DoesNotContain(Descendants<ListBox>(view), Shown);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    // ---- getting there

    private static (MainViewModel Main, HomeViewModel Home, LibraryViewModel Library) NewMain(bool withLibrary = true)
    {
        var settings = new MemorySettings(new AppSettings());
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0);
        var home = new HomeViewModel(new InMemoryProfileRepository(), new StubExtractor(), new ScriptedDialogs(), bank);
        var learn = new LearnViewModel(llm, prompts, settings, bank, () => 0.0);
        var library = new LibraryViewModel(new InMemoryLearnHistory(), new ScriptedDialogs());
        var main = new MainViewModel(home, new SettingsViewModel(settings, llm, bank, new ScriptedDialogs()), learn, settings, withLibrary ? library : null);
        return (main, home, library);
    }

    [Fact]
    public void The_Revisit_card_on_Home_opens_the_library_and_going_back_returns_Home()
    {
        var (main, home, library) = NewMain();
        Assert.True(main.IsHomeSelected);

        home.OpenLibraryCommand.Execute(null);

        Assert.Same(library, main.CurrentPage);
        Assert.True(main.IsLibrarySelected);
        Assert.False(main.IsHomeSelected);

        library.BackCommand.Execute(null);

        Assert.Same(home, main.CurrentPage);
        Assert.True(main.IsHomeSelected);
        Assert.False(main.IsLibrarySelected);
    }

    [Fact]
    public void The_sidebar_can_open_the_library_and_Settings_is_not_confused_with_it()
    {
        var (main, _, library) = NewMain();

        main.ShowLibraryCommand.Execute(null);
        Assert.Same(library, main.CurrentPage);
        Assert.True(main.HasLibrary);

        main.ShowSettingsCommand.Execute(null);
        Assert.True(main.IsSettingsSelected);
        Assert.False(main.IsLibrarySelected);
        Assert.False(main.IsHomeSelected);
    }

    [Fact]
    public async Task Opening_the_library_reads_it_fresh_each_time()
    {
        var clock = new Clockwork();
        var history = new InMemoryLearnHistory(() => clock.Now);
        var settings = new MemorySettings(new AppSettings());
        var llm = new FakeLlmService();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0);
        var library = new LibraryViewModel(history);
        var main = new MainViewModel(
            new HomeViewModel(new InMemoryProfileRepository(), new StubExtractor(), new ScriptedDialogs(), bank),
            new SettingsViewModel(settings, llm, bank, new ScriptedDialogs()), new LearnViewModel(llm, prompts, settings, bank, () => 0.0), settings, library);

        main.ShowLibraryCommand.Execute(null);
        Assert.Empty(library.Rows);

        await history.RecordAsync(Entry("What is a struct?"));
        main.ShowLibraryCommand.Execute(null);
        for (var i = 0; i < 100 && library.Rows.Count == 0; i++) await Task.Delay(10);

        Assert.Single(library.Rows);
    }

    [Fact]
    public void Without_a_library_the_sidebar_item_is_hidden_and_opening_it_does_nothing()
    {
        var (main, home, _) = NewMain(withLibrary: false);

        Assert.False(main.HasLibrary);
        main.ShowLibraryCommand.Execute(null);
        home.OpenLibraryCommand.Execute(null);

        Assert.Same(home, main.CurrentPage);
        Assert.False(main.IsLibrarySelected);
    }

    [Fact]
    public async Task Home_has_a_Revisit_card_beside_Practice_that_is_usable_and_opens_the_library()
    {
        var home = new HomeViewModel(new InMemoryProfileRepository(), new StubExtractor(), new ScriptedDialogs());
        await home.InitializeAsync();
        var requested = 0;
        home.LibraryRequested += () => requested++;
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = home };
            Layout(view);

            var cards = Descendants<RadioButton>(view).Where(r => r.Template.FindName("Card", r) is Border).ToList();
            Assert.Equal(4, cards.Count);

            var order = cards.Select(c => Descendants<TextBlock>(c).Select(t => t.Text).First(t => t.Length > 0 && t != "" && t.All(ch => ch < 0xE000))).ToList();
            Assert.Equal(["Mock Interview", "Learn", "Practice", "Revisit"], order);

            var revisit = cards[3];
            Assert.True(revisit.IsEnabled);
            Assert.NotNull(revisit.Command);
            revisit.Command!.Execute(null);
        });

        Assert.Equal(1, requested);
        Assert.Equal("", WpfHost.TakeBindingErrors());
    }
}
