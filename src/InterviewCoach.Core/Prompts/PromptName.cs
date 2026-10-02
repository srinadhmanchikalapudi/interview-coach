namespace InterviewCoach.Core.Prompts;

public enum PromptName { Planner, Interviewer, QuestionGenerator, Coach, Debrief, TechTags, QuestionBatch }

public static class PromptNames
{
    public static string FileName(this PromptName name) => name switch
    {
        PromptName.Planner => "planner.md",
        PromptName.Interviewer => "interviewer.md",
        PromptName.QuestionGenerator => "question_generator.md",
        PromptName.Coach => "coach.md",
        PromptName.Debrief => "debrief.md",
        PromptName.TechTags => "tech_tags.md",
        PromptName.QuestionBatch => "question_batch.md",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>The single user message each prompt is called with. The Interviewer has none (see the Mock engine).</summary>
    public static string? UserMessage(this PromptName name) => name switch
    {
        PromptName.Planner => "Create the interview plan.",
        PromptName.QuestionGenerator => "Give me the next question.",
        PromptName.Coach => "Coach this.",
        PromptName.Debrief => "Write the debrief.",
        PromptName.TechTags => "List the technologies.",
        PromptName.QuestionBatch => "Write the questions.",
        _ => null,
    };
}
