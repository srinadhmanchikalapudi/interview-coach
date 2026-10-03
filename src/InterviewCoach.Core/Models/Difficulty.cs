namespace InterviewCoach.Core.Models;

/// <summary>
/// How hard the questions of a technology concepts session are. It is the level the saved technical questions are kept at, so the
/// three choices map onto the three levels a question bank already has: Beginner is Junior, Medium is Mid and Advanced is Senior.
/// </summary>
public enum Difficulty { Beginner, Medium, Advanced }

public static class Difficulties
{
    public static IReadOnlyList<Difficulty> All { get; } = Enum.GetValues<Difficulty>();

    public static string Label(this Difficulty difficulty) => difficulty switch
    {
        Difficulty.Beginner => "Beginner",
        Difficulty.Medium => "Medium",
        Difficulty.Advanced => "Advanced",
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
    };

    /// <summary>One line shown on screen about what each level asks.</summary>
    public static string Description(this Difficulty difficulty) => difficulty switch
    {
        Difficulty.Beginner => "Fundamentals: what it is, what it is for, and the everyday basics.",
        Difficulty.Medium => "Working knowledge: how it behaves in real projects, common problems and trade-offs.",
        Difficulty.Advanced => "Depth: internals, performance, failure cases, design decisions and how you would diagnose them.",
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
    };

    /// <summary>The level the question bank and the prompts use for this difficulty.</summary>
    public static Seniority ToSeniority(this Difficulty difficulty) => difficulty switch
    {
        Difficulty.Beginner => Seniority.Junior,
        Difficulty.Medium => Seniority.Mid,
        Difficulty.Advanced => Seniority.Senior,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty)),
    };
}

/// <summary>
/// A technology concepts session has no resume or job description, but the Learn and Practice engines work on a profile, so one is made
/// up for the session: the role, a level from the difficulty and notes in place of the two documents (the prompts then judge an answer on
/// its technical content only). It is never saved.
/// </summary>
public static class ConceptSession
{
    public const string DefaultRole = "software engineer";

    public const string NoJobDescription = "(No job description: this is a technology concepts session. Ask about the technology itself.)";

    public const string NoResume = "(No resume: this is a technology concepts session. Judge the answer on its technical content only.)";

    public static CandidateProfile Profile(string? role, Difficulty difficulty)
    {
        var name = string.IsNullOrWhiteSpace(role) ? DefaultRole : role.Trim();
        return new CandidateProfile
        {
            Name = $"Concepts: {name}",
            JobRole = name,
            Seniority = difficulty.ToSeniority(),
            JobDescription = NoJobDescription,
            ResumeText = NoResume,
        };
    }

    /// <summary>True for a profile made by <see cref="Profile"/>, which has nothing to tailor an answer to.</summary>
    public static bool IsConceptProfile(CandidateProfile profile) => profile.ResumeText == NoResume;
}
