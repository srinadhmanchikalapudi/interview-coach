namespace InterviewCoach.Core.Models;

/// <summary>
/// What kind of job the interview is for. It changes what interviewers ask and what they listen for: a full-time hire is
/// judged on fundamentals, ownership, growth and fit over years, a contract hire on how quickly and independently they
/// can deliver in the exact stack, plus practical questions about availability and rate.
/// </summary>
public enum EmploymentType { FullTime, Contract }

public static class EmploymentTypes
{
    public static IReadOnlyList<EmploymentType> All { get; } = Enum.GetValues<EmploymentType>();

    public static string Label(this EmploymentType type) => type switch
    {
        EmploymentType.FullTime => "Full-time",
        EmploymentType.Contract => "Contract",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>One line shown on screen about what changes for this kind of interview.</summary>
    public static string Description(this EmploymentType type) => type switch
    {
        EmploymentType.FullTime =>
            "A broader loop: fundamentals, design and trade-offs, ownership, culture fit, and where you want to grow.",
        EmploymentType.Contract =>
            "Shorter and narrower: hands-on depth in your stack, how fast you become productive, working independently, and availability and rate.",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The value sent as {{EMPLOYMENT_TYPE}} in the prompts.</summary>
    public static string PromptValue(this EmploymentType type) => type.Label();
}
