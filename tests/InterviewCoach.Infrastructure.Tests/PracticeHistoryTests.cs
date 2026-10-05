using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InterviewCoach.Infrastructure.Tests;

public class PracticeHistoryTests : IDisposable
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

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly TestClock _clock = new();
    private string DbPath => Path.Combine(_dir.FullName, "app.db");

    // Every call builds a new factory and repository over the same file, like restarting the app.
    private PracticeHistoryRepository Open()
    {
        var factory = new FileDbFactory(DbPath);
        Database.Migrate(factory, DbPath);
        return new PracticeHistoryRepository(factory, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    private static PracticeRecord Record(string question = "What is a struct?", string answer = "A value type.", int attempt = 1, string? parent = null) => new()
    {
        Question = question, QuestionType = "technical_concept", Technology = "C#", Seniority = "Senior", Source = "fundamentals",
        IsFollowUp = parent is not null, ParentQuestion = parent, ProfileName = "Orders platform", AttemptNumber = attempt,
        AnswerText = answer, InputMethod = "typed", DurationSeconds = 139, WordCount = 3,
        Coach = new CoachOutput
        {
            WhatTheyreTesting = "signal", ModelAnswer = "A struct is a value type.", Shape = "A → B", Delivery = "A good length.",
            Feedback = [new FeedbackPoint { Kind = "strength", Point = "Correct.", Quote = "A value type" }],
            FollowUps = [new FollowUp { Question = "Why?", Hint = "because" }],
        },
    };

    [Fact]
    public async Task An_attempt_comes_back_with_everything_after_a_restart()
    {
        await Open().RecordAsync(Record());

        var record = Assert.Single(await Open().ListAsync());

        Assert.Equal("What is a struct?", record.Question);
        Assert.Equal("technical_concept", record.QuestionType);
        Assert.Equal("C#", record.Technology);
        Assert.Equal("Senior", record.Seniority);
        Assert.Equal("fundamentals", record.Source);
        Assert.Equal("Orders platform", record.ProfileName);
        Assert.Equal("A value type.", record.AnswerText);
        Assert.Equal("typed", record.InputMethod);
        Assert.Equal(139, record.DurationSeconds);
        Assert.Equal(3, record.WordCount);
        Assert.Equal(1, record.AttemptNumber);
        Assert.False(record.IsFollowUp);
        Assert.Null(record.ParentQuestion);
        Assert.Equal(_clock.UtcNow.UtcDateTime, record.CreatedAt);
        Assert.Equal("Correct.", Assert.Single(record.Coach.Feedback).Point);
        Assert.Equal("A value type", record.Coach.Feedback[0].Quote);
        Assert.Equal("A good length.", record.Coach.Delivery);
        Assert.Equal("Why?", Assert.Single(record.Coach.FollowUps).Question);
    }

    [Fact]
    public async Task Every_attempt_is_kept_even_for_the_same_question_and_the_newest_comes_first()
    {
        var repo = Open();
        await repo.RecordAsync(Record(answer: "first try", attempt: 1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
        await repo.RecordAsync(Record(answer: "second try", attempt: 2));
        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        await repo.RecordAsync(Record(answer: "another day", attempt: 1));

        var records = await repo.ListAsync();

        Assert.Equal(["another day", "second try", "first try"], records.Select(r => r.AnswerText).ToArray());
        Assert.Equal([1, 2, 1], records.Select(r => r.AttemptNumber).ToArray());
    }

    [Fact]
    public async Task A_follow_up_keeps_the_question_it_came_from()
    {
        await Open().RecordAsync(Record(question: "Why?", parent: "What is a struct?"));

        var record = Assert.Single(await Open().ListAsync());

        Assert.True(record.IsFollowUp);
        Assert.Equal("What is a struct?", record.ParentQuestion);
    }

    [Fact]
    public async Task One_attempt_can_be_deleted_and_the_rest_stay_and_clearing_removes_everything()
    {
        var repo = Open();
        await repo.RecordAsync(Record(answer: "keep"));
        await repo.RecordAsync(Record(answer: "drop"));
        var drop = (await repo.ListAsync()).Single(r => r.AnswerText == "drop");

        await repo.DeleteAsync(drop.Id);
        Assert.Equal(["keep"], (await repo.ListAsync()).Select(r => r.AnswerText).ToArray());

        await repo.ClearAsync();
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task An_attempt_with_unreadable_feedback_data_is_left_out_instead_of_breaking_the_list()
    {
        var repo = Open();
        await repo.RecordAsync(Record(answer: "good"));
        await using (var db = new FileDbFactory(DbPath).CreateDbContext())
        {
            db.PracticeAttempts.Add(new PracticeAttemptEntity { Question = "Bad?", AnswerText = "x", CoachJson = "{ not json", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        Assert.Equal(["good"], (await repo.ListAsync()).Select(r => r.AnswerText).ToArray());
    }

    [Fact]
    public async Task A_database_from_before_practice_was_saved_is_upgraded_without_losing_anything()
    {
        var factory = new FileDbFactory(DbPath);
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261003014851_AddResumeTopics");   // the version before this one
            const string json = "{\"model_answer\":\"x\"}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO LearnHistory (QuestionKey, Question, QuestionType, Technology, Seniority, Source, IsFollowUp, ParentQuestion, ParentKey, IsGeneral, ProfileName, CoachJson, FirstSeenAt, LastSeenAt, TimesSeen) VALUES ('q', 'Old question?', 'behavioral', '', '', '', 0, '', '', 1, '', {json}, '2026-10-01 10:00:00', '2026-10-01 10:00:00', 1)");
        }

        Database.Migrate(factory, DbPath);

        await using var after = factory.CreateDbContext();
        Assert.Equal(1, await after.LearnHistory.CountAsync());
        Assert.Equal(0, await after.PracticeAttempts.CountAsync());
        await new PracticeHistoryRepository(factory, _clock).RecordAsync(Record());
        Assert.Equal(1, await after.PracticeAttempts.CountAsync());
    }

    [Fact]
    public async Task In_demo_mode_attempts_go_to_memory_and_never_to_the_database()
    {
        var real = Open();
        var demo = new InMemoryPracticeHistory();
        var settings = new AppSettings { DemoMode = true };
        var routing = new RoutingPracticeHistory(new MemorySettings(settings), real, demo);

        await routing.RecordAsync(Record(answer: "demo answer"));

        Assert.Single(await demo.ListAsync());
        Assert.Empty(await real.ListAsync());
        settings.DemoMode = false;
        await routing.RecordAsync(Record(answer: "real answer"));
        Assert.Equal(["real answer"], (await real.ListAsync()).Select(r => r.AnswerText).ToArray());
    }

    [Fact]
    public async Task The_in_memory_copy_keeps_every_attempt_newest_first_and_can_delete_and_clear()
    {
        var now = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        var memory = new InMemoryPracticeHistory(() => now);
        await memory.RecordAsync(Record(answer: "one"));
        now = now.AddMinutes(1);
        await memory.RecordAsync(Record(answer: "two"));

        var records = await memory.ListAsync();
        Assert.Equal(["two", "one"], records.Select(r => r.AnswerText).ToArray());

        await memory.DeleteAsync(records[0].Id);
        Assert.Equal(["one"], (await memory.ListAsync()).Select(r => r.AnswerText).ToArray());
        await memory.ClearAsync();
        Assert.Empty(await memory.ListAsync());
    }
}
