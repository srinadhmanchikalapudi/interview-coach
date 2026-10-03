using System.IO;
using System.Windows;
using System.Windows.Controls;
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

/// <summary>History of mock interviews: listing, opening a debrief, resuming a round that was left, the Home notice, and the pages around them.</summary>
public class HistoryTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = s;
        public void Save(AppSettings settings) { Current = settings; Changed?.Invoke(this, EventArgs.Empty); }
        public event EventHandler? Changed;
    }

    private sealed class ThrowingHistory : IMockHistory
    {
        public Task<int> AddAsync(MockRecord record, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task UpdateAsync(MockRecord record, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default) => throw new IOException("disk gone");
        public Task DeleteAsync(int id, CancellationToken ct = default) => throw new IOException("disk gone");
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private sealed class Clockwork
    {
        public DateTime Now { get; set; } = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    /// <summary>Runs a demo interview into the history: to the end (with its debrief), or left midway.</summary>
    private static async Task<MockRecord> RecordInterviewAsync(InMemoryMockHistory history, CandidateProfile profile, bool finish, Clockwork? clock = null)
    {
        clock ??= new Clockwork();
        var engine = new MockEngine(new FakeLlmService(), Prompts, () => clock.Now, history);
        await engine.StartAsync(profile, RoundType.Technical, 15);
        for (var guard = 0; guard < 20 && engine.Phase != MockPhase.Done; guard++)
        {
            await engine.FinishedSpeakingAsync();
            if (engine.Phase == MockPhase.CandidateAnswering)
            {
                clock.Advance(40);
                await engine.SubmitAnswerAsync("A sample answer with enough words to count.", "typed", 30);
                if (!finish)
                {
                    engine.Cancel();
                    break;
                }
            }
        }
        await engine.WhenCoachedAsync();
        return (await history.ListAsync()).OrderByDescending(r => r.Id).First();
    }

    private sealed class World
    {
        public InMemoryProfileRepository Profiles { get; } = new();
        public InMemoryMockHistory History { get; } = new();
        public ScriptedDialogs Dialogs { get; } = new();
        public CandidateProfile Profile { get; set; } = null!;
        public HistoryViewModel Vm { get; set; } = null!;
    }

    private static async Task<World> NewWorldAsync()
    {
        var w = new World();
        await w.Profiles.SaveAsync(Samples.CompleteProfile());
        w.Profile = (await w.Profiles.ListAsync())[0];
        w.Vm = new HistoryViewModel(w.History, w.Profiles, w.Dialogs, new MemorySettings(new AppSettings()));
        return w;
    }

    // ---- the list

    [Fact]
    public async Task With_nothing_recorded_the_list_is_empty_and_says_so()
    {
        var w = await NewWorldAsync();

        await w.Vm.LoadAsync();

        Assert.True(w.Vm.IsEmpty);
        Assert.False(w.Vm.HasRows);
        Assert.Equal("", w.Vm.CountText);
    }

    [Fact]
    public async Task Interviews_are_listed_newest_first_with_what_state_each_is_in()
    {
        var w = await NewWorldAsync();
        var clock = new Clockwork();
        await RecordInterviewAsync(w.History, w.Profile, finish: true, clock);
        clock.Advance(3600);
        await RecordInterviewAsync(w.History, w.Profile, finish: false, clock);
        clock.Advance(3600);
        var third = await RecordInterviewAsync(w.History, w.Profile, finish: true, clock);
        third.DebriefJson = null;                                           // this one never got its debrief
        third.HireSignal = null;

        await w.Vm.LoadAsync();

        Assert.Equal(3, w.Vm.Rows.Count);
        Assert.Equal([HistoryKind.NeedsDebrief, HistoryKind.Unfinished, HistoryKind.Finished], w.Vm.Rows.Select(r => r.Kind));
        Assert.Equal("3 mock interviews", w.Vm.CountText);
        Assert.False(w.Vm.IsEmpty);
        var finished = w.Vm.Rows[2];
        Assert.Equal("Senior Backend Engineer", finished.Title);
        Assert.StartsWith("Acme backend · ", finished.Subtitle);
        Assert.Equal("Technical · 15 min", finished.RoundText);
        Assert.Equal("Lean yes", finished.StatusText);
        Assert.True(finished.OpenCommand.CanExecute(null));
        Assert.False(finished.ResumeCommand.CanExecute(null));
        Assert.Equal("Unfinished", w.Vm.Rows[1].StatusText);
        Assert.Equal("Resume", w.Vm.Rows[1].ResumeLabel);
        Assert.True(w.Vm.Rows[1].ResumeCommand.CanExecute(null));
        Assert.False(w.Vm.Rows[1].OpenCommand.CanExecute(null));
        Assert.Equal("No debrief yet", w.Vm.Rows[0].StatusText);
        Assert.Equal("Write the debrief", w.Vm.Rows[0].ResumeLabel);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_says_so_instead_of_failing()
    {
        var vm = new HistoryViewModel(new ThrowingHistory(), new InMemoryProfileRepository());

        await vm.LoadAsync();

        Assert.True(vm.HasError);
        Assert.Contains("disk gone", vm.Error);
        Assert.False(vm.IsEmpty);                                           // not "nothing yet": something is wrong
        Assert.False(vm.IsLoading);
    }

    // ---- opening a finished interview

    [Fact]
    public async Task Opening_a_finished_interview_shows_its_stored_debrief_with_the_coaching_and_a_Back_to_History_button()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: true);
        await w.Vm.LoadAsync();
        DebriefViewModel? opened = null;
        w.Vm.OpenRequested += d => opened = d;

        await w.Vm.Rows[0].OpenCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.Equal("Lean yes", opened.SignalLabel);
        Assert.Contains("sample debrief", opened.Summary);
        Assert.Equal(2, opened.Ratings.Count);
        Assert.Equal(2, opened.Threads.Count);
        Assert.All(opened.Threads, t => Assert.Equal(ThreadStatus.Done, t.Status));
        Assert.NotNull(opened.Threads[0].Coach);
        Assert.False(opened.IsStillCoaching);
        Assert.Equal("Back to History", opened.BackLabel);
        Assert.False(opened.ProfileMissing);
        Assert.True(opened.Threads[0].PracticeCommand.CanExecute(null));
        Assert.False(opened.Threads[0].CanRetry);                           // only a live interview can ask the coach again
        Assert.StartsWith("Senior Backend Engineer · Technical · 15 min planned", opened.RoleLine);
    }

    [Fact]
    public async Task A_stored_debrief_whose_profile_was_deleted_opens_but_cannot_be_practised()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: true);
        await w.Profiles.DeleteAsync(w.Profile.Id);
        await w.Vm.LoadAsync();
        DebriefViewModel? opened = null;
        w.Vm.OpenRequested += d => opened = d;

        await w.Vm.Rows[0].OpenCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.True(opened.ProfileMissing);
        Assert.False(opened.Threads[0].CanPractise);
        Assert.False(opened.Threads[0].PracticeCommand.CanExecute(null));
        Assert.Empty(opened.Threads[0].Coach!.FollowUps);                   // no buttons that would go nowhere
        var practised = false;
        opened.PracticeRequested += (_, _) => practised = true;
        opened.Threads[0].PracticeCommand.Execute(null);
        Assert.False(practised);
    }

    [Fact]
    public async Task A_stored_debrief_can_be_practised_with_the_profile_as_saved_now()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: true);
        await w.Vm.LoadAsync();
        DebriefViewModel? opened = null;
        w.Vm.OpenRequested += d => opened = d;
        await w.Vm.Rows[0].OpenCommand.ExecuteAsync(null);
        LearnSessionRequest? request = null;
        LearnItem? item = null;
        opened!.PracticeRequested += (r, i) => { request = r; item = i; };

        opened.Threads[0].PracticeCommand.Execute(null);

        Assert.Equal(w.Profile.Id, request!.Profile.Id);
        Assert.Equal(opened.Threads[0].Question, item!.Question);
    }

    [Fact]
    public async Task A_debrief_that_cannot_be_read_is_explained_and_not_opened()
    {
        var w = await NewWorldAsync();
        var record = await RecordInterviewAsync(w.History, w.Profile, finish: true);
        record.DebriefJson = "{broken";
        await w.Vm.LoadAsync();
        var raised = false;
        w.Vm.OpenRequested += _ => raised = true;

        await w.Vm.Rows[0].OpenCommand.ExecuteAsync(null);

        Assert.False(raised);
        Assert.Contains("cannot be opened", w.Vm.Notice);
    }

    // ---- resuming

    [Fact]
    public async Task Resuming_hands_over_the_round_length_and_role_type_with_the_profile_as_saved_now()
    {
        var w = await NewWorldAsync();
        var record = await RecordInterviewAsync(w.History, w.Profile, finish: false);
        await w.Vm.LoadAsync();
        MockSessionRequest? request = null;
        MockRecord? resumed = null;
        w.Vm.ResumeRequested += (r, rec) => { request = r; resumed = rec; };

        await w.Vm.Rows[0].ResumeCommand.ExecuteAsync(null);

        Assert.Same(record, resumed);
        Assert.Equal(RoundType.Technical, request!.Round);
        Assert.Equal(15, request.DurationMinutes);
        Assert.Equal(EmploymentType.FullTime, request.Employment);
        Assert.Equal(w.Profile.Id, request.Profile.Id);
        Assert.True(request.ShowQuestionText);
    }

    [Fact]
    public async Task A_round_whose_profile_was_deleted_cannot_be_resumed_and_the_screen_says_why()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: false);
        await w.Profiles.DeleteAsync(w.Profile.Id);
        await w.Vm.LoadAsync();
        var raised = false;
        w.Vm.ResumeRequested += (_, _) => raised = true;

        await w.Vm.Rows[0].ResumeCommand.ExecuteAsync(null);

        Assert.False(raised);
        Assert.Contains("profile it was for has been deleted", w.Vm.Notice);
        Assert.True(w.Vm.HasNotice);
    }

    [Fact]
    public async Task A_record_from_before_profiles_were_stored_is_matched_to_its_profile_by_name()
    {
        var w = await NewWorldAsync();
        var record = await RecordInterviewAsync(w.History, w.Profile, finish: false);
        w.History.Replace(record, new MockRecord
        {
            Id = record.Id, ProfileId = 0, ProfileName = record.ProfileName, JobRole = record.JobRole, RoundType = record.RoundType, Employment = "",
            DurationMinutes = record.DurationMinutes, StartedAt = record.StartedAt, ElapsedSeconds = record.ElapsedSeconds, Finished = false,
            PlanJson = record.PlanJson, TurnsJson = record.TurnsJson,
        });
        await w.Vm.LoadAsync();
        MockSessionRequest? request = null;
        w.Vm.ResumeRequested += (r, _) => request = r;

        await w.Vm.Rows[0].ResumeCommand.ExecuteAsync(null);

        Assert.NotNull(request);
        Assert.Equal(EmploymentType.FullTime, request.Employment);          // an empty role type reads as full-time
    }

    // ---- deleting

    [Fact]
    public async Task Deleting_asks_first_and_declining_keeps_the_interview()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: true);
        await w.Vm.LoadAsync();
        w.Dialogs.ConfirmAnswer = false;

        await w.Vm.Rows[0].DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Delete interview"], w.Dialogs.Confirmations);
        Assert.Single(w.Vm.Rows);
        Assert.Single(await w.History.ListAsync());
    }

    [Fact]
    public async Task Confirming_the_delete_removes_it_and_refreshes_the_list()
    {
        var w = await NewWorldAsync();
        await RecordInterviewAsync(w.History, w.Profile, finish: true);
        await w.Vm.LoadAsync();

        await w.Vm.Rows[0].DeleteCommand.ExecuteAsync(null);

        Assert.Empty(w.Vm.Rows);
        Assert.Empty(await w.History.ListAsync());
        Assert.Equal("Interview deleted.", w.Vm.Notice);
        Assert.True(w.Vm.IsEmpty);
    }

    [Fact]
    public async Task The_Library_button_asks_for_the_Library()
    {
        var w = await NewWorldAsync();
        var asked = false;
        w.Vm.LibraryRequested += () => asked = true;

        w.Vm.OpenLibraryCommand.Execute(null);

        Assert.True(asked);
    }

    // ---- the notice on Home

    private static async Task<(HomeViewModel Home, InMemoryMockHistory History, ScriptedDialogs Dialogs, CandidateProfile Profile, InMemoryProfileRepository Profiles)> NewHomeAsync()
    {
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var history = new InMemoryMockHistory();
        var dialogs = new ScriptedDialogs();
        var home = new HomeViewModel(repo, new StubExtractor(), dialogs, null, new MemorySettings(new AppSettings()), history);
        await home.InitializeAsync();
        return (home, history, dialogs, (await repo.ListAsync())[0], repo);
    }

    [Fact]
    public async Task Home_offers_to_pick_up_a_round_that_was_left_and_not_when_there_is_none()
    {
        var (home, history, _, profile, _) = await NewHomeAsync();
        Assert.False(home.HasPendingMock);

        await RecordInterviewAsync(history, profile, finish: false);
        await home.RefreshMockStatusAsync();

        Assert.True(home.HasPendingMock);
        Assert.Equal("Resume", home.PendingMockResumeLabel);
        Assert.StartsWith("You left a technical interview for Senior Backend Engineer unfinished (", home.PendingMockText);
        Assert.True(home.CanResumePendingMock);
    }

    [Fact]
    public async Task A_finished_interview_does_not_trigger_the_notice_but_one_without_a_debrief_does()
    {
        var (home, history, _, profile, _) = await NewHomeAsync();
        var finished = await RecordInterviewAsync(history, profile, finish: true);
        await home.RefreshMockStatusAsync();
        Assert.False(home.HasPendingMock);

        finished.DebriefJson = null;
        await home.RefreshMockStatusAsync();

        Assert.True(home.HasPendingMock);
        Assert.Equal("Write the debrief", home.PendingMockResumeLabel);
        Assert.Contains("ended before its debrief was written", home.PendingMockText);
    }

    [Fact]
    public async Task Resuming_from_Home_raises_the_request_and_clears_the_notice()
    {
        var (home, history, _, profile, _) = await NewHomeAsync();
        var record = await RecordInterviewAsync(history, profile, finish: false);
        await home.RefreshMockStatusAsync();
        MockSessionRequest? request = null;
        MockRecord? resumed = null;
        home.MockResumeRequested += (r, rec) => { request = r; resumed = rec; };

        home.ResumePendingMockCommand.Execute(null);

        Assert.Same(record, resumed);
        Assert.Equal(RoundType.Technical, request!.Round);
        Assert.False(home.HasPendingMock);
    }

    [Fact]
    public async Task Discarding_asks_first_and_then_deletes_the_interview()
    {
        var (home, history, dialogs, profile, _) = await NewHomeAsync();
        await RecordInterviewAsync(history, profile, finish: false);
        await home.RefreshMockStatusAsync();
        dialogs.ConfirmAnswer = false;

        await home.DiscardPendingMockCommand.ExecuteAsync(null);
        Assert.True(home.HasPendingMock);
        Assert.Single(await history.ListAsync());

        dialogs.ConfirmAnswer = true;
        await home.DiscardPendingMockCommand.ExecuteAsync(null);

        Assert.False(home.HasPendingMock);
        Assert.Empty(await history.ListAsync());
    }

    [Fact]
    public async Task When_the_profile_of_the_left_round_is_gone_the_notice_says_so_and_resume_is_off()
    {
        var (home, history, _, profile, repo) = await NewHomeAsync();
        await RecordInterviewAsync(history, profile, finish: false);
        await repo.DeleteAsync(profile.Id);
        await home.InitializeAsync();

        Assert.True(home.HasPendingMock);
        Assert.False(home.CanResumePendingMock);
        Assert.Contains("has been deleted", home.PendingMockText);
        var raised = false;
        home.MockResumeRequested += (_, _) => raised = true;
        home.ResumePendingMockCommand.Execute(null);
        Assert.False(raised);
    }

    [Fact]
    public async Task A_history_that_cannot_be_read_just_leaves_the_notice_off()
    {
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var home = new HomeViewModel(repo, new StubExtractor(), new ScriptedDialogs(), null, new MemorySettings(new AppSettings()), new ThrowingHistory());

        await home.InitializeAsync();

        Assert.False(home.HasPendingMock);
    }

    // ---- the pages around it

    private sealed class Pages
    {
        public MainViewModel Main { get; init; } = null!;
        public HomeViewModel Home { get; init; } = null!;
        public HistoryViewModel History { get; init; } = null!;
        public MockViewModel Mock { get; init; } = null!;
        public PracticeViewModel Practice { get; init; } = null!;
        public InMemoryMockHistory Store { get; init; } = null!;
        public TestSpeech Speech { get; init; } = null!;
        public MemorySettings Settings { get; init; } = null!;
    }

    private static async Task<Pages> NewPagesAsync(AppSettings? settings = null)
    {
        var s = new MemorySettings(settings ?? new AppSettings { AutoListen = false });
        var llm = new FakeLlmService();
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0);
        var repo = new InMemoryProfileRepository();
        await repo.SaveAsync(Samples.CompleteProfile());
        var store = new InMemoryMockHistory();
        var dialogs = new ScriptedDialogs();
        var speech = new TestSpeech();
        var home = new HomeViewModel(repo, new StubExtractor(), dialogs, bank, s, store);
        await home.InitializeAsync();
        var learn = new LearnViewModel(llm, Prompts, s, bank, () => 0.0);
        var practice = new PracticeViewModel(llm, Prompts, s, bank, () => 0.0, null, null, speech);
        var mock = new MockViewModel(llm, Prompts, s, speech, dialogs, store);
        var history = new HistoryViewModel(store, repo, dialogs, s);
        var main = new MainViewModel(home, new SettingsViewModel(s, llm, bank, dialogs), learn, s, null, practice, null, mock, history);
        return new Pages { Main = main, Home = home, History = history, Mock = mock, Practice = practice, Store = store, Speech = speech, Settings = s };
    }

    private static async Task AnswerToTheEndAsync(MockViewModel mock, Func<bool> done)
    {
        for (var guard = 0; !done() && guard < 20; guard++)
        {
            Assert.NotEqual(MockPhase.Failed, mock.Phase);
            if (mock.IsAnswering)
            {
                mock.AnswerText = "A sample answer with enough words to count.";
                await mock.SubmitCommand.ExecuteAsync(null);
            }
        }
        Assert.True(done());
    }

    [Fact]
    public async Task The_sidebar_opens_History_and_the_list_is_read_each_time()
    {
        var p = await NewPagesAsync();
        Assert.True(p.Main.HasHistory);
        Assert.True(p.Main.IsHomeSelected);

        p.Main.ShowHistoryCommand.Execute(null);

        Assert.Same(p.History, p.Main.CurrentPage);
        Assert.True(p.Main.IsHistorySelected);
        Assert.False(p.Main.IsHomeSelected);
        Assert.True(p.History.IsEmpty);

        await RecordInterviewAsync(p.Store, Samples.CompleteProfile(), finish: true);
        p.Main.ShowHomeCommand.Execute(null);
        p.Main.ShowHistoryCommand.Execute(null);
        await Task.Delay(50);

        Assert.Single(p.History.Rows);
    }

    [Fact]
    public async Task Leaving_an_interview_keeps_it_and_Home_offers_it_back_and_resuming_continues_with_the_conversation_so_far()
    {
        var p = await NewPagesAsync();
        p.Home.SelectMockCommand.Execute(null);
        p.Home.StartCommand.Execute(null);
        p.Mock.AnswerText = "Good, thanks.";
        await p.Mock.SubmitCommand.ExecuteAsync(null);
        var lineBefore = p.Mock.InterviewerLine;
        Assert.Equal(3, p.Mock.Conversation.Count);

        p.Main.ShowSettingsCommand.Execute(null);                          // the user goes elsewhere
        p.Main.ShowHomeCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(p.Home.HasPendingMock);
        var record = Assert.Single(await p.Store.ListAsync());
        Assert.True(record.IsUnfinished);

        p.Home.ResumePendingMockCommand.Execute(null);

        Assert.Same(p.Mock, p.Main.CurrentPage);
        Assert.Equal(3, p.Mock.Conversation.Count);                        // what was said is back on screen
        Assert.Equal(lineBefore, p.Mock.InterviewerLine);
        Assert.Equal(MockPhase.CandidateAnswering, p.Mock.Phase);          // the line was said again and it is the candidate's turn
        Assert.Equal(lineBefore, p.Speech.Voice.Spoken[^1]);

        await AnswerToTheEndAsync(p.Mock, () => p.Main.CurrentPage is DebriefViewModel);

        Assert.Single(await p.Store.ListAsync());                          // the same row, not a second one
        Assert.True(record.Finished);
        Assert.Equal("lean_yes", record.HireSignal);
    }

    [Fact]
    public async Task Resuming_from_History_works_the_same_way()
    {
        var p = await NewPagesAsync();
        p.Home.SelectMockCommand.Execute(null);
        p.Home.StartCommand.Execute(null);
        p.Mock.AnswerText = "Good, thanks.";
        await p.Mock.SubmitCommand.ExecuteAsync(null);
        p.Main.ShowHistoryCommand.Execute(null);                            // leaving the interview ends its voice but keeps it
        await Task.Delay(50);
        var row = Assert.Single(p.History.Rows);
        Assert.Equal(HistoryKind.Unfinished, row.Kind);

        await row.ResumeCommand.ExecuteAsync(null);

        Assert.Same(p.Mock, p.Main.CurrentPage);
        Assert.Equal(3, p.Mock.Conversation.Count);
        Assert.True(p.Mock.CanEnd);
    }

    [Fact]
    public async Task Opening_a_debrief_from_History_goes_Back_to_History_and_Practice_from_it_returns_to_the_debrief()
    {
        var p = await NewPagesAsync();
        // an interview run through the app, so its profile exists
        p.Home.SelectMockCommand.Execute(null);
        p.Home.StartCommand.Execute(null);
        await AnswerToTheEndAsync(p.Mock, () => p.Main.CurrentPage is DebriefViewModel);
        p.Main.ShowHistoryCommand.Execute(null);
        await Task.Delay(50);

        await p.History.Rows[0].OpenCommand.ExecuteAsync(null);

        var debrief = Assert.IsType<DebriefViewModel>(p.Main.CurrentPage);
        Assert.True(p.Main.IsHistorySelected);                              // the sidebar still says History
        Assert.Equal("Back to History", debrief.BackLabel);

        debrief.Threads[0].PracticeCommand.Execute(null);
        Assert.Same(p.Practice, p.Main.CurrentPage);
        p.Practice.ExitCommand.Execute(null);
        Assert.Same(debrief, p.Main.CurrentPage);

        debrief.BackCommand.Execute(null);
        Assert.Same(p.History, p.Main.CurrentPage);
        Assert.True(p.Main.IsHistorySelected);
    }

    // ---- the real views

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

    private static List<string> ButtonTexts(DependencyObject root) => Descendants<Button>(root).Where(Shown).SelectMany(b => Texts(b)).ToList();

    [Fact]
    public async Task The_History_screen_shows_each_interview_with_the_right_buttons_and_binds_cleanly()
    {
        var w = await NewWorldAsync();
        var clock = new Clockwork();
        await RecordInterviewAsync(w.History, w.Profile, finish: true, clock);
        clock.Advance(3600);
        await RecordInterviewAsync(w.History, w.Profile, finish: false, clock);
        await w.Vm.LoadAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HistoryView { DataContext = w.Vm };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains("History", texts);
            Assert.Contains("2 mock interviews", texts);
            Assert.Contains("Lean yes", texts);
            Assert.Contains("Unfinished", texts);
            Assert.Contains(texts, t => t.StartsWith("Technical · 15 min"));
            var buttons = ButtonTexts(view);
            Assert.Contains("Open debrief", buttons);
            Assert.Contains("Resume", buttons);
            Assert.Contains("Open the Library", buttons);
            Assert.Equal(1, buttons.Count(b => b == "Open debrief"));       // one is finished, the other is not
            Assert.Equal(1, buttons.Count(b => b == "Resume"));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task An_empty_History_screen_explains_what_will_appear()
    {
        var w = await NewWorldAsync();
        await w.Vm.LoadAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HistoryView { DataContext = w.Vm };
            Layout(view);

            Assert.Contains("No mock interviews yet", Texts(view));
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }

    [Fact]
    public async Task The_Home_screen_shows_the_notice_for_a_left_round_with_Resume_and_Discard()
    {
        var (home, history, _, profile, _) = await NewHomeAsync();
        await RecordInterviewAsync(history, profile, finish: false);
        await home.RefreshMockStatusAsync();
        WpfHost.TakeBindingErrors();

        WpfHost.Run(() =>
        {
            var view = new HomeView { DataContext = home };
            Layout(view);

            var texts = Texts(view);
            Assert.Contains(texts, t => t.StartsWith("You left a technical interview"));
            var buttons = ButtonTexts(view);
            Assert.Contains("Resume", buttons);
            Assert.Contains("Discard", buttons);
        });

        Assert.Equal("", WpfHost.TakeBindingErrors());
    }
}
