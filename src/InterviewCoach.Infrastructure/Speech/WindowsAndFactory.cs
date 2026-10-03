using System.Speech.Synthesis;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using InterviewCoach.Infrastructure.Fakes;
using AppVoice = InterviewCoach.Core.Models.VoiceInfo;
using SystemVoice = System.Speech.Synthesis.VoiceInfo;

namespace InterviewCoach.Infrastructure.Speech;

/// <summary>The voices built into Windows. They work offline and need no key.</summary>
public sealed class WindowsTextToSpeech(string? voice, double rate) : ITextToSpeech
{
    private SpeechSynthesizer? _synthesizer;

    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || ct.IsCancellationRequested) return;

        SpeechSynthesizer synthesizer;
        try
        {
            synthesizer = _synthesizer ??= Create();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            throw new SpeechException("The Windows voice could not be started: " + ex.Message, ex);
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender, SpeakCompletedEventArgs e)
        {
            if (e.Error is not null) done.TrySetException(new SpeechException("The Windows voice stopped: " + e.Error.Message, e.Error));
            else done.TrySetResult(); // finished, or cancelled by Stop: neither is an error
        }

        synthesizer.SpeakCompleted += Completed;
        using var registration = ct.Register(() => synthesizer.SpeakAsyncCancelAll());
        try
        {
            synthesizer.SpeakAsync(text);
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            synthesizer.SpeakCompleted -= Completed;
        }
    }

    public void Stop() => _synthesizer?.SpeakAsyncCancelAll();

    public Task<IReadOnlyList<AppVoice>> GetVoicesAsync(CancellationToken ct)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return Task.FromResult<IReadOnlyList<AppVoice>>(synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => new AppVoice(v.VoiceInfo.Name, $"{v.VoiceInfo.Name} ({v.VoiceInfo.Culture.Name})", v.VoiceInfo.Culture.Name))
                .OrderBy(v => v.Locale, StringComparer.Ordinal).ThenBy(v => v.DisplayName, StringComparer.Ordinal).ToList());
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            throw new SpeechException("Could not list the Windows voices: " + ex.Message, ex);
        }
    }

    private SpeechSynthesizer Create()
    {
        var synthesizer = new SpeechSynthesizer();
        synthesizer.SetOutputToDefaultAudioDevice();
        // Windows rates run from -10 to 10 (0 is normal); the app's rate is a multiplier from 0.5 to 2.
        synthesizer.Rate = ToWindowsRate(rate);
        if (!string.IsNullOrWhiteSpace(voice))
        {
            try { synthesizer.SelectVoice(voice.Trim()); }
            catch (ArgumentException) { /* that voice is not installed here: keep the default one */ }
        }
        return synthesizer;
    }

    /// <summary>Maps the 0.5 to 2 speed multiplier onto Windows' -10 to 10 scale (1.0 is 0).</summary>
    public static int ToWindowsRate(double multiplier)
    {
        var clamped = Math.Clamp(multiplier, 0.5, 2.0);
        return (int)Math.Round(clamped >= 1.0 ? (clamped - 1.0) * 10.0 : (clamped - 1.0) * 20.0);
    }
}

/// <summary>
/// Hands out the speech services chosen in Settings: Azure, OpenAI or Windows. It is asked each time, so a changed provider, key, voice
/// or speed applies at once. In Demo mode it hands out fakes that need no keys and make no sound.
/// </summary>
public sealed class SpeechFactory(ISettingsStore settings, IAudioRecorder? recorder = null, IAudioPlayer? player = null) : ISpeechFactory
{
    private readonly IAudioRecorder _recorder = recorder ?? new NAudioRecorder();
    private readonly IAudioPlayer _player = player ?? new NAudioPlayer();
    private readonly FakeTextToSpeech _demoVoice = new();
    private readonly object _gate = new();
    private int _demoSessions;
    private ITextToSpeech? _tts;
    private string? _ttsKey;

    private AppSettings S => settings.Current;

