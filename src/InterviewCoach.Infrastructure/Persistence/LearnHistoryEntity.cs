namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>One question and answer shown in Learn mode (the library). The keys are normalized copies used to find an existing entry.</summary>
public class LearnHistoryEntity
{
    public int Id { get; set; }
    public string QuestionKey { get; set; } = "";
    public string Question { get; set; } = "";
    public string QuestionType { get; set; } = "";
    public string Technology { get; set; } = "";
    public string Seniority { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsFollowUp { get; set; }
    public string ParentQuestion { get; set; } = "";
    public string ParentKey { get; set; } = "";
    public bool IsGeneral { get; set; }
    public string ProfileName { get; set; } = "";
    public string CoachJson { get; set; } = "";
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int TimesSeen { get; set; } = 1;
}
