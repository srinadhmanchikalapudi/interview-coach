namespace InterviewCoach.Core.Models;

/// <summary>An LLM call failed in a way the UI should show to the user (with a Retry button), not crash on.</summary>
public class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
    public LlmException(string message, Exception inner) : base(message, inner) { }
}
