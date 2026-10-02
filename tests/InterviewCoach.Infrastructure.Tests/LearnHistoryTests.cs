using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InterviewCoach.Infrastructure.Tests;

public class LearnHistoryTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
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
    private LearnHistoryRepository Open()
    {
        var factory = new FileDbFactory(DbPath);
        Database.Migrate(factory, DbPath);
        return new LearnHistoryRepository(factory, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Delete(true);
    }

    private static CoachOutput Answer(string text = "A struct is a value type.") => new()
    {
        WhatTheyreTesting = "signal",
        ModelAnswer = text,
        Shape = "A -> B",
        FollowUps = [new FollowUp { Question = "Why?", Hint = "because" }],
    };

    private static LearnHistoryEntry Entry(
        string question = "What is a struct?", bool general = true, string? profile = null, string? parent = null,
        string type = "technical_concept", string? technology = "C#", string answer = "A struct is a value type.") => new()
    {
        Question = question, QuestionType = type, Technology = technology, Seniority = "Senior", Source = "fundamentals",
        IsFollowUp = parent is not null, ParentQuestion = parent, IsGeneral = general, ProfileName = profile, Coach = Answer(answer),
    };

    // ---- recording and listing

    [Fact]
    public async Task A_recorded_question_and_answer_come_back_with_everything_that_was_kept()
    {
        await Open().RecordAsync(Entry());

        var entry = Assert.Single(await Open().ListAsync()); // a fresh repository: it was saved to disk

        Assert.Equal("What is a struct?", entry.Question);
        Assert.Equal("technical_concept", entry.QuestionType);
        Assert.Equal("C#", entry.Technology);
        Assert.Equal("Senior", entry.Seniority);
        Assert.Equal("fundamentals", entry.Source);
        Assert.True(entry.IsGeneral);
        Assert.False(entry.IsFollowUp);
        Assert.Null(entry.ParentQuestion);
        Assert.Null(entry.ProfileName);
        Assert.Equal("A struct is a value type.", entry.Coach.ModelAnswer);
        Assert.Equal("Why?", Assert.Single(entry.Coach.FollowUps).Question);
        Assert.Equal(1, entry.TimesSeen);
        Assert.Equal(_clock.UtcNow.UtcDateTime, entry.FirstSeenAt);
    }

    [Fact]
    public async Task Seeing_the_same_question_again_updates_it_instead_of_adding_a_copy()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(answer: "first answer"));
        _clock.UtcNow = _clock.UtcNow.AddDays(2);
        await repo.RecordAsync(Entry(question: "what is a  struct?", answer: "second answer")); // same question, different spacing and case

        var entry = Assert.Single(await repo.ListAsync());

        Assert.Equal(2, entry.TimesSeen);
        Assert.Equal("second answer", entry.Coach.ModelAnswer);
        Assert.Equal(new DateTime(2026, 10, 2, 9, 0, 0), entry.FirstSeenAt);
        Assert.Equal(new DateTime(2026, 10, 4, 9, 0, 0), entry.LastSeenAt);
        Assert.Equal("What is a struct?", entry.Question); // the first wording is kept
    }

    [Fact]
    public async Task A_general_answer_and_one_tailored_to_a_resume_are_separate_entries()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(general: true));
        await repo.RecordAsync(Entry(general: false, profile: "Claims platform", answer: "tailored"));

        var entries = await repo.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.IsGeneral && e.ProfileName is null);
        Assert.Contains(entries, e => !e.IsGeneral && e.ProfileName == "Claims platform" && e.Coach.ModelAnswer == "tailored");
    }

    [Fact]
    public async Task Answers_tailored_for_two_profiles_are_kept_apart()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(general: false, profile: "Claims platform"));
        await repo.RecordAsync(Entry(general: false, profile: "Fintech startup"));
        await repo.RecordAsync(Entry(general: false, profile: "Claims platform")); // seen again

        var entries = await repo.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Single(e => e.ProfileName == "Claims platform").TimesSeen);
    }

    [Fact]
    public async Task The_same_follow_up_under_a_different_parent_is_a_different_entry()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(question: "Why?", parent: "What is a struct?"));
        await repo.RecordAsync(Entry(question: "Why?", parent: "What is a class?"));

        var entries = await repo.ListAsync();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsFollowUp));
        Assert.Equal(["What is a class?", "What is a struct?"], entries.Select(e => e.ParentQuestion!).Order().ToArray());
    }

    [Fact]
    public async Task The_most_recently_seen_comes_first()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(question: "First?"));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await repo.RecordAsync(Entry(question: "Second?"));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        await repo.RecordAsync(Entry(question: "First?")); // seen again, so it moves to the top

        Assert.Equal(["First?", "Second?"], (await repo.ListAsync()).Select(e => e.Question).ToArray());
    }

    [Fact]
    public async Task A_question_with_no_technology_or_profile_reads_back_with_none()
    {
        await Open().RecordAsync(Entry(question: "Tell me about yourself.", type: "tell_me_about_yourself", technology: null, general: false, profile: null));

        var entry = Assert.Single(await Open().ListAsync());

        Assert.Null(entry.Technology);
        Assert.Null(entry.ProfileName);
    }

    // ---- removing

    [Fact]
    public async Task One_entry_can_be_deleted_and_the_rest_stay()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(question: "Keep?"));
        await repo.RecordAsync(Entry(question: "Drop?"));
        var drop = (await repo.ListAsync()).Single(e => e.Question == "Drop?");

        await repo.DeleteAsync(drop.Id);

        Assert.Equal(["Keep?"], (await repo.ListAsync()).Select(e => e.Question).ToArray());
    }

    [Fact]
    public async Task Clearing_removes_everything()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(question: "A?"));
        await repo.RecordAsync(Entry(question: "B?"));

        await repo.ClearAsync();

        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task An_entry_with_unreadable_answer_data_is_left_out_instead_of_breaking_the_list()
    {
        var repo = Open();
        await repo.RecordAsync(Entry(question: "Good?"));
        await using (var db = new FileDbFactory(DbPath).CreateDbContext())
        {
            db.LearnHistory.Add(new LearnHistoryEntity
            {
                QuestionKey = "bad", Question = "Bad?", CoachJson = "{ not json", FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(["Good?"], (await repo.ListAsync()).Select(e => e.Question).ToArray());
    }

    // ---- the first migration run carries earlier technical questions over

    [Fact]
    public async Task Technical_questions_already_in_the_bank_start_the_library_off_when_the_database_is_upgraded()
    {
        var factory = new FileDbFactory(DbPath);
        await using (var db = factory.CreateDbContext())
        {
            // A database as it was before the library existed.
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261002045347_AddTechBank");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO TechQuestions (Id, TechnologyKey, Technology, Seniority, QuestionKey, Question, Focus, CreatedAt) VALUES " +
                "(1, 'c#', 'C#', 'Senior', 'q1', 'What is a struct?', 'f', '2026-10-01 10:00:00'), " +
                "(2, 'c#', 'C#', 'Senior', 'q2', 'Never shown?', 'f', '2026-10-01 11:00:00'), " +      // written in advance, has no answer
                "(3, 'react', 'React', 'Mid', 'q3', 'What is a hook?', 'f', '2026-10-01 12:00:00')");
            var norm = System.Text.Json.JsonSerializer.Serialize(new { what_theyre_testing = "norm", model_answer = "the norm answer", shape = "a", follow_ups = Array.Empty<object>() });
            var old = System.Text.Json.JsonSerializer.Serialize(new { what_theyre_testing = "old", model_answer = "an older answer", shape = "a", follow_ups = Array.Empty<object>() });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO TechAnswers (TechQuestionId, AnswerWords, CoachJson, CreatedAt) VALUES (1, 0, {old}, '2026-10-01 10:05:00'), (1, -1, {norm}, '2026-10-01 10:01:00'), (3, -1, {old}, '2026-10-01 12:01:00')");
        }

        Database.Migrate(factory, DbPath); // the upgrade that adds the library

        var entries = await new LearnHistoryRepository(factory, _clock).ListAsync();

        Assert.Equal(2, entries.Count);
        var struct1 = entries.Single(e => e.Question == "What is a struct?");
        Assert.Equal("the norm answer", struct1.Coach.ModelAnswer); // the interviewer-norm answer wins over an older one
        Assert.True(struct1.IsGeneral);
        Assert.Equal("technical_concept", struct1.QuestionType);
        Assert.Equal("C#", struct1.Technology);
        Assert.Equal("Senior", struct1.Seniority);
        Assert.Equal(1, struct1.TimesSeen);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), struct1.FirstSeenAt);
        Assert.DoesNotContain(entries, e => e.Question == "Never shown?");
        Assert.Equal("React", entries.Single(e => e.Question == "What is a hook?").Technology);
    }

    // ---- Demo mode and the in-memory copy

    [Fact]
    public async Task In_demo_mode_questions_go_to_memory_and_never_to_the_database()
    {
        var real = Open();
        var demo = new InMemoryLearnHistory();
        var settings = new AppSettings { DemoMode = true };
        var routing = new RoutingLearnHistory(new MemorySettings(settings), real, demo);

        await routing.RecordAsync(Entry(question: "A demo question?"));

        Assert.Single(await demo.ListAsync());
        Assert.Empty(await real.ListAsync());

        settings.DemoMode = false;
        await routing.RecordAsync(Entry(question: "A real question?"));
        Assert.Equal(["A real question?"], (await real.ListAsync()).Select(e => e.Question).ToArray());
        Assert.Equal(["A real question?"], (await routing.ListAsync()).Select(e => e.Question).ToArray());
    }

    [Fact]
    public async Task The_in_memory_library_treats_a_repeat_the_same_way_the_database_does()
    {
        var memory = new InMemoryLearnHistory();
        await memory.RecordAsync(Entry(answer: "first"));
        await memory.RecordAsync(Entry(question: "WHAT is a struct", answer: "second"));
        await memory.RecordAsync(Entry(general: false, profile: "p"));

        var entries = await memory.ListAsync();

        Assert.Equal(2, entries.Count);
        var general = entries.Single(e => e.IsGeneral);
        Assert.Equal(2, general.TimesSeen);
        Assert.Equal("second", general.Coach.ModelAnswer);

        await memory.DeleteAsync(general.Id);
        Assert.Single(await memory.ListAsync());
        await memory.ClearAsync();
        Assert.Empty(await memory.ListAsync());
    }
}
