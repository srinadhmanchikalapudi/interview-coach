using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;

namespace InterviewCoach.App.Tests;

/// <summary>A recognizer that hears whatever the test says, when the test says it.</summary>
internal sealed class TestStt : ISpeechToText
{
    public bool SupportsPartials { get; init; } = true;
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public string? SayOnStop { get; set; }

    public event EventHandler<string>? PartialRecognized;
    public event EventHandler<string>? FinalRecognized;
    public event EventHandler<string>? Error;

    public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }

    public Task StopAsync()
    {
        Stopped = true;
        if (SayOnStop is { } text) FinalRecognized?.Invoke(this, text);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Partial(string text) => PartialRecognized?.Invoke(this, text);
    public void Final(string text) => FinalRecognized?.Invoke(this, text);
    public void Fail(string message) => Error?.Invoke(this, message);
}

/// <summary>A voice that holds until the test lets it finish, so a test can act while a line is being spoken.</summary>
internal sealed class HeldVoice : ITextToSpeech
{
    private TaskCompletionSource _release = new();
    public List<string> Spoken { get; } = [];
    public int StopCount { get; private set; }
    public bool Hold { get; set; }
    public Exception? Throw { get; set; }
    public CancellationToken LastToken { get; private set; }

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        Spoken.Add(text);
        LastToken = ct;
        if (Throw is not null) throw Throw;
        if (Hold)
        {
            _release = new TaskCompletionSource();
            await _release.Task;
        }
    }

    public void Release() => _release.TrySetResult();
    public void Stop() { StopCount++; _release.TrySetResult(); }
    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VoiceInfo>>([]);
}

internal sealed class TestSpeech : ISpeechFactory
{
    public HeldVoice Voice { get; } = new();
    public List<TestStt> Recognizers { get; } = [];
    public SpeechReadiness TextToSpeechReadiness { get; set; } = SpeechReadiness.Ready;
    public SpeechReadiness SpeechToTextReadiness { get; set; } = SpeechReadiness.Ready;
    public Func<TestStt> Next { get; set; } = () => new TestStt();
    public ITextToSpeech TextToSpeech => Voice;
    public TestStt Mic => Recognizers[^1];

    public ISpeechToText CreateSpeechToText()
    {
        var stt = Next();
        Recognizers.Add(stt);
        return stt;
    }
}
