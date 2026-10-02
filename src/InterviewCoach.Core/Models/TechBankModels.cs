namespace InterviewCoach.Core.Models;

/// <summary>Reply from tech_tags.md.</summary>
public class TechTagsDto
{
    public List<string> Technologies { get; init; } = [];
}

/// <summary>A technical-concept question saved for reuse. It never depends on a resume or a job description.</summary>
public class TechQuestion
{
    public int Id { get; set; }
    /// <summary>As the model named it, for display ("SQL Server"). Lookups ignore case.</summary>
    public string Technology { get; set; } = "";
    public Seniority Seniority { get; set; }
    public string Question { get; set; } = "";
    public string Focus { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public record TechBankStats(int Questions, int Answers);
