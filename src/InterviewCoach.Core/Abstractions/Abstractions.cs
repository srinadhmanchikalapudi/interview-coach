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

/// <summary>
/// The library: every question and answer Learn mode has shown, so they can be looked at again. Recording is best effort and
/// must never interrupt a session.
/// </summary>
public interface ILearnHistory
{
    /// <summary>Adds the entry, or updates the existing one for the same question and answer kind (last seen, times seen, answer).</summary>
    Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default);

    /// <summary>Everything recorded, most recently seen first.</summary>
    Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Every answer given in Practice mode with its feedback, so the Library can show it again. Recording is best effort and must never
/// interrupt a session.
/// </summary>
public interface IPracticeHistory
{
    /// <summary>Adds the attempt as a new record (every attempt is kept).</summary>
    Task RecordAsync(PracticeRecord record, CancellationToken ct = default);

    /// <summary>Everything recorded, newest first.</summary>
    Task<IReadOnlyList<PracticeRecord>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Storage for finished mock interviews. Recording is best effort: a failure here must never interrupt an interview or its debrief.
/// </summary>
public interface IMockHistory
{
    /// <summary>Adds the interview and returns its id.</summary>
    Task<int> AddAsync(MockRecord record, CancellationToken ct = default);

    /// <summary>Replaces what changed after the interview ended: the debrief, the coaching of each question, the signal.</summary>
    Task UpdateAsync(MockRecord record, CancellationToken ct = default);

    /// <summary>Everything recorded, newest first.</summary>
    Task<IReadOnlyList<MockRecord>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>Whether a speech feature can be used right now, and if not, what to tell the user to do about it.</summary>
public sealed record SpeechReadiness(bool IsReady, string Message)
{
    public static SpeechReadiness Ready { get; } = new(true, "");
    public static SpeechReadiness NotReady(string message) => new(false, message);
}

/// <summary>
/// The speech services chosen in Settings. It is asked each time, so changing a provider, key or voice takes effect at once. In Demo mode
/// it hands out fakes that need no keys and make no sound.
/// </summary>
public interface ISpeechFactory
{
    /// <summary>Speaks text. The same instance is returned until the settings that affect it change.</summary>
    ITextToSpeech TextToSpeech { get; }

    /// <summary>A new recognizer for one listening session.</summary>
    ISpeechToText CreateSpeechToText();

    SpeechReadiness TextToSpeechReadiness { get; }
    SpeechReadiness SpeechToTextReadiness { get; }
}
