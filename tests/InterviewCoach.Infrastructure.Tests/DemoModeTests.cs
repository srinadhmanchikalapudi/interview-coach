using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using InterviewCoach.Infrastructure.Prompts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

public class DemoModeTests : IDisposable
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "demo", JobRole = "Backend Engineer", Seniority = Seniority.Senior,
        JobDescription = "C# and SQL Server", ResumeText = "Built things.",
    };

    [Fact]
    public async Task The_demo_model_answers_the_tags_prompt()
    {
        var bank = new TechBank(new InMemoryTechBankRepository(), new FakeLlmService(), _prompts);

        var technologies = await bank.GetTechnologiesAsync(Profile());

        Assert.Equal(["C#", ".NET", "SQL Server"], technologies);
    }

    [Fact]
    public async Task The_demo_model_writes_distinct_questions_about_the_requested_technology()
    {
        var bank = new TechBank(new InMemoryTechBankRepository(), new FakeLlmService(), _prompts, () => 0.0);
        var asked = new List<string>();

        for (var i = 0; i < 3; i++)
            asked.Add((await bank.NextQuestionAsync("Redis", Seniority.Senior, asked)).Question);

        Assert.All(asked, q => Assert.Contains("Redis", q));
        Assert.Equal(3, asked.Distinct().Count());
    }

    [Fact]
    public async Task A_whole_demo_learn_session_uses_the_bank_and_ends_up_with_general_answers()
    {
        var llm = new FakeLlmService();
        var repo = new InMemoryTechBankRepository();
        var engine = new LearnEngine(llm, _prompts, new TechBank(repo, llm, _prompts, () => 0.0), () => 0.0);

        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept]);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.True(engine.Current!.IsGeneric);
        Assert.Equal("C#", engine.Current.Technology);
        Assert.NotEmpty(engine.Current.Coach!.ModelAnswer);
        Assert.Equal(new TechBankStats(1, 1), await repo.GetStatsAsync());
    }

    [Fact]
    public async Task Demo_mode_keeps_made_up_questions_out_of_the_real_database()
    {
        var path = Path.Combine(_dir.FullName, "app.db");
        var factory = new FileDbFactory(path);
        Database.Migrate(factory, path);
        var real = new TechBankRepository(factory, new SystemClock());
        var demo = new InMemoryTechBankRepository();
        var settings = new AppSettings { DemoMode = true };
        var router = new RoutingTechBankRepository(new MemorySettings(settings), real, demo);

        await router.AddQuestionAsync("C#", Seniority.Senior, "A demo question?", "f");

        Assert.Equal(new TechBankStats(1, 0), await demo.GetStatsAsync());
        Assert.Equal(new TechBankStats(0, 0), await real.GetStatsAsync()); // the real bank is untouched

        settings.DemoMode = false;
        await router.AddQuestionAsync("C#", Seniority.Senior, "A real question?", "f");

        Assert.Equal(new TechBankStats(1, 0), await real.GetStatsAsync());
        Assert.Equal(new TechBankStats(1, 0), await demo.GetStatsAsync());
    }
}
