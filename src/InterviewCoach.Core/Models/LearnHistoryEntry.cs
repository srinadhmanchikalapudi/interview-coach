namespace InterviewCoach.Core.Models;

/// <summary>
/// One question and answer that Learn mode showed, kept so it can be revisited later. The same question seen again updates
/// its entry (and <see cref="TimesSeen"/>) instead of adding a copy. A general answer and one tailored to a resume are
/// separate entries, and so is the same follow-up question under a different parent.
/// </summary>
public sealed class LearnHistoryEntry
{
    public int Id { get; set; }
    public required string Question { get; init; }
    /// <summary>The prompt id of the question type, for example "behavioral".</summary>
    public string QuestionType { get; init; } = "";
    public string? Technology { get; init; }
    public string? Seniority { get; init; }
    public string Source { get; init; } = "";
    public bool IsFollowUp { get; init; }
    public string? ParentQuestion { get; init; }
    /// <summary>True when the answer was written without a resume (the kind saved in the technology bank).</summary>
    public bool IsGeneral { get; init; }
    /// <summary>The profile the answer was written for, for tailored answers.</summary>
    public string? ProfileName { get; init; }
    public required CoachOutput Coach { get; init; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int TimesSeen { get; set; } = 1;
}
