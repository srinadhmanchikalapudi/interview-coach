using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Prompts;

/// <summary>Builds the variable dictionaries the prompts are rendered with (spec section 8).</summary>
public static class PromptVars
{
    /// <summary>JOB_ROLE, SENIORITY, JOB_DESCRIPTION and RESUME: available to every prompt.</summary>
    public static Dictionary<string, string?> ForProfile(CandidateProfile profile) => new()
    {
        ["JOB_ROLE"] = profile.JobRole,
        ["SENIORITY"] = profile.Seniority.ToString(),
        ["JOB_DESCRIPTION"] = profile.JobDescription,
        ["RESUME"] = profile.ResumeText,
        ["ANSWER_RULES"] = AnswerRules(profile.AnswerRules),
    };

    /// <summary>
    /// Variables for prompts written without any person's details, so the result can be saved and reused by anyone.
    /// JOB_DESCRIPTION and RESUME are left absent (they render as "(none)"), and the role is the neutral "software engineer".
    /// </summary>
    public static Dictionary<string, string?> Generic(Seniority seniority) => new()
    {
        ["JOB_ROLE"] = "software engineer",
        ["SENIORITY"] = seniority.ToString(),
        ["JOB_DESCRIPTION"] = null,
        ["RESUME"] = null,
        ["ANSWER_RULES"] = null,
    };

    /// <summary>The candidate's answer rules, trimmed and cut to <see cref="CandidateProfile.MaxAnswerRulesLength"/>; null when there are none (the prompts then show "(none)").</summary>
    public static string? AnswerRules(string? rules)
    {
        var text = rules?.Replace("</candidate_rules>", "", StringComparison.OrdinalIgnoreCase).Trim(); // the rules cannot close their own block
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= CandidateProfile.MaxAnswerRulesLength ? text : text[..CandidateProfile.MaxAnswerRulesLength];
    }

    /// <summary>A numbered list of the questions asked so far, or "(none)" (spec section 9).</summary>
    public static string AlreadyAsked(IReadOnlyList<string> questions)
        => questions.Count == 0
            ? PromptRenderer.Absent
            : string.Join("\n", questions.Select((q, i) => $"{i + 1}. {q}"));
}
