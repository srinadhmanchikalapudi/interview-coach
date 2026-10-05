using System.ClientModel;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using OpenAI;
using OpenAI.Audio;

namespace InterviewCoach.Infrastructure.Speech;

/// <summary>Builds the OpenAI audio client from the settings, or from a given address (the tests point it at a local stand-in server).</summary>
internal static class OpenAiAudio
{
    public const string TranscriptionModel = "whisper-1";
    public const string SpeechModel = "tts-1";

    public static readonly string[] Voices = ["alloy", "echo", "fable", "onyx", "nova", "shimmer"];

    public static OpenAIClient CreateClient(AppSettings settings, Uri? endpoint)
    {
        var key = settings.EffectiveOpenAiKey ?? "not-needed"; // local OpenAI-compatible servers need no key, but the client wants one
        var options = new OpenAIClientOptions();
        var address = endpoint ?? (Uri.TryCreate(settings.OpenAiBaseUrl?.Trim(), UriKind.Absolute, out var configured) ? configured : null);
        if (address is not null) options.Endpoint = address;
        return new OpenAIClient(new ApiKeyCredential(key), options);
    }

    public static string Describe(Exception ex) => ex switch
    {
        ClientResultException { Status: 401 } => "OpenAI rejected the API key. Check it in Settings.",
        ClientResultException { Status: 429 } => "OpenAI is limiting requests or your credit has run out. Try again in a moment, or check your usage.",
        ClientResultException { Status: 404 } => "That OpenAI-compatible address does not offer speech. Choose another speech provider in Settings.",
        ClientResultException e => $"OpenAI returned an error ({e.Status}): {e.Message}",
        HttpRequestException e => "Could not reach OpenAI to handle the audio: " + e.Message,
        _ => ex.Message,
    };
}

/// <summary>
/// Dictation with OpenAI: the microphone is recorded, and when it stops the recording is transcribed and delivered as one final result.
/// There are no live partial results with this provider.
/// </summary>
public sealed class OpenAiSpeechToText(AppSettings settings, IAudioRecorder recorder, Uri? endpoint = null) : ISpeechToText
{
    private bool _recording;
    private IReadOnlyList<string> _phrases = [];

    public bool SupportsPartials => false;

    public event EventHandler<string>? PartialRecognized { add { } remove { } }
    public event EventHandler<string>? FinalRecognized;
    public event EventHandler<string>? Error;

    public void SetPhrases(IReadOnlyList<string> phrases) => _phrases = phrases;

    public Task StartAsync(CancellationToken ct)
    {
        if (_recording) return Task.CompletedTask;
        try
        {
            recorder.Start();
            _recording = true;
        }
        catch (SpeechException ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>Stops recording and transcribes it; FinalRecognized fires here.</summary>
    public async Task StopAsync()
    {
        if (!_recording) return;
        _recording = false;

        byte[] wav;
        try
        {
            wav = await recorder.StopAsync().ConfigureAwait(false);
        }
        catch (SpeechException ex)
        {
            Error?.Invoke(this, ex.Message);
            return;
        }
        if (wav.Length <= 44) return; // a WAV header and no audio: nothing was said

        try
        {
            var audio = OpenAiAudio.CreateClient(settings, endpoint).GetAudioClient(OpenAiAudio.TranscriptionModel);
            var result = await audio.TranscribeAudioAsync(new MemoryStream(wav), "answer.wav", new AudioTranscriptionOptions { Language = "en", Prompt = SpeechPhrases.AsPrompt(_phrases) is { Length: > 0 } hint ? hint : null }).ConfigureAwait(false);
            var text = result.Value.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) FinalRecognized?.Invoke(this, text);
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException)
        {
            Error?.Invoke(this, OpenAiAudio.Describe(ex));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_recording)
        {
            _recording = false;
            try { await recorder.StopAsync().ConfigureAwait(false); }
            catch (SpeechException) { /* nothing left to say about a recording that is being thrown away */ }
        }
    }
}

/// <summary>Spoken text with OpenAI voices: the speech is generated as a WAV file and played through the speakers.</summary>
public sealed class OpenAiTextToSpeech(AppSettings settings, IAudioPlayer player, Uri? endpoint = null) : ITextToSpeech
{
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || ct.IsCancellationRequested) return;

        byte[] wav;
        try
        {
            var voice = string.IsNullOrWhiteSpace(settings.Voice) ? OpenAiAudio.Voices[0] : settings.Voice.Trim().ToLowerInvariant();
            var audio = OpenAiAudio.CreateClient(settings, endpoint).GetAudioClient(OpenAiAudio.SpeechModel);
            var options = new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Wav, SpeedRatio = (float)Math.Clamp(settings.SpeakingRate, 0.5, 2.0) };
            var result = await audio.GenerateSpeechAsync(text, new GeneratedSpeechVoice(voice), options, ct).ConfigureAwait(false);
            wav = result.Value.ToArray();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException)
        {
            throw new SpeechException(OpenAiAudio.Describe(ex), ex);
        }

        await player.PlayWavAsync(wav, ct).ConfigureAwait(false);
    }

    public void Stop() => player.Stop();

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<VoiceInfo>>(OpenAiAudio.Voices.Select(v => new VoiceInfo(v, char.ToUpperInvariant(v[0]) + v[1..], "en")).ToList());
}
