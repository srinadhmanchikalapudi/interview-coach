namespace InterviewCoach.Core.Models;

public enum LlmRole { Planner, Interviewer, QuestionGenerator, Coach, Debrief }

public enum ChatTurnRole { User, Assistant }

public record ChatTurn(ChatTurnRole Role, string Content);

public record ConnectionTestResult(LlmRole Role, string ModelId, bool Success, string Message);

public record VoiceInfo(string Id, string DisplayName, string? Locale = null);