    public ITextToSpeech TextToSpeech
    {
        get
        {
            lock (_gate)
            {
                var s = S;
                if (s.DemoMode) return _demoVoice;

                // The same speaker is kept until a setting that affects it changes, so Stop reaches the voice that is speaking.
                var key = $"{s.TextToSpeech}|{s.EffectiveAzureSpeechKey}|{s.EffectiveAzureSpeechRegion}|{s.EffectiveOpenAiKey}|{s.OpenAiBaseUrl}|{s.Voice}|{s.SpeakingRate}";
                if (_tts is null || key != _ttsKey)
                {
                    _tts = s.TextToSpeech switch
                    {
                        TtsProvider.Azure => new AzureTextToSpeech(s.EffectiveAzureSpeechKey ?? "", s.EffectiveAzureSpeechRegion ?? "", s.Voice, s.SpeakingRate),
                        TtsProvider.OpenAi => new OpenAiTextToSpeech(s.Clone(), _player),
                        _ => new WindowsTextToSpeech(s.Voice, s.SpeakingRate),
                    };
                    _ttsKey = key;
                }
                return _tts;
            }
        }
    }

    public ISpeechToText CreateSpeechToText()
    {
        var s = S;
        if (s.DemoMode) return new FakeSpeechToText(DemoAnswers[(Interlocked.Increment(ref _demoSessions) - 1) % DemoAnswers.Length]);
        return s.SpeechToText == SttProvider.OpenAi
            ? new OpenAiSpeechToText(s.Clone(), _recorder)
            : new AzureSpeechToText(s.EffectiveAzureSpeechKey ?? "", s.EffectiveAzureSpeechRegion ?? "");
    }

    public SpeechReadiness TextToSpeechReadiness
    {
        get
        {
            var s = S;
            if (s.DemoMode) return SpeechReadiness.Ready;
            return s.TextToSpeech switch
            {
                TtsProvider.Azure when AzureMissing(s) => SpeechReadiness.NotReady("To hear questions with the Azure voice, add your Azure Speech key and region in Settings, or choose the Windows voice there."),
                TtsProvider.OpenAi when OpenAiMissing(s) => SpeechReadiness.NotReady("To hear questions with an OpenAI voice, add your OpenAI API key in Settings, or choose the Windows voice there."),
                _ => SpeechReadiness.Ready,
            };
        }
    }

    public SpeechReadiness SpeechToTextReadiness
    {
        get
        {
            var s = S;
            if (s.DemoMode) return SpeechReadiness.Ready;
            if (s.SpeechToText == SttProvider.OpenAi)
            {
                if (OpenAiMissing(s)) return SpeechReadiness.NotReady("To dictate with OpenAI, add your OpenAI API key in Settings, or choose Azure there.");
                if (!_recorder.IsAvailable) return SpeechReadiness.NotReady("No microphone was found. Plug one in or check Windows Settings, Privacy, Microphone. You can still type.");
                return SpeechReadiness.Ready;
            }
            return AzureMissing(s)
                ? SpeechReadiness.NotReady("To dictate your answer, add your Azure Speech key and region in Settings (or choose OpenAI there). You can still type.")
                : SpeechReadiness.Ready;
        }
    }

    private static bool AzureMissing(AppSettings s) => string.IsNullOrWhiteSpace(s.EffectiveAzureSpeechKey) || string.IsNullOrWhiteSpace(s.EffectiveAzureSpeechRegion);

    private static bool OpenAiMissing(AppSettings s) => string.IsNullOrWhiteSpace(s.EffectiveOpenAiKey) && string.IsNullOrWhiteSpace(s.OpenAiBaseUrl);

    // What Demo mode "hears" when the microphone is used, one answer per listening session, so the whole flow can be tried with no keys.
    private static readonly string[] DemoAnswers =
    [
        "In my last role I led the move from a monolith to services. The hardest part was keeping the data consistent while both ran side by side.",
        "I would start by measuring where the time goes, then fix the slowest query first and check the result with the same numbers.",
        "We had two options, a queue or a direct call. I chose the queue because it let us retry failures without blocking the user.",
    ];
}
