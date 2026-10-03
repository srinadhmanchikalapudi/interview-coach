using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Speech;

namespace InterviewCoach.App.Services;

/// <summary>What became of one thing said aloud.</summary>
/// <param name="Completed">True when it was heard to the end: not stopped, not failed.</param>
/// <param name="Error">A sentence for the user when the voice failed, otherwise null.</param>
public readonly record struct SpeakOutcome(bool Completed, string? Error = null);

/// <summary>
/// Says text aloud through the chosen voice, one thing at a time: saying something new stops what is being said. Used by Practice (the
/// question, the model answer) and Mock Interview (the interviewer). A voice that fails is reported as text for the screen to show; it never throws.
/// </summary>
public sealed class Speaker(ISpeechFactory speech)
{
    private CancellationTokenSource? _current;

    public bool IsSpeaking => _current is not null;

    /// <summary>Raised when speaking starts or ends.</summary>
    public event Action? Changed;

    public SpeechReadiness Readiness => speech.TextToSpeechReadiness;

    /// <summary>Says the text (formatting characters removed) and completes when it ends or is stopped.</summary>
    public async Task<SpeakOutcome> SpeakAsync(string text)
    {
        var spoken = SpeechText.ForSpeaking(text);
        if (spoken.Length == 0) return new SpeakOutcome(false);

        Stop();
        var cts = new CancellationTokenSource();
        _current = cts;
        Changed?.Invoke();
        try
        {
            await speech.TextToSpeech.SpeakAsync(spoken, cts.Token);
            return new SpeakOutcome(!cts.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            return new SpeakOutcome(false);
        }
        catch (Exception ex)
        {
            return new SpeakOutcome(false, ex is SpeechException ? ex.Message : "The voice stopped: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_current, cts))
            {
                _current = null;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Stops what is being said. Nothing happens when nothing is.</summary>
    public void Stop()
    {
        var current = _current;
        if (current is null) return;
        _current = null;
        try { current.Cancel(); }
        catch (ObjectDisposedException) { }
        speech.TextToSpeech.Stop();
        Changed?.Invoke();
    }
}
