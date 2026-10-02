using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace InterviewCoach.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<CandidateProfile> Profiles => Set<CandidateProfile>();
    public DbSet<TechQuestionEntity> TechQuestions => Set<TechQuestionEntity>();
    public DbSet<TechAnswerEntity> TechAnswers => Set<TechAnswerEntity>();
    public DbSet<JdTechnologiesEntity> JdTechnologies => Set<JdTechnologiesEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateProfile>(e =>
        {
            e.ToTable("Profiles");
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired();
            e.Property(p => p.JobRole).IsRequired();
            e.Property(p => p.JobDescription).IsRequired();
            e.Property(p => p.ResumeText).IsRequired();
            e.Property(p => p.Seniority).HasConversion<string>().IsRequired(); // readable in the db file, survives enum reordering
            e.HasIndex(p => p.UpdatedAt);
        });

        modelBuilder.Entity<TechQuestionEntity>(e =>
        {
            e.ToTable("TechQuestions");
            e.HasKey(q => q.Id);
            e.Property(q => q.TechnologyKey).IsRequired();
            e.Property(q => q.Technology).IsRequired();
            e.Property(q => q.QuestionKey).IsRequired();
            e.Property(q => q.Question).IsRequired();
            e.Property(q => q.Focus).IsRequired();
            e.Property(q => q.Seniority).HasConversion<string>().IsRequired();
            e.HasIndex(q => new { q.TechnologyKey, q.Seniority, q.QuestionKey }).IsUnique(); // the same question is stored once
            e.HasMany(q => q.Answers).WithOne().HasForeignKey(a => a.TechQuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TechAnswerEntity>(e =>
        {
            e.ToTable("TechAnswers");
            e.HasKey(a => a.Id);
            e.Property(a => a.CoachJson).IsRequired();
            e.HasIndex(a => new { a.TechQuestionId, a.AnswerWords }).IsUnique();
        });

        modelBuilder.Entity<JdTechnologiesEntity>(e =>
        {
            e.ToTable("JdTechnologies");
            e.HasKey(j => j.Fingerprint);
            e.Property(j => j.TechnologiesJson).IsRequired();
        });
    }
}

public static class Database
{
    public static string DefaultPath => Path.Combine(SettingsStore.AppDataDirectory, "app.db");

    public static string ConnectionString(string path) => $"Data Source={path}";

    /// <summary>Creates the folder and brings the database up to the latest migration.</summary>
    public static void Migrate(IDbContextFactory<AppDbContext> factory, string? path = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path ?? DefaultPath)!);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
    }
}

/// <summary>Lets `dotnet ef migrations add` build a context without starting the WPF app.</summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options;
        return new AppDbContext(options);
    }
}
