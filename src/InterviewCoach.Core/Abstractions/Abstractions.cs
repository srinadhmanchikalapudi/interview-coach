using InterviewCoach.Core.Models;
using InterviewCoach.Core.Prompts;

namespace InterviewCoach.Core.Abstractions;

public interface ILlmService
{
    /// <summary>Sends system prompt + messages, expects JSON matching T. Handles parsing and one repair retry.</summary>
    Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct);

    /// <summary>Pings every distinct model configured in <paramref name="settings"/> (Settings, Test connection).</summary>
    Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct);
}

public interface IPromptLibrary
{
    /// <summary>Renders the named prompt. Throws if any {{VAR}} is left unresolved.</summary>
    string Render(PromptName name, IReadOnlyDictionary<string, string?> vars);
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event EventHandler? Changed;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface ISpeechToText : IAsyncDisposable
{
    bool SupportsPartials { get; }
    event EventHandler<string>? PartialRecognized;
    event EventHandler<string>? FinalRecognized;
    event EventHandler<string>? Error;
    Task StartAsync(CancellationToken ct);
    /// <summary>For record-then-transcribe providers, this is where FinalRecognized fires.</summary>
    Task StopAsync();
}

public interface ITextToSpeech
{
    /// <summary>Completes when playback ends or is cancelled.</summary>
    Task SpeakAsync(string text, CancellationToken ct);
    void Stop();
    Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct);
}

/// <summary>The list of models OpenRouter offers, with prices. Used by Settings to help choose a model; the list is public and needs no key.</summary>
public interface IOpenRouterCatalog
{
    /// <summary>Models that take text and answer in text, sorted by name. The list is kept for the rest of the run unless <paramref name="refresh"/> is set.</summary>
    Task<IReadOnlyList<OpenRouterModel>> GetModelsAsync(bool refresh, CancellationToken ct = default);
}
