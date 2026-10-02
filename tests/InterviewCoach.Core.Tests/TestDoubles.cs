using System.Text.RegularExpressions;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

internal sealed record LlmCall(LlmRole Role, string SystemPrompt, IReadOnlyList<ChatTurn> Messages)
{
    /// <summary>The system prompt with Windows line endings normalized, for stable assertions.</summary>
    public string Prompt => SystemPrompt.Replace("\r\n", "\n");
}

/// <summary>An LLM whose replies the test controls.</summary>
internal sealed class ScriptedLlmService(Func<LlmCall, Task<object>> handler) : ILlmService
{
    public List<LlmCall> Calls { get; } = [];

    public async Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
    {
        var call = new LlmCall(role, systemPrompt, messages);
        Calls.Add(call);
        return (T)await handler(call);
    }

    public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct)
        => throw new NotSupportedException();

    public IEnumerable<LlmCall> To(LlmRole role) => Calls.Where(c => c.Role == role);
    public IEnumerable<LlmCall> TagCalls => Calls.Where(c => c.Prompt.Contains(BankScript.TagsMarker));
    public IEnumerable<LlmCall> BankQuestionCalls => To(LlmRole.QuestionGenerator).Where(c => BankScript.FocusOf(c) != "(none)");
    public IEnumerable<LlmCall> BatchCalls => BankQuestionCalls.Where(c => c.Prompt.Contains(BankScript.BatchMarker));
    public IEnumerable<LlmCall> SingleQuestionCalls => BankQuestionCalls.Where(c => !c.Prompt.Contains(BankScript.BatchMarker));
    public IEnumerable<LlmCall> ModelQuestionCalls => To(LlmRole.QuestionGenerator).Where(c => !c.Prompt.Contains(BankScript.TagsMarker) && BankScript.FocusOf(c) == "(none)");
    public IEnumerable<LlmCall> GeneralAnswerCalls => To(LlmRole.Coach).Where(c => c.Prompt.Contains(TechBank.GeneralAnswerNote));
    public IEnumerable<LlmCall> TailoredAnswerCalls => To(LlmRole.Coach).Where(c => !c.Prompt.Contains(TechBank.GeneralAnswerNote));
}

/// <summary>
/// A stand-in model for the technology bank. It tells the kinds of call apart by the prompt:
/// tag extraction, a question about a focus technology, an ordinary question, and answers (general or tailored).
/// </summary>
internal sealed partial class BankScript
{
    public const string TagsMarker = "List the main technologies this job actually requires";
    public const string BatchMarker = "preparing a bank of technical screening questions";

    public string[] Technologies { get; set; } = ["C#", "SQL Server"];
    public bool FailTags { get; set; }
    public bool FailBankQuestions { get; set; }
    /// <summary>When the bank runs out the app asks for a batch; this many questions come back (the app asks for 10).</summary>
    public int BatchCount { get; set; } = 1;
    /// <summary>Makes the batch call fail, so the app falls back to writing one question.</summary>
    public bool FailBatches { get; set; }
    public bool FailGeneralAnswers { get; set; }

    private int _bankQuestions;
    private int _modelQuestions;

    [GeneratedRegex(@"<focus_technology>\n(.+?)\n</focus_technology>", RegexOptions.Singleline)]
    private static partial Regex Focus();

    public static string FocusOf(LlmCall call) => Focus().Match(call.Prompt) is { Success: true } m ? m.Groups[1].Value : "(none)";

    public Task<object> Handle(LlmCall call)
    {
        if (call.Prompt.Contains(TagsMarker))
        {
            if (FailTags) throw new LlmException("tag extraction failed");
            return Task.FromResult<object>(new TechTagsDto { Technologies = [.. Technologies] });
        }

        if (call.Prompt.Contains(BatchMarker))
        {
            if (FailBankQuestions || FailBatches) throw new LlmException("batch failed");
            var batchFocus = FocusOf(call);
            return Task.FromResult<object>(new QuestionBatchDto
            {
                Questions = Enumerable.Range(0, BatchCount)
                    .Select(_ => new BatchQuestionDto { Question = $"{batchFocus} concept question {++_bankQuestions}?", Area = $"{batchFocus} basics" })
                    .ToList(),
            });
        }

        if (call.Role == LlmRole.QuestionGenerator)
        {
            var focus = FocusOf(call);
            if (focus != "(none)")
            {
                if (FailBankQuestions) throw new LlmException("bank question failed");
                return Task.FromResult<object>(new QuestionDto
                {
                    Question = $"{focus} concept question {++_bankQuestions}?",
                    QuestionType = "technical_concept", Source = "fundamentals", Focus = $"{focus} basics",
                });
            }
            return Task.FromResult<object>(new QuestionDto
            {
                Question = $"Model question {++_modelQuestions}?", QuestionType = "behavioral", Source = "resume", Focus = "ownership",
            });
        }

        var general = call.Prompt.Contains(TechBank.GeneralAnswerNote);
        if (general && FailGeneralAnswers) throw new LlmException("general answer failed");
        return Task.FromResult<object>(new CoachOutput
        {
            WhatTheyreTesting = "signal",
            ModelAnswer = general ? "GENERAL answer" : "TAILORED answer",
            Shape = "A → B",
            FollowUps = [new FollowUp { Question = "Why that?", Hint = "hint" }],
        });
    }
}
