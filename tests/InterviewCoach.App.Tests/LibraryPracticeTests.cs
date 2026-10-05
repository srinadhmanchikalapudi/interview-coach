using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using InterviewCoach.App.ViewModels;
using InterviewCoach.App.Views;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>The library also holds the answers given in Practice, with the feedback on them.</summary>
public class LibraryPracticeTests
{
    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private static CoachOutput Model(string text = "A struct is a value type.", params FollowUp[] followUps) => new()
    {
        WhatTheyreTesting = "the signal", ModelAnswer = text, Shape = "Direct answer → why", FollowUps = [.. followUps],
    };

    private static LearnHistoryEntry Learned(string question, string type = "technical_concept", string answer = "A struct is a value type.", params FollowUp[] followUps) => new()
    {
        Question = question, QuestionType = type, Technology = type == "technical_concept" ? "C#" : null, IsGeneral = true, Coach = Model(answer, followUps),
    };

    private static PracticeRecord Practised(
        string question, string answer = "My own answer.", string type = "technical_concept", int attempt = 1, string? parent = null,
        int seconds = 0, string? quote = "My own", params FollowUp[] followUps) => new()
    {
        Question = question, QuestionType = type, Technology = type == "technical_concept" ? "C#" : null, ProfileName = "Orders platform",
        IsFollowUp = parent is not null, ParentQuestion = parent, AttemptNumber = attempt, AnswerText = answer, DurationSeconds = seconds,
        WordCount = AnswerLength.CountWords(answer),
        Coach = new CoachOutput
        {
            WhatTheyreTesting = "the signal",
            Feedback = [new FeedbackPoint { Kind = "strength", Point = "You were clear.", Quote = quote }, new FeedbackPoint { Kind = "fix", Point = "Add a number." }],
            ModelAnswer = "A struct is a value type.", Shape = "A → B", Delivery = "A good length.", FollowUps = [.. followUps],
        },
    };

    private static async Task<(LibraryViewModel Vm, InMemoryLearnHistory Learn, InMemoryPracticeHistory Practice, Clockwork Clock, ScriptedDialogs Dialogs)> NewAsync(
        Func<Clockwork, InMemoryLearnHistory, InMemoryPracticeHistory, Task>? seed = null)
    {
        var clock = new Clockwork();
        var learn = new InMemoryLearnHistory(() => clock.Now);
        var practice = new InMemoryPracticeHistory(() => clock.Now);
        if (seed is not null) await seed(clock, learn, practice);
        var dialogs = new ScriptedDialogs();
        var vm = new LibraryViewModel(learn, dialogs, () => clock.Now.ToLocalTime(), practice);
        await vm.LoadAsync();
        return (vm, learn, practice, clock, dialogs);
    }

    // Two learned questions and two practice answers, seen at four different times (the first recorded is the oldest).
    private static async Task SeedAsync(Clockwork clock, InMemoryLearnHistory learn, InMemoryPracticeHistory practice)
    {
        await learn.RecordAsync(Learned("What is a struct?"));
        clock.Advance(TimeSpan.FromMinutes(10));
        await learn.RecordAsync(Learned("Tell me about a hard deadline.", "behavioral", "We cut scope and shipped the core."));
        clock.Advance(TimeSpan.FromMinutes(10));
        await practice.RecordAsync(Practised("What is a class?", "A class is a reference type and I use it for identity.", seconds: 139));
        clock.Advance(TimeSpan.FromMinutes(10));
        await practice.RecordAsync(Practised("Tell me about a hard deadline.", "I negotiated the date.", "behavioral", attempt: 2));
    }

    private static string[] Questions(LibraryViewModel vm) => vm.Rows.Select(r => r.Question).ToArray();

    // ---- listing

