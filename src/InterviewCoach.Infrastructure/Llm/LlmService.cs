using System.Text.Json;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace InterviewCoach.Infrastructure.Llm;

public sealed class LlmService(ISettingsStore settings, IChatClientFactory clients, string? logDirectory = null) : ILlmService
{
    private const int MaxOutputTokens = 4096;
    private readonly string _logDirectory = logDirectory ?? Path.Combine(Settings.SettingsStore.AppDataDirectory, "logs");

    private static TimeSpan TimeoutFor(LlmRole role) => role switch
    {
        LlmRole.Interviewer => TimeSpan.FromSeconds(30),
        LlmRole.Coach or LlmRole.Debrief => TimeSpan.FromSeconds(90),
        _ => TimeSpan.FromSeconds(60),
    };

    public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
        => GetJsonAsync<T>(settings.Current, role, systemPrompt, messages, ct);

    public async Task<T> GetJsonAsync<T>(AppSettings s, LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
    {
        var model = s.ModelFor(role);
        var requestSettings = ForRole(s, role);
        var client = clients.Create(requestSettings, model);

        var chat = new List<ChatMessage> { BuildSystemMessage(s, systemPrompt) };
        chat.AddRange(messages.Select(m => new ChatMessage(m.Role == ChatTurnRole.User ? ChatRole.User : ChatRole.Assistant, m.Content)));

        var options = OptionsFor(requestSettings, model);
        var reply = await SendAsync(client, role, chat, options, ct);
        Log(s, role, model, "system", systemPrompt, messages, reply);
        try
        {
            return JsonResponseParser.Parse<T>(reply.Text);
        }
        catch (JsonException first)
        {
            // One repair retry: show the model its own bad output and the parser's complaint.
            chat.Add(new ChatMessage(ChatRole.Assistant, reply.Text));
            chat.Add(new ChatMessage(ChatRole.User,
                $"That wasn't valid JSON matching the schema. Error: {first.Message}. Reply with only the corrected JSON."));

            var repaired = await SendAsync(client, role, chat, options, ct);
            Log(s, role, model, "REPAIR RETRY (the first reply was not valid JSON)", "(repair retry)", [], repaired);
            try
            {
                return JsonResponseParser.Parse<T>(repaired.Text);
            }
            catch (JsonException second)
            {
                throw new LlmException($"The model's reply wasn't valid JSON even after a retry: {second.Message}", second);
            }
        }
    }

    public async Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings s, CancellationToken ct)
    {
        const string system = "This is a connectivity check. Reply with only this JSON: {\"ok\": true}";
        var ping = new[] { new ChatTurn(ChatTurnRole.User, "ping") };

        // One check per distinct model; label it with the first role that uses it.
        var targets = Enum.GetValues<LlmRole>()
            .GroupBy(s.ModelFor)
            .Select(g => (Model: g.Key, Role: g.First(), Roles: string.Join(", ", g)))
            .ToList();

        var results = new List<ConnectionTestResult>();
        foreach (var t in targets)
        {
            try
            {
                await GetJsonAsync<PingResponse>(s, t.Role, system, ping, ct);
                results.Add(new ConnectionTestResult(t.Role, t.Model, true, $"OK ({t.Roles})"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new ConnectionTestResult(t.Role, t.Model, false, $"{t.Roles}: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>
    /// Settings as they apply to one role. On OpenRouter a thinking effort makes models that think only when asked start
    /// thinking: a logged Gemini 3.5 Flash Lite call spent about 500 of its 550 output tokens thinking about a 25-token
    /// question, and took twice as long as Haiku. The question generator and the interviewer (short, fast replies) therefore
    /// send no effort there; the coach, planner and debrief still do.
    /// </summary>
    public static AppSettings ForRole(AppSettings s, LlmRole role)
    {
        var fastRole = role is LlmRole.QuestionGenerator or LlmRole.Interviewer;
        if (!fastRole || s.Provider != LlmProvider.OpenRouter || s.ThinkingEffort == ThinkingEffort.ModelDefault)
            return s;
        var copy = s.Clone();
        copy.ThinkingEffort = ThinkingEffort.ModelDefault;
        return copy;
    }

    /// <summary>One model reply plus what is needed to tell why a call was slow: wall-clock time, token counts, stop reason.</summary>
    private sealed record Reply(string Text, TimeSpan Elapsed, long? InputTokens, long? OutputTokens, long? ReasoningTokens, long? CacheReadTokens, long? CacheWriteTokens, string? FinishReason);

    /// <summary>The Coach prompt keeps its fixed guidance first; this header starts the part that changes from request to request.</summary>
    internal const string StaticBoundary = "=== THE CANDIDATE AND THE QUESTION ===";

    /// <summary>The job description and resume end here. Everything after it changes from call to call.</summary>
    internal const string CacheBoundary = "</candidate_resume>";

    /// <summary>
    /// The system message. For Anthropic it is split at up to two points, each ending a block that carries a cache marker:
    /// the fixed guidance (identical for every call, so every user and session shares it) and the job description plus
    /// resume (identical within a profile). Repeat calls pay about a tenth of the normal input price for those blocks.
    /// Anthropic ignores a marker on a block under its minimum size, so this is always safe to send.
    /// </summary>
    public static ChatMessage BuildSystemMessage(AppSettings s, string systemPrompt)
    {
        if (!s.PromptCaching || s.Provider != LlmProvider.Anthropic)
            return new ChatMessage(ChatRole.System, systemPrompt);

        var cuts = new List<int>();
        var fixedEnds = systemPrompt.IndexOf(StaticBoundary, StringComparison.Ordinal);
        if (fixedEnds > 0) cuts.Add(fixedEnds);
        var resumeEnds = systemPrompt.IndexOf(CacheBoundary, StringComparison.Ordinal);
        if (resumeEnds >= 0) cuts.Add(resumeEnds + CacheBoundary.Length);
        cuts = cuts.Where(c => c < systemPrompt.Length).Distinct().Order().ToList();
        if (cuts.Count == 0)
            return new ChatMessage(ChatRole.System, systemPrompt);

        var parts = new List<AIContent>();
        var start = 0;
        foreach (var cut in cuts)
        {
            parts.Add(new TextContent(systemPrompt[start..cut]).WithCacheControl(Ttl.Ttl5m));
            start = cut;
        }
        parts.Add(new TextContent(systemPrompt[start..])); // the part that changes every call: never cached
        return new ChatMessage(ChatRole.System, parts);
    }

    /// <summary>
    /// Request options for a model. Newer models think before answering, which can be most of the wait for a short
    /// JSON reply. When the user picks a lower effort it is sent as the adaptive-thinking effort level.
    /// Haiku is left alone: it predates adaptive thinking and does not take that setting.
    /// </summary>
    public static ChatOptions OptionsFor(AppSettings s, string model)
    {
        var options = new ChatOptions { MaxOutputTokens = MaxOutputTokens };
        var effort = s.ThinkingEffort switch
        {
            ThinkingEffort.Low => ReasoningEffort.Low,
            ThinkingEffort.Medium => ReasoningEffort.Medium,
            ThinkingEffort.High => ReasoningEffort.High,
            _ => (ReasoningEffort?)null,
        };
        var isHaiku = model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
        if (effort is { } e && s.Provider == LlmProvider.Anthropic && !isHaiku)
            options.Reasoning = new ReasoningOptions { Effort = e };
        return options;
    }

    // The adapter reports tokens written to the cache as an extra count; the exact key is not part of the abstraction.
    private static long? CacheWrite(UsageDetails? usage)
    {
        if (usage?.AdditionalCounts is not { } counts) return null;
        foreach (var (key, value) in counts)
            if (key.Contains("CacheCreation", StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    private async Task<Reply> SendAsync(IChatClient client, LlmRole role, List<ChatMessage> chat, ChatOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeoutFor(role));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(chat, options, timeout.Token);
            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
                throw new LlmException("The model returned an empty reply.");
            return new Reply(text, clock.Elapsed, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
                response.Usage?.ReasoningTokenCount, response.Usage?.CachedInputTokenCount, CacheWrite(response.Usage), response.FinishReason?.Value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"The {role} call timed out after {TimeoutFor(role).TotalSeconds:0} seconds.");
        }
        catch (LlmException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LlmException(ex.Message, ex);
        }
    }

    // Prompts contain the resume and JD, so logging is opt-in (Settings, Debug logging).
    private void Log(AppSettings s, LlmRole role, string model, string note, string system, IReadOnlyList<ChatTurn> messages, Reply reply)
    {
        if (!s.DebugLogging) return;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var path = Path.Combine(_logDirectory, $"llm-{DateTime.Now:yyyyMMdd}.log");
            var entry = $"""
                ===== {DateTime.Now:O} {role} ({model}) =====
                took {reply.Elapsed.TotalSeconds:0.0}s | input tokens {reply.InputTokens?.ToString() ?? "?"} | output tokens {reply.OutputTokens?.ToString() ?? "?"} (thinking: {reply.ReasoningTokens?.ToString() ?? "not reported"}) | cache read {reply.CacheReadTokens?.ToString() ?? "0"} write {reply.CacheWriteTokens?.ToString() ?? "0"} | reply text {reply.Text.Length} chars | finish {reply.FinishReason ?? "?"} | {note}
                --- system ---
                {system}
                --- messages ---
                {string.Join(Environment.NewLine, messages.Select(m => $"[{m.Role}] {m.Content}"))}
                --- response ---
                {reply.Text}


                """;
            File.AppendAllText(path, entry);
        }
        catch (IOException) { /* logging must never break a call */ }
    }

    private sealed record PingResponse(bool Ok);
}
