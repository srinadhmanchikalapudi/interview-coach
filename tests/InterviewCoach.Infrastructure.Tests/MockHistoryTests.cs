using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using InterviewCoach.Infrastructure.Prompts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

/// <summary>Mock interviews are kept in the database (for the History screen), and Demo mode's fake interviewer runs a whole interview.</summary>
public class MockHistoryTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private string DbPath => Path.Combine(_dir.FullName, "app.db");

    private MockHistoryRepository Open()
    {
        var factory = new FileDbFactory(DbPath);
        Database.Migrate(factory, DbPath);
        return new MockHistoryRepository(factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    private static MockRecord Record(string role = "Backend Engineer", DateTime? started = null) => new()
    {
        ProfileId = 7, ProfileName = "Acme", JobRole = role, RoundType = "Technical", Employment = "Contract", DurationMinutes = 30,
        StartedAt = started ?? new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc), PlanJson = "{\"focus_areas\":[]}",
        TurnsJson = "[{\"text\":\"Hi\"}]",
    };

    [Fact]
    public async Task An_interview_is_saved_and_listed_with_its_parts()
    {
        var repo = Open();
        var record = Record();
        record.Finished = true;
        record.ElapsedSeconds = 900;
        record.EndedAt = record.StartedAt.AddMinutes(15);

        var id = await repo.AddAsync(record);
        var saved = Assert.Single(await repo.ListAsync());

        Assert.True(id > 0);
        Assert.Equal(id, saved.Id);
        Assert.Equal("Backend Engineer", saved.JobRole);
        Assert.Equal("Technical", saved.RoundType);
        Assert.Equal(7, saved.ProfileId);
        Assert.Equal("Contract", saved.Employment);
        Assert.Equal(30, saved.DurationMinutes);
        Assert.Equal(900, saved.ElapsedSeconds);
        Assert.True(saved.Finished);
        Assert.Equal("{\"focus_areas\":[]}", saved.PlanJson);
        Assert.Equal("[{\"text\":\"Hi\"}]", saved.TurnsJson);
        Assert.Null(saved.DebriefJson);                  // not written yet
        Assert.Null(saved.HireSignal);
    }

    [Fact]
    public async Task Updating_adds_the_debrief_and_the_coaching_to_the_same_row()
    {
        var repo = Open();
        var record = Record();
        record.Id = await repo.AddAsync(record);

        record.DebriefJson = "{\"hire_signal\":\"yes\"}";
        record.ThreadsJson = "[{\"question\":\"Q\"}]";
        record.HireSignal = "yes";
        record.Finished = true;
        await repo.UpdateAsync(record);

        var saved = Assert.Single(await repo.ListAsync());
        Assert.Equal("{\"hire_signal\":\"yes\"}", saved.DebriefJson);
        Assert.Equal("[{\"question\":\"Q\"}]", saved.ThreadsJson);
        Assert.Equal("yes", saved.HireSignal);
    }

    [Fact]
    public async Task The_list_is_newest_first_survives_a_restart_and_a_delete_removes_one()
    {
        var first = await Open().AddAsync(Record("Old", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)));
        await Open().AddAsync(Record("New", new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc)));

        var repo = Open();
        Assert.Equal(["New", "Old"], (await repo.ListAsync()).Select(r => r.JobRole));

        await repo.DeleteAsync(first);
        Assert.Equal(["New"], (await repo.ListAsync()).Select(r => r.JobRole));
    }

    [Fact]
    public async Task Updating_an_interview_that_is_not_there_does_nothing()
    {
        var repo = Open();
        var record = Record();
        record.Id = 999;

        await repo.UpdateAsync(record);

        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Demo_mode_keeps_its_interviews_in_memory_only()
    {
        var settings = new DemoSettings();
        var routing = new RoutingMockHistory(settings, Open(), new InMemoryMockHistory());

        settings.Demo = true;
        await routing.AddAsync(Record("Demo role"));
        Assert.Single(await routing.ListAsync());

        settings.Demo = false;
        Assert.Empty(await routing.ListAsync());               // the real database never saw it
    }

    private sealed class DemoSettings : ISettingsStore
    {
        public bool Demo { get; set; }
        public AppSettings Current => new() { DemoMode = Demo };
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    // ---- the engine on the real repository and on Demo mode's fake model

    [Fact]
    public async Task Demo_mode_runs_a_whole_interview_from_plan_to_debrief_with_no_key()
    {
        var llm = new FakeLlmService();
        var history = new InMemoryMockHistory();
        var engine = new MockEngine(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), history: history);
        var profile = new CandidateProfile { Name = "Demo", JobRole = "Backend Engineer", Seniority = Seniority.Mid, JobDescription = "jd", ResumeText = "resume" };

        await engine.StartAsync(profile, RoundType.Mixed, 15);
        var spoken = new List<string>();
        var guard = 0;
        while (engine.Phase != MockPhase.Done && guard++ < 30)
        {
            Assert.NotEqual(MockPhase.Failed, engine.Phase);
            spoken.Add(engine.CurrentLine);
            await engine.FinishedSpeakingAsync();
            if (engine.Phase == MockPhase.CandidateAnswering) await engine.SubmitAnswerAsync("A sample answer with enough words to count.", "typed", 12);
        }
        await engine.WhenCoachedAsync();

        Assert.Equal(MockPhase.Done, engine.Phase);
        Assert.Equal("closing", engine.Turns.Last(t => t.Speaker == MockSpeaker.Interviewer).TurnType);
        Assert.Equal(2, engine.Threads.Count);                  // two main questions, one with a follow-up
        Assert.All(engine.Threads, t => Assert.Equal(ThreadStatus.Done, t.Status));
        Assert.Equal("lean_yes", engine.Debrief!.HireSignal);
        Assert.Null(engine.Debrief.FocusAreaRatings[1].Rating);  // an area that did not come up
        var record = Assert.Single(await history.ListAsync());
        Assert.Equal("lean_yes", record.HireSignal);
        Assert.Contains(spoken, line => line.Contains("Walk me through a project you are proud of."));
    }

    [Fact]
    public async Task The_demo_interviewer_closes_when_told_the_time_is_up()
    {
        var llm = new FakeLlmService();
        var clock = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        var engine = new MockEngine(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => clock);
        var profile = new CandidateProfile { Name = "Demo", JobRole = "Dev", Seniority = Seniority.Mid, JobDescription = "jd", ResumeText = "resume" };
        await engine.StartAsync(profile, RoundType.Mixed, 15);
        await engine.FinishedSpeakingAsync();
        clock = clock.AddMinutes(21);

        await engine.SubmitAnswerAsync("Fine.");

        Assert.True(engine.IsClosing);
        Assert.Contains("this was great to chat", engine.CurrentLine);
    }
}
