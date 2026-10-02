using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Llm;

/// <summary>The ILlmService the app resolves: routes to the fake when Demo mode is on, otherwise to the real provider.</summary>
public sealed class RoutingLlmService(ISettingsStore settings, LlmService real, Fakes.FakeLlmService fake) : ILlmService
{
    public Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
        => (settings.Current.DemoMode ? (ILlmService)fake : real).GetJsonAsync<T>(role, systemPrompt, messages, ct);

    public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings s, CancellationToken ct)
        => (s.DemoMode ? (ILlmService)fake : real).TestConnectionAsync(s, ct);
}
