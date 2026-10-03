namespace InterviewCoach.Infrastructure.Persistence;

/// <summary>One answer given in Practice mode, with the Coach's feedback as JSON. Every attempt is its own row.</summary>
public class PracticeAttemptEntity
{
    public int Id { get; set; }
    public string Question { get; set; } = "";
    public string QuestionType { get; set; } = "";
    public string Technology { get; set; } = "";
    public string Seniority { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsFollowUp { get; set; }
    public string ParentQuestion { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public int AttemptNumber { get; set; } = 1;
    public string AnswerText { get; set; } = "";
    public string InputMethod { get; set; } = "typed";
    public int DurationSeconds { get; set; }
    public int WordCount { get; set; }
    public string CoachJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
