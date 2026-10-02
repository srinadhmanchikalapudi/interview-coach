using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure.Tests;

public class ProfileRepositoryTests : IDisposable
{
    private sealed class FileDbFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(Database.ConnectionString(path)).Options);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory();
    private readonly TestClock _clock = new();
    private string DbPath => Path.Combine(_dir.FullName, "app.db");

    // Each call builds a brand-new factory, repository and connection pool entry, like an app restart.
    private ProfileRepository OpenRepository()
    {
        var factory = new FileDbFactory(DbPath);
        Database.Migrate(factory, DbPath);
        return new ProfileRepository(factory, _clock);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release the file lock so the folder can be deleted
        _dir.Delete(true);
    }

    private static CandidateProfile Sample(string name = "Backend at Acme") => new()
    {
        Name = name,
        JobRole = "Senior Backend Engineer",
        Seniority = Seniority.Senior,
        JobDescription = "Build services.\n\nMust know SQL.",
        ResumeText = "Jane Doe\n- Built the ranking service (p99 300ms to 80ms)\n- Ünïcode ✓ résumé",
    };

    [Fact]
    public async Task Saved_profile_survives_a_restart_with_every_field_intact()
    {
        var saved = await OpenRepository().SaveAsync(Sample());

        var reopened = await OpenRepository().GetAsync(saved.Id);

        Assert.NotNull(reopened);
        Assert.Equal("Backend at Acme", reopened.Name);
        Assert.Equal("Senior Backend Engineer", reopened.JobRole);
        Assert.Equal(Seniority.Senior, reopened.Seniority);
        Assert.Equal("Build services.\n\nMust know SQL.", reopened.JobDescription);
        Assert.Equal(Sample().ResumeText, reopened.ResumeText);
    }

    [Fact]
    public async Task Insert_assigns_an_id_and_stamps_both_dates()
    {
        var saved = await OpenRepository().SaveAsync(Sample());

        Assert.True(saved.Id > 0);
        Assert.Equal(_clock.UtcNow.UtcDateTime, saved.CreatedAt);
        Assert.Equal(_clock.UtcNow.UtcDateTime, saved.UpdatedAt);
    }

    [Fact]
    public async Task Update_keeps_created_date_and_moves_updated_date()
    {
        var repo = OpenRepository();
        var first = await repo.SaveAsync(Sample());
        _clock.UtcNow = _clock.UtcNow.AddHours(3);

        var edited = first.Clone();
        edited.JobRole = "Staff Engineer";
        var second = await repo.SaveAsync(edited);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(_clock.UtcNow.UtcDateTime, second.UpdatedAt);
        Assert.Equal("Staff Engineer", (await repo.GetAsync(first.Id))!.JobRole);
        Assert.Single(await repo.ListAsync());
    }

    [Fact]
    public async Task List_is_most_recently_updated_first()
    {
        var repo = OpenRepository();
        await repo.SaveAsync(Sample("old"));
        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        await repo.SaveAsync(Sample("new"));

        var names = (await repo.ListAsync()).Select(p => p.Name);

        Assert.Equal(["new", "old"], names);
    }

    [Fact]
    public async Task Delete_removes_only_that_profile()
    {
        var repo = OpenRepository();
        var keep = await repo.SaveAsync(Sample("keep"));
        var drop = await repo.SaveAsync(Sample("drop"));

        await repo.DeleteAsync(drop.Id);

        Assert.Null(await repo.GetAsync(drop.Id));
        Assert.NotNull(await repo.GetAsync(keep.Id));
    }

    [Fact]
    public async Task Saving_a_profile_deleted_elsewhere_fails_clearly_instead_of_recreating_it()
    {
        var repo = OpenRepository();
        var saved = await repo.SaveAsync(Sample());
        await repo.DeleteAsync(saved.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.SaveAsync(saved));
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Names_are_trimmed_but_resume_text_is_stored_exactly()
    {
        var p = Sample();
        p.Name = "  padded  ";
        p.ResumeText = "  keep my spacing\n";

        var saved = await OpenRepository().SaveAsync(p);

        Assert.Equal("padded", saved.Name);
        Assert.Equal("  keep my spacing\n", saved.ResumeText);
    }

    [Fact]
    public void Migrating_twice_is_harmless()
    {
        OpenRepository();
        OpenRepository();
    }
}
