using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

public class TechBankRepositoryTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
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

    private static CoachOutput Answer(string text = "a general answer with [where you used it]") => new()
    {
        WhatTheyreTesting = "signal",
        ModelAnswer = text,
        Shape = "A → B",
        Delivery = null,
        FollowUps = [new FollowUp { Question = "Why?", Hint = "because" }],
    };

    // ---- technologies per job description

    [Fact]
    public async Task Technologies_survive_a_restart_and_unknown_fingerprints_are_null()
    {
        await Open().SaveTechnologiesAsync("abc123", ["C#", ".NET", "SQL Server"]);

        var reopened = Open();

        Assert.Equal(["C#", ".NET", "SQL Server"], await reopened.GetTechnologiesAsync("abc123"));
        Assert.Null(await reopened.GetTechnologiesAsync("never-seen"));
    }

    [Fact]
    public async Task An_empty_technology_list_is_stored_and_is_not_confused_with_never_extracted()
    {
        await Open().SaveTechnologiesAsync("empty-jd", []);

        var found = await Open().GetTechnologiesAsync("empty-jd");

        Assert.NotNull(found);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Saving_technologies_again_for_the_same_fingerprint_replaces_them()
    {
        var repo = Open();
        await repo.SaveTechnologiesAsync("fp", ["A"]);

        await repo.SaveTechnologiesAsync("fp", ["B", "C"]);

        Assert.Equal(["B", "C"], await repo.GetTechnologiesAsync("fp"));
    }

    // ---- questions

    [Fact]
    public async Task A_saved_question_survives_a_restart_with_its_details()
    {
        var added = await Open().AddQuestionAsync("SQL Server", Seniority.Senior, "How do indexes speed up a query?", "Index basics");

        var found = Assert.Single(await Open().ListQuestionsAsync("SQL Server", Seniority.Senior));

        Assert.Equal(added.Id, found.Id);
        Assert.Equal("SQL Server", found.Technology);
        Assert.Equal(Seniority.Senior, found.Seniority);
        Assert.Equal("How do indexes speed up a query?", found.Question);
        Assert.Equal("Index basics", found.Focus);
    }

    [Theory]
    [InlineData("sql server")]
    [InlineData("SQL SERVER")]
    [InlineData("  SQL Server  ")]
    public async Task Technology_lookup_ignores_case_and_surrounding_spaces(string lookup)
    {
        await Open().AddQuestionAsync("SQL Server", Seniority.Senior, "Q?", "f");

        Assert.Single(await Open().ListQuestionsAsync(lookup, Seniority.Senior));
    }

    [Fact]
    public async Task Questions_are_separated_by_seniority_and_technology()
    {
        var repo = Open();
        await repo.AddQuestionAsync("C#", Seniority.Senior, "Senior C# question?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Junior, "Junior C# question?", "f");
        await repo.AddQuestionAsync("React", Seniority.Senior, "React question?", "f");

        var seniorCSharp = await repo.ListQuestionsAsync("C#", Seniority.Senior);

        Assert.Equal("Senior C# question?", Assert.Single(seniorCSharp).Question);
    }

    [Fact]
    public async Task The_same_question_is_stored_once_even_with_different_punctuation_or_case()
    {
        var repo = Open();
        var first = await repo.AddQuestionAsync("Go", Seniority.Mid, "What is a goroutine?", "f");

        var second = await repo.AddQuestionAsync("go", Seniority.Mid, "what is a goroutine", "f2");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await repo.ListQuestionsAsync("Go", Seniority.Mid));
    }

    [Fact]
    public async Task The_same_question_text_can_exist_for_two_levels()
    {
        var repo = Open();
        var mid = await repo.AddQuestionAsync("Go", Seniority.Mid, "What is a goroutine?", "f");

        var senior = await repo.AddQuestionAsync("Go", Seniority.Senior, "What is a goroutine?", "f");

        Assert.NotEqual(mid.Id, senior.Id);
    }

    [Fact]
    public async Task Questions_are_listed_oldest_first()
    {
        var repo = Open();
        await repo.AddQuestionAsync("C#", Seniority.Senior, "First?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "Second?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "Third?", "f");

        var questions = (await repo.ListQuestionsAsync("C#", Seniority.Senior)).Select(q => q.Question);

        Assert.Equal(["First?", "Second?", "Third?"], questions);
    }

    // ---- answers

    [Fact]
    public async Task An_answer_survives_a_restart_with_every_field_intact()
    {
        var repo = Open();
        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "What is a closure?", "closures");
        await repo.SaveAnswerAsync(question.Id, 0, Answer());

        var found = await Open().GetAnswerAsync(question.Id, 0);

        Assert.NotNull(found);
        Assert.Equal("a general answer with [where you used it]", found.ModelAnswer);
        Assert.Equal("signal", found.WhatTheyreTesting);
        Assert.Equal("A → B", found.Shape);
        Assert.Null(found.Delivery);
        Assert.Equal("Why?", found.FollowUps.Single().Question);
    }

    [Fact]
    public async Task Answers_are_kept_per_requested_length()
    {
        var repo = Open();
        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "What is a closure?", "f");
        await repo.SaveAnswerAsync(question.Id, 0, Answer("norm"));
        await repo.SaveAnswerAsync(question.Id, 120, Answer("120 words"));

        Assert.Equal("norm", (await repo.GetAnswerAsync(question.Id, 0))!.ModelAnswer);
        Assert.Equal("120 words", (await repo.GetAnswerAsync(question.Id, 120))!.ModelAnswer);
        Assert.Null(await repo.GetAnswerAsync(question.Id, 250));
    }

    [Fact]
    public async Task Saving_an_answer_again_replaces_it()
    {
        var repo = Open();
        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "What is a closure?", "f");
        await repo.SaveAnswerAsync(question.Id, 0, Answer("old"));

        await repo.SaveAnswerAsync(question.Id, 0, Answer("new"));

        Assert.Equal("new", (await repo.GetAnswerAsync(question.Id, 0))!.ModelAnswer);
        Assert.Equal(1, (await repo.GetStatsAsync()).Answers);
    }

    [Fact]
    public async Task A_missing_answer_is_null()
    {
        var repo = Open();
        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "Q?", "f");

        Assert.Null(await repo.GetAnswerAsync(question.Id, 0));
        Assert.Null(await repo.GetAnswerAsync(9999, 0));
    }

    // ---- stats and clearing

    [Fact]
    public async Task Stats_count_questions_and_answers()
    {
        var repo = Open();
        var a = await repo.AddQuestionAsync("C#", Seniority.Senior, "A?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "B?", "f");
        await repo.SaveAnswerAsync(a.Id, 0, Answer());

        Assert.Equal(new TechBankStats(2, 1), await repo.GetStatsAsync());
    }

    [Fact]
    public async Task Clearing_removes_questions_and_answers_together_but_keeps_the_technologies()
    {
        var repo = Open();
        await repo.SaveTechnologiesAsync("fp", ["C#"]);
        var question = await repo.AddQuestionAsync("C#", Seniority.Senior, "Q?", "f");
        await repo.SaveAnswerAsync(question.Id, 0, Answer());

        await repo.ClearAsync();

        Assert.Equal(new TechBankStats(0, 0), await repo.GetStatsAsync());
        Assert.Empty(await repo.ListQuestionsAsync("C#", Seniority.Senior));
        Assert.Null(await repo.GetAnswerAsync(question.Id, 0));
        Assert.Equal(["C#"], await repo.GetTechnologiesAsync("fp"));
    }

    [Fact]
    public void Migrating_twice_with_the_tech_bank_tables_is_harmless()
    {
        Open();
        Open();
    }
}
