using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using InterviewCoach.Infrastructure.Prompts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

public class ResumeTopicsTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
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

    private static readonly ResumeTopic[] Topics =
    [
        new("CPF", "claims platform", "built the claims ranking service"),
        new("EDF", "", "applied the strangler fig pattern"),
    ];

    [Fact]
    public async Task Topics_read_from_a_resume_are_kept_across_restarts()
    {
        await Open().SaveResumeTopicsAsync("abc", Topics);

        var loaded = await Open().GetResumeTopicsAsync("abc");

        Assert.Equal(Topics, loaded);
    }

    [Fact]
    public async Task A_resume_that_was_never_read_has_no_entry_and_an_empty_list_is_still_an_entry()
    {
        var repo = Open();
        Assert.Null(await repo.GetResumeTopicsAsync("never-read"));

        await repo.SaveResumeTopicsAsync("blank", []);

        var loaded = await repo.GetResumeTopicsAsync("blank");
        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Saving_again_for_the_same_resume_replaces_the_list()
    {
        var repo = Open();
        await repo.SaveResumeTopicsAsync("abc", Topics);

        await repo.SaveResumeTopicsAsync("abc", [new ResumeTopic("TCS", "", "maintained billing")]);

        Assert.Equal([new ResumeTopic("TCS", "", "maintained billing")], await repo.GetResumeTopicsAsync("abc"));
    }

    [Fact]
    public async Task Unreadable_saved_topics_are_treated_as_never_read_so_they_are_read_again()
    {
        var repo = Open();
        await using (var db = new FileDbFactory(DbPath).CreateDbContext())
        {
            db.ResumeTopics.Add(new ResumeTopicsEntity { Fingerprint = "bad", TopicsJson = "{ not json", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Null(await repo.GetResumeTopicsAsync("bad"));
    }

    [Fact]
    public async Task Topics_do_not_collide_with_the_technologies_saved_for_a_job_description()
    {
        var repo = Open();
        await repo.SaveTechnologiesAsync("same-key", ["C#"]);
        await repo.SaveResumeTopicsAsync("same-key", Topics);

        Assert.Equal(["C#"], await repo.GetTechnologiesAsync("same-key"));
        Assert.Equal(Topics, await repo.GetResumeTopicsAsync("same-key"));
    }

    [Fact]
    public async Task Clearing_the_saved_technical_questions_keeps_the_resume_topics()
    {
        var repo = Open();
        await repo.SaveResumeTopicsAsync("abc", Topics);

        await repo.ClearAsync();

        Assert.Equal(Topics, await repo.GetResumeTopicsAsync("abc")); // they are cheap and still correct, like the technologies
    }

    [Fact]
    public async Task In_demo_mode_topics_go_to_memory_and_never_to_the_database()
    {
        var real = Open();
        var demo = new InMemoryTechBankRepository();
        var settings = new AppSettings { DemoMode = true };
        var routing = new RoutingTechBankRepository(new MemorySettings(settings), real, demo);

        await routing.SaveResumeTopicsAsync("abc", Topics);

        Assert.Equal(Topics, await demo.GetResumeTopicsAsync("abc"));
        Assert.Null(await real.GetResumeTopicsAsync("abc"));
        settings.DemoMode = false;
        Assert.Null(await routing.GetResumeTopicsAsync("abc"));
    }

    // ---- the prompt

    private static readonly PromptLibrary Embedded = new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    [Fact]
    public void The_resume_topics_prompt_asks_for_employers_and_highlights_without_inventing_anything()
    {
        var vars = PromptVars.ForProfile(new CandidateProfile { JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = "JD-TEXT", ResumeText = "RESUME-TEXT" });

        var rendered = Embedded.Render(PromptName.ResumeTopics, vars);

        Assert.Contains("RESUME-TEXT", rendered);
        Assert.DoesNotContain("JD-TEXT", rendered);                  // the reading depends on the resume alone
        Assert.Contains("At most 8 entries", rendered);
        Assert.Contains("2 to 5 highlights", rendered);
        Assert.Contains("Do not invent anything the resume does not say", rendered);
        Assert.Contains("\"topics\"", rendered);
        Assert.Equal("List the topics.", PromptName.ResumeTopics.UserMessage());
        Assert.Equal("resume_topics.md", PromptName.ResumeTopics.FileName());
    }

    [Fact]
    public void The_question_prompt_has_a_resume_focus_block_after_the_resume_and_a_rule_for_using_it()
    {
        var vars = new Dictionary<string, string?>
        {
            ["JOB_ROLE"] = "r", ["SENIORITY"] = "Senior", ["JOB_DESCRIPTION"] = "jd", ["RESUME"] = "cv", ["QUESTION_TYPES"] = "Any",
            ["ALREADY_ASKED"] = null, ["FOCUS_TECHNOLOGY"] = null, ["EMPLOYMENT_TYPE"] = null, ["RESUME_FOCUS"] = "FOCUS-TEXT",
        };

        var rendered = Embedded.Render(PromptName.QuestionGenerator, vars).Replace("\r\n", "\n");

        Assert.Contains("<resume_focus>\nFOCUS-TEXT\n</resume_focus>", rendered);
        Assert.True(rendered.IndexOf("</candidate_resume>", StringComparison.Ordinal) < rendered.IndexOf("<resume_focus>", StringComparison.Ordinal),
            "the focus changes every question, so it must come after the part that is cached");
        Assert.Contains("If resume_focus is not (none) and you write a resume_deep_dive question, follow it exactly", rendered);
        vars["RESUME_FOCUS"] = null;
        Assert.Contains("<resume_focus>\n(none)\n</resume_focus>", Embedded.Render(PromptName.QuestionGenerator, vars).Replace("\r\n", "\n"));
    }
}
