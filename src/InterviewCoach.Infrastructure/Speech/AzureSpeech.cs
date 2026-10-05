using System.Globalization;
using System.Security;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using AppVoice = InterviewCoach.Core.Models.VoiceInfo;
using SdkVoice = Microsoft.CognitiveServices.Speech.VoiceInfo;

namespace InterviewCoach.Infrastructure.Speech;

/// <summary>Turns Azure's error codes and exceptions into sentences that say what to do.</summary>
public static class AzureErrors
{
    public static string Describe(CancellationErrorCode code, string? details) => code switch
    {
        CancellationErrorCode.AuthenticationFailure => "Azure rejected the speech key or region. Check both in Settings.",
        CancellationErrorCode.ConnectionFailure or CancellationErrorCode.ServiceTimeout => "Could not reach Azure Speech. Check your internet connection and the region in Settings.",
        CancellationErrorCode.Forbidden or CancellationErrorCode.TooManyRequests => "Azure Speech refused the request (access or quota). Check your Speech resource in the Azure portal.",
        _ => string.IsNullOrWhiteSpace(details) ? "Azure Speech reported an error." : "Azure Speech reported an error: " + details,
    };

    public static string Describe(Exception ex)
        => ex.Message.Contains("MIC", StringComparison.OrdinalIgnoreCase)
            ? "No microphone is available, or access to it is turned off. Check Windows Settings, Privacy, Microphone, then try again."
            : "Could not start Azure Speech: " + ex.Message;
}

/// <summary>Live dictation with Azure AI Speech: partial results as you talk, final results as each phrase ends.</summary>
public sealed class AzureSpeechToText(string key, string region, string language = "en-US") : ISpeechToText
{
    private SpeechRecognizer? _recognizer;
    private AudioConfig? _audio;

    private IReadOnlyList<string> _phrases = [];

    public bool SupportsPartials => true;

    public event EventHandler<string>? PartialRecognized;
    public event EventHandler<string>? FinalRecognized;
    public event EventHandler<string>? Error;

    public void SetPhrases(IReadOnlyList<string> phrases) => _phrases = phrases;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_recognizer is not null) return;

        // The SDK reports a missing microphone or a bad key by throwing a general exception; a speech problem must never end a
        // session, so every failure here becomes a message the screen can show.
        try
        {
            var config = SpeechConfig.FromSubscription(key, region);
            config.SpeechRecognitionLanguage = language;
            _audio = AudioConfig.FromDefaultMicrophoneInput();
            var recognizer = new SpeechRecognizer(config, _audio);
            if (_phrases.Count > 0)
            {
                // A phrase list makes the recognizer prefer these terms to look-alikes (".NET" for "dot net", "queue" for "cube").
                var grammar = PhraseListGrammar.FromRecognizer(recognizer);
                foreach (var phrase in _phrases) grammar.AddPhrase(phrase);
            }
            recognizer.Recognizing += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Result.Text)) PartialRecognized?.Invoke(this, e.Result.Text);
            };
            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                    FinalRecognized?.Invoke(this, e.Result.Text);
            };
            recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error) Error?.Invoke(this, AzureErrors.Describe(e.ErrorCode, e.ErrorDetails));
            };
            _recognizer = recognizer;
            await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Cleanup();
            Error?.Invoke(this, AzureErrors.Describe(ex));
        }
    }

    public async Task StopAsync()
    {
        var recognizer = _recognizer;
        if (recognizer is null) return;
        try
        {
            await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, AzureErrors.Describe(ex));
        }
        finally
        {
            Cleanup();
        }
    }

    public ValueTask DisposeAsync()
    {
        Cleanup();
        return ValueTask.CompletedTask;
    }

    private void Cleanup()
    {
        _recognizer?.Dispose();
        _audio?.Dispose();
        _recognizer = null;
        _audio = null;
    }
}

/// <summary>Spoken questions and answers with Azure AI Speech neural voices, played straight through the default speakers.</summary>
public sealed class AzureTextToSpeech(string key, string region, string? voice, double rate) : ITextToSpeech
{
    public const string DefaultVoice = "en-US-JennyNeural";

    private SpeechSynthesizer? _synthesizer;

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || ct.IsCancellationRequested) return;

        try
        {
            var synthesizer = _synthesizer ??= CreateSynthesizer();
            using var registration = ct.Register(() => _ = synthesizer.StopSpeakingAsync());
            var result = await synthesizer.SpeakSsmlAsync(Ssml(text)).ConfigureAwait(false);
            if (result.Reason == ResultReason.Canceled && !ct.IsCancellationRequested)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);
                throw new SpeechException(AzureErrors.Describe(details.ErrorCode == CancellationErrorCode.NoError ? CancellationErrorCode.RuntimeError : details.ErrorCode, details.ErrorDetails));
            }
        }
        catch (Exception ex) when (ex is not SpeechException and not OperationCanceledException)
        {
            throw new SpeechException(AzureErrors.Describe(ex), ex);
        }
    }

    public void Stop() => _ = _synthesizer?.StopSpeakingAsync();

    public async Task<IReadOnlyList<AppVoice>> GetVoicesAsync(CancellationToken ct)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer(SpeechConfig.FromSubscription(key, region), (AudioConfig?)null);
            var result = await synthesizer.GetVoicesAsync().ConfigureAwait(false);
            if (result.Reason == ResultReason.Canceled)
                throw new SpeechException("Could not load the Azure voices: " + result.ErrorDetails);
            return result.Voices
                .Where(v => v.Locale.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => v.Locale, StringComparer.Ordinal).ThenBy(v => v.LocalName, StringComparer.Ordinal)
                .Select(ToVoice).ToList();
        }
        catch (Exception ex) when (ex is not SpeechException and not OperationCanceledException)
        {
            throw new SpeechException(AzureErrors.Describe(ex), ex);
        }
    }

    private SpeechSynthesizer CreateSynthesizer()
        => new(SpeechConfig.FromSubscription(key, region), AudioConfig.FromDefaultSpeakerOutput());

    /// <summary>The SSML for a spoken text: the chosen voice at the chosen speed, with the text escaped so nothing in it can break the markup.</summary>
    public string Ssml(string text)
    {
        var name = string.IsNullOrWhiteSpace(voice) ? DefaultVoice : voice.Trim();
        var locale = name.Length >= 5 && name[2] == '-' ? name[..5] : "en-US";
        var speed = Math.Clamp(rate, 0.5, 2.0).ToString("0.00", CultureInfo.InvariantCulture);
        return $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{locale}\">" +
               $"<voice name=\"{SecurityElement.Escape(name)}\"><prosody rate=\"{speed}\">{SecurityElement.Escape(text)}</prosody></voice></speak>";
    }

    private static AppVoice ToVoice(SdkVoice v) => new(v.ShortName, $"{v.LocalName} ({v.Locale})", v.Locale);
}
