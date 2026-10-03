namespace InterviewCoach.Core.Models;

/// <summary>
/// A mock interview kept for the History screen: who it was for, the plan, the whole conversation, the debrief and the coaching of each
/// question. The parts are stored as JSON, written by <c>MockEngine</c>; they are never edited afterwards.
/// </summary>
public sealed class MockRecord
{
    public int Id { get; set; }
    public string ProfileName { get; init; } = "";
    public string JobRole { get; init; } = "";
    public string RoundType { get; init; } = "";
    public int DurationMinutes { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public int ElapsedSeconds { get; set; }
    /// <summary>True when the interviewer closed the round or the user ended it; false for one that was abandoned.</summary>
    public bool Finished { get; set; }
    public string? PlanJson { get; init; }
    public string TurnsJson { get; set; } = "[]";
    public string? DebriefJson { get; set; }
    public string? ThreadsJson { get; set; }
    public string? HireSignal { get; set; }
}
