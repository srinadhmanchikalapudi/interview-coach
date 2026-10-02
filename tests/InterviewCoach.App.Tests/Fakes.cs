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
