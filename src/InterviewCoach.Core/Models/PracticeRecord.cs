namespace InterviewCoach.Core.Models;

/// <summary>
/// One answer given in Practice mode, with the Coach's feedback on it, kept so it can be read again in the Library. Every attempt
/// is its own record: answering the same question again later, or trying a question again in the same session, adds another.
/// </summary>
public sealed class PracticeRecord
{
    public int Id { get; set; }
    public required string Question { get; init; }
    public string QuestionType { get; init; } = "";
    public string? Technology { get; init; }
    public string? Seniority { get; init; }
    public string Source { get; init; } = "";
    public bool IsFollowUp { get; init; }
    public string? ParentQuestion { get; init; }
    public string? ProfileName { get; init; }
    /// <summary>1 for the first answer to the question in a session, 2 for the second try, and so on.</summary>
    public int AttemptNumber { get; init; } = 1;
    public required string AnswerText { get; init; }
    public string InputMethod { get; init; } = "typed";
    public int DurationSeconds { get; init; }
    public int WordCount { get; init; }
    public required CoachOutput Coach { get; init; }
    public DateTime CreatedAt { get; set; }
}
