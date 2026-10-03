namespace InterviewCoach.Infrastructure.Speech;

/// <summary>Records from the microphone. Used by the speech services that transcribe a finished recording (OpenAI).</summary>
public interface IAudioRecorder
{
    /// <summary>False when the computer has no microphone to record from.</summary>
    bool IsAvailable { get; }

    /// <summary>Starts recording. Throws <see cref="Core.Speech.SpeechException"/> when there is no microphone.</summary>
    void Start();

    /// <summary>Stops recording and returns what was recorded as a WAV file (16 kHz, mono, 16-bit).</summary>
    Task<byte[]> StopAsync();
}

/// <summary>Plays a WAV file through the speakers.</summary>
public interface IAudioPlayer
{
    /// <summary>Completes when playback ends or is cancelled (cancelling is not an error).</summary>
    Task PlayWavAsync(byte[] wav, CancellationToken ct);

    void Stop();
}
