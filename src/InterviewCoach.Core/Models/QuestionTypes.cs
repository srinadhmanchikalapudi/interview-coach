namespace InterviewCoach.Core.Models;

/// <summary>
/// The first seven apply to every interview. MotivationFit belongs to full-time interviews (why this company, where you want
/// to grow) and Engagement to contract interviews (availability, notice, rate, contract length).
/// </summary>
public enum QuestionType
{
    TellMeAboutYourself, ResumeDeepDive, TechnicalConcept, SystemDesign, CodingTalkthrough, Behavioral, Scenario,
    MotivationFit, Engagement,
}

public static class QuestionTypes
{
    public static IReadOnlyList<QuestionType> All { get; } = Enum.GetValues<QuestionType>();

    /// <summary>The identifier used inside the prompts (question_generator.md).</summary>
    public static string Id(this QuestionType type) => type switch
    {
        QuestionType.TellMeAboutYourself => "tell_me_about_yourself",
        QuestionType.ResumeDeepDive => "resume_deep_dive",
        QuestionType.TechnicalConcept => "technical_concept",
        QuestionType.SystemDesign => "system_design",
        QuestionType.CodingTalkthrough => "coding_talkthrough",
        QuestionType.Behavioral => "behavioral",
        QuestionType.Scenario => "scenario",
        QuestionType.MotivationFit => "motivation_fit",
        QuestionType.Engagement => "engagement",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Label(this QuestionType type) => type switch
    {
        QuestionType.TellMeAboutYourself => "Tell me about yourself",
        QuestionType.ResumeDeepDive => "Resume deep-dive",
        QuestionType.TechnicalConcept => "Technical concept",
        QuestionType.SystemDesign => "System design",
        QuestionType.CodingTalkthrough => "Coding talk-through",
        QuestionType.Behavioral => "Behavioral",
        QuestionType.Scenario => "Scenario-based",
        QuestionType.MotivationFit => "Motivation and fit",
        QuestionType.Engagement => "Availability and engagement",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The kind of interview a type is only asked in, or null when it applies to both.</summary>
    public static EmploymentType? OnlyFor(this QuestionType type) => type switch
    {
        QuestionType.MotivationFit => EmploymentType.FullTime,
        QuestionType.Engagement => EmploymentType.Contract,
        _ => null,
    };

    /// <summary>The types that can be asked in this kind of interview: the common ones plus its own.</summary>
    public static IReadOnlyList<QuestionType> Applicable(EmploymentType employment)
        => All.Where(t => t.OnlyFor() is null || t.OnlyFor() == employment).ToList();

    /// <summary>Label for an id the model returned; unknown ids are made readable instead of dropped.</summary>
    public static string LabelFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var trimmed = id.Trim();
        foreach (var type in All)
            if (type.Id().Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return type.Label();
        var spaced = trimmed.Replace('_', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>Value for {{QUESTION_TYPES}}: "Any" when nothing is filtered, otherwise one id per line.</summary>
    public static string RenderFilter(IReadOnlyCollection<QuestionType> allowed)
        => allowed.Count == 0 || allowed.Distinct().Count() == All.Count
            ? "Any"
            : string.Join("\n", allowed.Distinct().OrderBy(t => t).Select(t => "- " + t.Id()));
}
