namespace InterviewCoach.Core.Models;

public enum Seniority { Junior, Mid, Senior, Staff }

public class CandidateProfile
{
    /// <summary>Above this the resume/JD is probably too long to send on every LLM call; the UI warns and offers to trim.</summary>
    public const int LongTextThreshold = 40_000;

    /// <summary>The most characters of answer rules kept and sent (about 250 words).</summary>
    public const int MaxAnswerRulesLength = 1_500;

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string JobRole { get; set; } = "";
    public Seniority Seniority { get; set; } = Seniority.Mid;
    public string JobDescription { get; set; } = "";
    public string ResumeText { get; set; } = "";

    /// <summary>The candidate's own rules for how answers should be written. Optional; sent to the Coach and the Debrief only, and they win over the built-in style guidance.</summary>
    public string AnswerRules { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The three things a session needs before it can start (spec 4.1).</summary>
    public IReadOnlyList<string> MissingForStart()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(JobRole)) missing.Add("job role");
        if (string.IsNullOrWhiteSpace(JobDescription)) missing.Add("job description");
        if (string.IsNullOrWhiteSpace(ResumeText)) missing.Add("resume");
        return missing;
    }

    public CandidateProfile Clone() => (CandidateProfile)MemberwiseClone();
}
