namespace InterviewCoach.Core.Models;

/// <summary>One thing the resume says was done, with the employer (or client) and project it belongs to. Questions are spread over these.</summary>
public sealed record ResumeTopic(string Employer, string Project, string Highlight)
{
    /// <summary>The employer, plus the project when there is one: "Globex, monolith migration".</summary>
    public string Where => string.IsNullOrWhiteSpace(Project) ? Employer : $"{Employer}, {Project}";
}

/// <summary>The part of the resume a question should be about, and the word it should begin with.</summary>
public sealed record ResumeFocus(ResumeTopic Topic, string Word)
{
    /// <summary>The text the question prompt receives. It is concrete on purpose: asking the model to "vary" its questions did not work.</summary>
    public string Describe() =>
        $"Ask about this part of the resume: {Topic.Where}: {Topic.Highlight}.\n" +
        $"Begin the question with the word \"{Word}\". Keep it to one short sentence and name the employer or project inside it; " +
        $"do not start with \"At {Topic.Employer}, you\" or list the technologies.";
}