    [Fact]
    public async Task Practice_answers_are_listed_with_what_was_learned_and_the_newest_comes_first()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        Assert.Equal(["Tell me about a hard deadline.", "What is a class?", "Tell me about a hard deadline.", "What is a struct?"], Questions(vm));
        Assert.Equal([true, true, false, false], vm.Rows.Select(r => r.IsPractice).ToArray());
        Assert.Equal("4 entries", vm.CountText);
    }

    [Fact]
    public async Task A_practice_row_says_it_is_your_answer_and_which_attempt()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        Assert.Equal("Your answer, attempt 2", vm.Rows[0].AnswerKind);
        Assert.Equal("Your answer", vm.Rows[1].AnswerKind);
        Assert.Equal("General answer", vm.Rows[2].AnswerKind);
        Assert.Equal("General answer", vm.Rows[3].AnswerKind);
        Assert.Equal("", vm.Rows[0].Seen);                       // "seen N times" belongs to Learn entries
    }

    [Fact]
    public async Task Without_a_practice_history_the_library_works_as_it_did_and_shows_no_kind_chips()
    {
        var learn = new InMemoryLearnHistory();
        await learn.RecordAsync(Learned("What is a struct?"));
        var vm = new LibraryViewModel(learn);

        await vm.LoadAsync();

        Assert.Single(vm.Rows);
        Assert.False(vm.HasKindFilter);
    }

    // ---- the Learned / Practised chips

    [Fact]
    public async Task The_kind_chips_appear_only_when_something_was_practised_and_count_each_kind()
    {
        var (vm, learn, _, _, _) = await NewAsync(async (c, l, p) => await l.RecordAsync(Learned("Only learned?")));
        Assert.False(vm.HasKindFilter);

        var (both, _, _, _, _) = await NewAsync(SeedAsync);

        Assert.True(both.HasKindFilter);
        Assert.Equal(["All  4", "Learned  2", "Practised  2"], both.KindFilters.Select(f => f.Text).ToArray());
        Assert.Single(both.KindFilters, f => f.IsSelected);
        Assert.True(both.KindFilters[0].IsSelected);
        _ = learn;
    }

    [Fact]
    public async Task Choosing_Practised_shows_only_practice_answers_and_Learned_only_learned_questions()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.KindFilters.Single(f => f.TypeId == "practice").IsSelected = true;
        Assert.Equal(["Tell me about a hard deadline.", "What is a class?"], Questions(vm));
        Assert.All(vm.Rows, r => Assert.True(r.IsPractice));
        Assert.Equal("2 of 4 entries", vm.CountText);

        vm.KindFilters.Single(f => f.TypeId == "learn").IsSelected = true;
        Assert.Equal(["Tell me about a hard deadline.", "What is a struct?"], Questions(vm));
        Assert.All(vm.Rows, r => Assert.False(r.IsPractice));

        vm.KindFilters[0].IsSelected = true;
        Assert.Equal(4, vm.Rows.Count);
    }

    [Fact]
    public async Task The_kind_and_the_question_type_work_together()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.KindFilters.Single(f => f.TypeId == "practice").IsSelected = true;
        vm.TypeFilters.Single(f => f.TypeId == "behavioral").IsSelected = true;

        Assert.Equal(["Tell me about a hard deadline."], Questions(vm));
        Assert.True(vm.Rows[0].IsPractice);
        Assert.Equal(["All  4", "Technical concept  2", "Behavioral  2"], vm.TypeFilters.Select(f => f.Text).ToArray());   // practice answers count for their type
    }

    [Fact]
    public async Task Search_also_looks_in_what_you_wrote()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.Search = "negotiated";

        Assert.Equal(["Tell me about a hard deadline."], Questions(vm));
        Assert.True(vm.Rows[0].IsPractice);
    }

    // ---- opening a practice answer

    [Fact]
    public async Task Opening_a_practice_answer_shows_what_you_wrote_and_the_feedback_on_it()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.Selected = vm.Rows.Single(r => r.Question == "What is a class?");

        Assert.True(vm.DetailIsPractice);
        Assert.True(vm.HasDetailAnswer);
        Assert.Equal("A class is a reference type and I use it for identity.", vm.DetailAnswer);
        Assert.Equal("12 words  ·  2:19", vm.DetailAnswerMeta);
        Assert.True(vm.Detail!.HasFeedback);                      // unlike a learned question, the feedback is kept
        Assert.Equal("You were clear.", vm.Detail.Feedback[0].Point);
        Assert.Equal("My own", vm.Detail.Feedback[0].Quote);
        Assert.True(vm.Detail.HasDelivery);
        Assert.Equal("Technical concept", vm.DetailTypeLabel);
        Assert.StartsWith("Practised ", vm.DetailMeta);
        Assert.Contains("Feedback for Orders platform", vm.DetailMeta);
    }

    [Fact]
    public async Task A_later_try_says_which_attempt_it_was()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.Selected = vm.Rows[0];

        Assert.Contains("Attempt 2", vm.DetailMeta);
    }

    [Fact]
    public async Task A_learned_question_has_no_answer_card_and_no_feedback()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);

        vm.Selected = vm.Rows.Single(r => r.Question == "What is a struct?");

        Assert.False(vm.DetailIsPractice);
        Assert.False(vm.HasDetailAnswer);
        Assert.False(vm.Detail!.HasFeedback);
        Assert.StartsWith("Last seen ", vm.DetailMeta);
    }

    [Fact]
    public async Task A_quote_saved_as_the_word_null_is_not_shown()
    {
        var (vm, _, _, _, _) = await NewAsync(async (c, l, p) => await p.RecordAsync(Practised("What is a struct?", quote: "null")));

        vm.Selected = vm.Rows[0];

        Assert.False(vm.Detail!.Feedback[0].HasQuote);
    }

    // ---- removing

    [Fact]
    public async Task Removing_a_practice_answer_removes_only_that_answer()
    {
        var (vm, learn, practice, _, dialogs) = await NewAsync(SeedAsync);
        vm.Selected = vm.Rows.Single(r => r.IsPractice && r.Question == "What is a class?");

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(await practice.ListAsync());
        Assert.Equal(2, (await learn.ListAsync()).Count);        // what was learned is untouched, including the same question
        Assert.Equal("Remove from library", dialogs.Confirmations.Single());
        Assert.Equal(3, vm.Rows.Count);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public async Task Removing_a_learned_question_leaves_the_practice_answers_to_the_same_question()
    {
        var (vm, learn, practice, _, _) = await NewAsync(SeedAsync);
        vm.Selected = vm.Rows.Single(r => !r.IsPractice && r.Question == "Tell me about a hard deadline.");

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(await learn.ListAsync());
        Assert.Equal(2, (await practice.ListAsync()).Count);
    }

    // ---- follow-ups

    private static readonly FollowUp WhyThat = new() { Question = "Why that approach?", Hint = "Name one alternative." };

    [Fact]
    public async Task A_follow_up_answered_in_practice_opens_that_saved_answer()
    {
        var (vm, _, _, _, _) = await NewAsync(async (clock, learn, practice) =>
        {
            await practice.RecordAsync(Practised("What is a struct?", "It is a value type.", followUps: WhyThat));
            clock.Advance(TimeSpan.FromMinutes(1));
            await practice.RecordAsync(Practised("Why that approach?", "Copying is cheap for small values.", parent: "What is a struct?"));
        });
        vm.Selected = vm.Rows.Single(r => !r.IsFollowUp);

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.Equal("Why that approach?", vm.Selected?.Question);
        Assert.True(vm.DetailIsFollowUp);
        Assert.Equal("Copying is cheap for small values.", vm.DetailAnswer);
    }

    [Fact]
    public async Task A_follow_up_with_no_saved_answer_in_either_mode_says_so()
    {
        var (vm, _, _, _, _) = await NewAsync(async (c, l, p) => await p.RecordAsync(Practised("What is a struct?", followUps: WhyThat)));
        vm.Selected = vm.Rows[0];

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.True(vm.HasMessage);
        Assert.Contains("not opened this follow-up in Learn or Practice", vm.Message);
    }

    [Fact]
    public async Task A_follow_up_hidden_by_a_filter_is_shown_by_clearing_every_filter()
    {
        var (vm, _, _, _, _) = await NewAsync(async (clock, learn, practice) =>
        {
            await learn.RecordAsync(Learned("What is a struct?", followUps: WhyThat));
            clock.Advance(TimeSpan.FromMinutes(1));
            await practice.RecordAsync(Practised("Why that approach?", parent: "What is a struct?"));
        });
        vm.Selected = vm.Rows.Single(r => !r.IsPractice);
        vm.KindFilters.Single(f => f.TypeId == "learn").IsSelected = true;   // hides the practice answer

        vm.Detail!.FollowUps.Single().OpenCommand.Execute(null);

        Assert.Equal("Why that approach?", vm.Selected?.Question);
        Assert.True(vm.KindFilters[0].IsSelected);
    }

    // ---- Practice feeds it

    [Fact]
    public async Task A_Practice_session_adds_the_answer_to_the_library()
    {
        var practice = new InMemoryPracticeHistory();
        var stub = new StubLlm();
        var vm = new PracticeViewModel(stub, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), new MemorySettings(new AppSettings()),
            null, () => 0.0, null, practice);
        vm.Begin(new LearnSessionRequest(Samples.CompleteProfile(), [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        vm.AnswerText = "We cut p99 to 80ms by caching.";

        await vm.SubmitCommand.ExecuteAsync(null);

        var record = Assert.Single(await practice.ListAsync());
        Assert.Equal("We cut p99 to 80ms by caching.", record.AnswerText);
        Assert.Equal("Practice question 1?", record.Question);
        Assert.Equal(2, record.Coach.Feedback.Count);

        var library = new LibraryViewModel(new InMemoryLearnHistory(), null, null, practice);
        await library.LoadAsync();
        Assert.Equal(["Practice question 1?"], library.Rows.Select(r => r.Question).ToArray());
        Assert.True(library.Rows[0].IsPractice);
    }

    // ---- the real view

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(1100, 1000));
            view.Arrange(new Rect(0, 0, 1100, 1000));
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
    public async Task The_view_shows_the_kind_chips_a_practice_tag_and_your_answer_and_binds_cleanly()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new LibraryView { DataContext = vm };
            vm.Selected = vm.Rows.Single(r => r.Question == "What is a class?");
            Layout(view);

            var chips = Descendants<RadioButton>(view).Where(Shown).Select(r => r.Content as string).ToList();
            Assert.Contains("Learned  2", chips);
            Assert.Contains("Practised  2", chips);

            var texts = Texts(view);
            Assert.Contains("Practice", texts);                                                       // the tag, in the list and on the open entry
            Assert.Contains("YOUR ANSWER", texts);
            Assert.Contains("A class is a reference type and I use it for identity.", texts);
            Assert.Contains(texts, t => t.StartsWith("HOW YOUR ANSWER"));                              // the feedback card
            Assert.Contains("12 words  ·  2:19", texts);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task A_learned_question_shows_no_answer_card_in_the_view()
    {
        var (vm, _, _, _, _) = await NewAsync(SeedAsync);
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new LibraryView { DataContext = vm };
            vm.Selected = vm.Rows.Single(r => r.Question == "What is a struct?");
            Layout(view);

            var texts = Texts(view);
            Assert.DoesNotContain("YOUR ANSWER", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("HOW YOUR ANSWER"));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }
}
