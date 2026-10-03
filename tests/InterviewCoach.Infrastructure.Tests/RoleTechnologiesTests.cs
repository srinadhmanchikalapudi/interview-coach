using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

/// <summary>The technologies fetched for a job role are kept in the database, so the model is asked once per role, across restarts.</summary>
public class RoleTechnologiesTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private string DbPath => Path.Combine(_dir.FullName, "app.db");

    // Every call builds a new factory and repository over the same file, like restarting the app.
    private TechBankRepository Open()
    {
        var factory = new FileDbFactory(DbPath);
        Database.Migrate(factory, DbPath);
        return new TechBankRepository(factory, new TestClock());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    [Fact]
    public async Task A_role_that_was_never_fetched_is_null_and_a_saved_one_comes_back_in_order()
    {
        var repo = Open();
        Assert.Null(await repo.GetRoleTechnologiesAsync("data engineer"));

        await repo.SaveRoleTechnologiesAsync("data engineer", "Data Engineer", ["Python", "SQL", "Spark"]);

        Assert.Equal(["Python", "SQL", "Spark"], await repo.GetRoleTechnologiesAsync("data engineer"));
        Assert.Null(await repo.GetRoleTechnologiesAsync("android developer")); // roles are separate
    }

    [Fact]
    public async Task The_list_survives_restarting_the_app()
    {
        await Open().SaveRoleTechnologiesAsync("data engineer", "Data Engineer", ["Python", "dbt"]);

        var afterRestart = await Open().GetRoleTechnologiesAsync("data engineer");

        Assert.Equal(["Python", "dbt"], afterRestart);
    }

    [Fact]
    public async Task Saving_again_replaces_the_list_and_keeps_one_row_per_role()
    {
        var repo = Open();
        await repo.SaveRoleTechnologiesAsync("data engineer", "Data engineer", ["Python"]);

        await repo.SaveRoleTechnologiesAsync("data engineer", "Data Engineer", ["Python", "Airflow"]);

        Assert.Equal(["Python", "Airflow"], await repo.GetRoleTechnologiesAsync("data engineer"));
        await using var db = new FileDbFactory(DbPath).CreateDbContext();
        var row = await db.RoleTechnologies.SingleAsync();
        Assert.Equal("Data Engineer", row.Role);
    }

    [Fact]
    public async Task An_unreadable_saved_list_counts_as_never_fetched()
    {
        var repo = Open();
        await using (var db = new FileDbFactory(DbPath).CreateDbContext())
        {
            db.RoleTechnologies.Add(new RoleTechnologiesEntity { RoleKey = "broken", Role = "Broken", TechnologiesJson = "{not json" });
            await db.SaveChangesAsync();
        }

        Assert.Null(await repo.GetRoleTechnologiesAsync("broken"));
    }

    [Fact]
    public async Task Clearing_the_saved_questions_keeps_the_role_lists()
    {
        var repo = Open();
        await repo.SaveRoleTechnologiesAsync("data engineer", "Data engineer", ["Python"]);

        await repo.ClearAsync();

        Assert.Equal(["Python"], await repo.GetRoleTechnologiesAsync("data engineer"));
    }

    [Fact]
    public async Task The_bank_saves_through_the_database_so_a_new_run_does_not_ask_the_model_again()
    {
        var calls = 0;
        var llm = new CountingLlm(() => calls++);
        var prompts = new Infrastructure.Prompts.PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        var first = await new TechBank(Open(), llm, prompts).GetRoleTechnologiesAsync("Data Engineer");
        var second = await new TechBank(Open(), llm, prompts).GetRoleTechnologiesAsync("data  engineer");

        Assert.Equal(1, calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Demo_mode_keeps_its_role_lists_in_memory_only()
    {
        var settings = new DemoSettings();
        var routing = new RoutingTechBankRepository(settings, Open(), new InMemoryTechBankRepository());

        settings.Demo = true;
        await routing.SaveRoleTechnologiesAsync("data engineer", "Data engineer", ["Fake tech"]);
        Assert.Equal(["Fake tech"], await routing.GetRoleTechnologiesAsync("data engineer"));

        settings.Demo = false;
        Assert.Null(await routing.GetRoleTechnologiesAsync("data engineer")); // the real database never saw it
    }

    private sealed class DemoSettings : ISettingsStore
    {
        public bool Demo { get; set; }
        public AppSettings Current => new() { DemoMode = Demo };
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class CountingLlm(Action called) : ILlmService
    {
        public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
        {
            called();
            return Task.FromResult((T)(object)new TechTagsDto { Technologies = ["Python", "SQL"] });
        }

        public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct) => throw new NotSupportedException();
    }
}
