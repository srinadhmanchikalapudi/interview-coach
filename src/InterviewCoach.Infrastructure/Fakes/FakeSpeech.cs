using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Infrastructure.Fakes;

public sealed class FakeTextToSpeech : ITextToSpeech
{
    private CancellationTokenSource? _current;

    public List<string> Spoken { get; } = [];
    public int StopCount { get; private set; }

    /// <summary>Simulated playback time per character; zero keeps tests instant.</summary>
    public TimeSpan PerCharacter { get; init; } = TimeSpan.FromMilliseconds(15);

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        Spoken.Add(text);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _current = cts;
        try
        {
            if (PerCharacter > TimeSpan.Zero)
                await Task.Delay(PerCharacter * Math.Min(text.Length, 400), cts.Token);
        }
        catch (OperationCanceledException) { /* Stop() or caller cancellation ends playback, never an error */ }
        finally { _current = null; }
    }

    public void Stop()
    {
        StopCount++;
        try { _current?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<VoiceInfo>>([new VoiceInfo("demo", "Demo voice", "en-US")]);
}

public sealed class FakeSpeechToText(params string[] script) : ISpeechToText
{
    private readonly Queue<string> _script = new(script);

    public bool SupportsPartials => true;
    public bool IsListening { get; private set; }

    public event EventHandler<string>? PartialRecognized;
    public event EventHandler<string>? FinalRecognized;
    public event EventHandler<string>? Error;

    public Task StartAsync(CancellationToken ct)
    {
        IsListening = true;
        if (_script.TryPeek(out var next))
            PartialRecognized?.Invoke(this, next[..Math.Min(next.Length, Math.Max(1, next.Length / 2))]);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!IsListening) return Task.CompletedTask;
        IsListening = false;
        if (_script.TryDequeue(out var segment))
            FinalRecognized?.Invoke(this, segment);
        return Task.CompletedTask;
    }

    public void RaiseError(string message) => Error?.Invoke(this, message);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
