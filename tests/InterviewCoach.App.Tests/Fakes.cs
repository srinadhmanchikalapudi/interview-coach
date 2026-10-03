using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.App.Tests;

internal sealed class InMemoryProfileRepository : IProfileRepository
{
    private readonly List<CandidateProfile> _items = [];
    private int _nextId = 1;
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Task<IReadOnlyList<CandidateProfile>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CandidateProfile>>(_items.OrderByDescending(p => p.UpdatedAt).Select(p => p.Clone()).ToList());

    public Task<CandidateProfile?> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(p => p.Id == id)?.Clone());

    public Task<CandidateProfile> SaveAsync(CandidateProfile profile, CancellationToken ct = default)
    {
        var stored = profile.Clone();
        stored.UpdatedAt = _clock = _clock.AddMinutes(1);
        if (stored.Id == 0)
        {
            stored.Id = _nextId++;
            stored.CreatedAt = _clock;
            _items.Add(stored);
        }
        else
        {
            _items[_items.FindIndex(p => p.Id == stored.Id)] = stored;
        }
        return Task.FromResult(stored.Clone());
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _items.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }
}

internal sealed class StubExtractor : IDocumentTextExtractor
{
    public IReadOnlyList<string> SupportedExtensions { get; } = [".txt"];
    public Task<string> ExtractTextAsync(string path, CancellationToken ct = default) => Task.FromResult("extracted text");
}

internal sealed class ScriptedDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Confirmations { get; } = [];
    public string? FileToPick { get; set; }

    public string? PickFile(string title, string filter) => FileToPick;

    /// <summary>The path the "user" chooses in a save dialog (null to cancel). The last name offered is kept.</summary>
    public string? FileToSave { get; set; }
    public string? OfferedFileName { get; private set; }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        OfferedFileName = defaultName;
        return FileToSave;
    }

    public bool Confirm(string title, string message)
    {
        Confirmations.Add(title);
        return ConfirmAnswer;
    }
}

internal static class Samples
{
    public static CandidateProfile CompleteProfile(string name = "Acme backend") => new()
    {
        Name = name,
        JobRole = "Senior Backend Engineer",
        Seniority = Seniority.Senior,
        JobDescription = "Build and run services.",
        ResumeText = "Built the ranking service.",
    };
}

internal sealed class FakeCatalog(params OpenRouterModel[] models) : IOpenRouterCatalog
{
public bool Fail { get; set; }
public int Calls { get; private set; }
public bool LastRefresh { get; private set; }

public Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(bool refresh, CancellationToken ct = default)
{
    Calls++;
    LastRefresh = refresh;
    if (Fail) throw new LlmException("Could not reach OpenRouter to load its model list: no network");
    return Task.FromResult<IReadOnlyList<OpenRouterModel>>(models);
}
}

/// <summary>A model that answers questions and coaching with fixed, readable output, for the Practice tests and screenshots.</summary>
internal sealed class StubLlm : ILlmService
{
    public List<(LlmRole Role, string Prompt)> Calls { get; } = [];
    public string QuestionType { get; set; } = "behavioral";
    /// <summary>The question to ask; empty means "Practice question N?".</summary>
    public string QuestionText { get; set; } = "";
    public bool FailCoach { get; set; }
    public bool ThrowUnexpected { get; set; }
    private int _questions;

    public IEnumerable<string> CoachPrompts => Calls.Where(c => c.Role == LlmRole.Coach).Select(c => c.Prompt);

    public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
    {
        Calls.Add((role, systemPrompt));
        if (role == LlmRole.QuestionGenerator)
            return Task.FromResult((T)(object)new QuestionDto { Question = QuestionText.Length > 0 ? QuestionText : $"Practice question {++_questions}?", QuestionType = QuestionType, Focus = "ownership", Source = "resume" });

        if (ThrowUnexpected) throw new InvalidOperationException("something unexpected");
        if (FailCoach) throw new LlmException("the coach is unavailable");
        return Task.FromResult((T)(object)new CoachOutput
        {
            WhatTheyreTesting = "ownership and results",
            Feedback = [new FeedbackPoint { Kind = "strength", Point = "You gave a number.", Quote = "cut p99 to 80ms" }, new FeedbackPoint { Kind = "fix", Point = "Say what you chose." }],
            ModelAnswer = "I cut p99 by caching the hot lookups.",
            Shape = "Direct answer → the constraint → result",
            Delivery = "A good length.",
            FollowUps = [new FollowUp { Question = "Why Redis?", Hint = "Name the alternative." }, new FollowUp { Question = "How did you measure it?", Hint = "Say the metric." }],
        });
    }

    public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct) => throw new NotSupportedException();
}
