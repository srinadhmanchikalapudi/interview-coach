using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>A saved technical-concept question. The keys are lower-cased / normalized copies used for matching.</summary>
public class TechQuestionEntity
{
    public int Id { get; set; }
    public string TechnologyKey { get; set; } = "";
    public string Technology { get; set; } = "";
    public Seniority Seniority { get; set; }
    public string QuestionKey { get; set; } = "";
    public string Question { get; set; } = "";
    public string Focus { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<TechAnswerEntity> Answers { get; set; } = [];
}

/// <summary>A saved general answer to a bank question, for one requested length (0 = interviewer norm).</summary>
public class TechAnswerEntity
{
    public int Id { get; set; }
    public int TechQuestionId { get; set; }
    public int AnswerWords { get; set; }
    public string CoachJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>The employers, projects and highlights read from one resume, identified by a fingerprint of its text.</summary>
public class ResumeTopicsEntity
{
    public string Fingerprint { get; set; } = "";
    public string TopicsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}

/// <summary>The technologies found in one job description, identified by a fingerprint of its text.</summary>
public class JdTechnologiesEntity
{
    public string Fingerprint { get; set; } = "";
    public string TechnologiesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}

/// <summary>The technologies fetched for one job role, saved so the model is asked only once per role.</summary>
public class RoleTechnologiesEntity
{
    /// <summary>The role, lower case with spaces tidied (see TechBank.RoleKey).</summary>
    public string RoleKey { get; set; } = "";
    /// <summary>The role as it was typed, for display.</summary>
    public string Role { get; set; } = "";
    public string TechnologiesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}
