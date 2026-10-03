namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>A mock interview: the plan, the conversation, the debrief and the coaching of each question, as JSON.</summary>
public class MockSessionEntity
{
    public int Id { get; set; }
    public string ProfileName { get; set; } = "";
    public string JobRole { get; set; } = "";
    public string RoundType { get; set; } = "";
    public int DurationMinutes { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int ElapsedSeconds { get; set; }
    public bool Finished { get; set; }
    public string PlanJson { get; set; } = "";
    public string TurnsJson { get; set; } = "[]";
    public string DebriefJson { get; set; } = "";
    public string ThreadsJson { get; set; } = "";
    public string HireSignal { get; set; } = "";
}
