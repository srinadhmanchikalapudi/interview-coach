using System.Net;
using System.Net.Sockets;
using System.Text;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Speech;
using Microsoft.CognitiveServices.Speech;

namespace InterviewCoach.Infrastructure.Tests;

public class SpeechTests
{
    // ---- a local stand-in for OpenAI's audio endpoints, so the real requests can be checked without a key

    private sealed class FakeOpenAiAudio : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Uri Endpoint { get; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? ContentType { get; private set; }
        public byte[] Body { get; private set; } = [];
        public int Status { get; set; } = 200;
        public string TranscriptionJson { get; set; } = "{\"text\":\"hello from the stand-in\"}";
        public byte[] SpeechBytes { get; set; } = Encoding.ASCII.GetBytes("RIFF....WAVEfmt fake audio");

        public string BodyText => Encoding.UTF8.GetString(Body);

        public FakeOpenAiAudio()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Endpoint = new Uri($"http://127.0.0.1:{port}/v1");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
        }

        /// <summary>Serves one request while <paramref name="action"/> runs.</summary>
        public async Task ServeAsync(Func<Task> action)
        {
            var served = Task.Run(async () =>
            {
                var context = await _listener.GetContextAsync();
                Path = context.Request.Url!.AbsolutePath;
                Authorization = context.Request.Headers["Authorization"];
                ContentType = context.Request.ContentType;
                using (var memory = new MemoryStream())
                {
                    await context.Request.InputStream.CopyToAsync(memory);
                    Body = memory.ToArray();
                }
                context.Response.StatusCode = Status;
                if (Status >= 400)
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"nope\"}}"));
                }
                else if (Path!.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(TranscriptionJson));
                }
                else
                {
                    context.Response.ContentType = "audio/wav";
                    await context.Response.OutputStream.WriteAsync(SpeechBytes);
                }
                context.Response.Close();
            });
            await action();
            await served;
        }

        public void Dispose() => _listener.Close();
    }

    private sealed class FakeRecorder(byte[]? audio = null, bool available = true, bool failToStart = false) : IAudioRecorder
    {
        public bool IsAvailable { get; } = available;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public void Start()
        {
            Starts++;
            if (failToStart) throw new SpeechException("No microphone was found.");
        }

        public Task<byte[]> StopAsync()
        {
            Stops++;
            return Task.FromResult(audio ?? new byte[2000]);
        }
    }

    private sealed class FakePlayer : IAudioPlayer
    {
        public List<byte[]> Played { get; } = [];
        public int StopCount { get; private set; }
        public Task PlayWavAsync(byte[] wav, CancellationToken ct) { Played.Add(wav); return Task.CompletedTask; }
        public void Stop() => StopCount++;
    }

    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private static AppSettings OpenAi(string key = "sk-test") => new() { OpenAiApiKey = key, SpeechToText = SttProvider.OpenAi, TextToSpeech = TtsProvider.OpenAi };

    // ---- OpenAI: record, then transcribe

    [Fact]
    public async Task OpenAI_dictation_records_then_sends_the_recording_and_delivers_the_text_when_stopped()
    {
        using var server = new FakeOpenAiAudio();
        var recorder = new FakeRecorder();
        await using var stt = new OpenAiSpeechToText(OpenAi(), recorder, server.Endpoint);
        var heard = new List<string>();
        var partials = 0;
        stt.FinalRecognized += (_, text) => heard.Add(text);
        stt.PartialRecognized += (_, _) => partials++;

        await stt.StartAsync(CancellationToken.None);
        Assert.Equal(1, recorder.Starts);
        Assert.Empty(heard);                                   // nothing is delivered while recording
        await server.ServeAsync(() => stt.StopAsync());

        Assert.False(stt.SupportsPartials);
        Assert.Equal(0, partials);
        Assert.Equal(["hello from the stand-in"], heard);
        Assert.Equal("/v1/audio/transcriptions", server.Path);
        Assert.Equal("Bearer sk-test", server.Authorization);
        Assert.StartsWith("multipart/form-data", server.ContentType);
        Assert.Contains("whisper-1", server.BodyText);
        Assert.Contains("answer.wav", server.BodyText);
        Assert.Equal(1, recorder.Stops);
    }

    [Fact]
    public async Task OpenAI_dictation_reports_a_rejected_key_in_words_and_delivers_nothing()
    {
        using var server = new FakeOpenAiAudio { Status = 401 };
        await using var stt = new OpenAiSpeechToText(OpenAi("sk-wrong"), new FakeRecorder(), server.Endpoint);
        var errors = new List<string>();
        var heard = new List<string>();
        stt.Error += (_, e) => errors.Add(e);
        stt.FinalRecognized += (_, t) => heard.Add(t);
        await stt.StartAsync(CancellationToken.None);

        await server.ServeAsync(() => stt.StopAsync());

        Assert.Equal(["OpenAI rejected the API key. Check it in Settings."], errors);
        Assert.Empty(heard);
    }

    [Fact]
    public async Task OpenAI_dictation_sends_nothing_when_nothing_was_recorded()
    {
        var recorder = new FakeRecorder(audio: new byte[44]);   // a WAV header and no sound
        await using var stt = new OpenAiSpeechToText(OpenAi(), recorder, new Uri("http://127.0.0.1:9/v1")); // an address nothing listens on
        var errors = new List<string>();
        stt.Error += (_, e) => errors.Add(e);
        await stt.StartAsync(CancellationToken.None);

        await stt.StopAsync();

        Assert.Empty(errors);   // no request was made, so there was nothing to fail
    }

    [Fact]
    public async Task A_missing_microphone_is_reported_as_an_error_not_thrown()
    {
        await using var stt = new OpenAiSpeechToText(OpenAi(), new FakeRecorder(failToStart: true));
        var errors = new List<string>();
        stt.Error += (_, e) => errors.Add(e);

        await stt.StartAsync(CancellationToken.None);

        Assert.Equal(["No microphone was found."], errors);
        await stt.StopAsync();   // stopping something that never started is harmless
    }

    // ---- OpenAI: spoken text

    [Fact]
    public async Task An_OpenAI_voice_asks_for_wav_speech_in_the_chosen_voice_and_speed_and_plays_what_comes_back()
    {
        using var server = new FakeOpenAiAudio();
        var player = new FakePlayer();
        var settings = OpenAi();
        settings.Voice = "Nova";
        settings.SpeakingRate = 1.25;
        var tts = new OpenAiTextToSpeech(settings, player, server.Endpoint);

        await server.ServeAsync(() => tts.SpeakAsync("What is a struct?", CancellationToken.None));

        Assert.Equal("/v1/audio/speech", server.Path);
        Assert.Equal("Bearer sk-test", server.Authorization);
        var body = server.BodyText;
        Assert.Contains("\"model\":\"tts-1\"", body);
        Assert.Contains("\"input\":\"What is a struct?\"", body);
        Assert.Contains("\"voice\":\"nova\"", body);
        Assert.Contains("\"response_format\":\"wav\"", body);
        Assert.Contains("\"speed\":1.25", body);
        Assert.Equal(server.SpeechBytes, Assert.Single(player.Played));
    }

    [Fact]
    public async Task An_OpenAI_voice_defaults_to_alloy_and_clamps_the_speed()
    {
        using var server = new FakeOpenAiAudio();
        var settings = OpenAi();
        settings.SpeakingRate = 9;

        await server.ServeAsync(() => new OpenAiTextToSpeech(settings, new FakePlayer(), server.Endpoint).SpeakAsync("Hi", CancellationToken.None));

        Assert.Contains("\"voice\":\"alloy\"", server.BodyText);
        Assert.Contains("\"speed\":2", server.BodyText);
    }

    [Fact]
    public async Task An_OpenAI_voice_that_fails_says_why_and_plays_nothing()
    {
        using var server = new FakeOpenAiAudio { Status = 401 };
        var player = new FakePlayer();
        var tts = new OpenAiTextToSpeech(OpenAi("bad"), player, server.Endpoint);

        var ex = await Assert.ThrowsAsync<SpeechException>(async () =>
        {
            Task<Exception?> run = Task.FromResult<Exception?>(null);
            await server.ServeAsync(async () => await tts.SpeakAsync("Hello", CancellationToken.None));
            _ = run;
        });

        Assert.Contains("rejected the API key", ex.Message);
        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task Cancelling_before_speaking_plays_nothing_and_is_not_an_error()
    {
        var player = new FakePlayer();
        var tts = new OpenAiTextToSpeech(OpenAi(), player, new Uri("http://127.0.0.1:9/v1"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await tts.SpeakAsync("Never spoken", cts.Token);

        Assert.Empty(player.Played);
    }

    [Fact]
    public async Task Stopping_an_OpenAI_voice_stops_the_player_and_blank_text_is_not_sent()
    {
        var player = new FakePlayer();
        var tts = new OpenAiTextToSpeech(OpenAi(), player, new Uri("http://127.0.0.1:9/v1"));

        await tts.SpeakAsync("   ", CancellationToken.None);   // nothing to say: no request, no error
        tts.Stop();

        Assert.Equal(1, player.StopCount);
        Assert.Empty(player.Played);
        var voices = await tts.GetVoicesAsync(CancellationToken.None);
        Assert.Contains(voices, v => v.Id == "alloy");
        Assert.Contains(voices, v => v.Id == "nova" && v.DisplayName == "Nova");
    }

    // ---- Azure: what can be checked without Azure

    [Fact]
    public void Azure_speech_markup_names_the_voice_and_speed_and_escapes_the_text()
    {
        var tts = new AzureTextToSpeech("key", "westeurope", "en-GB-RyanNeural", 1.2);

        var ssml = tts.Ssml("Use <b> & \"quotes\" 'here'");

        Assert.Contains("xml:lang=\"en-GB\"", ssml);
        Assert.Contains("<voice name=\"en-GB-RyanNeural\">", ssml);
        Assert.Contains("<prosody rate=\"1.20\">", ssml);
        Assert.Contains("Use &lt;b&gt; &amp; &quot;quotes&quot; &apos;here&apos;", ssml);
        Assert.DoesNotContain("<b>", ssml);
    }

    [Theory]
    [InlineData(null, 1.0, "en-US-JennyNeural", "1.00")]
    [InlineData("", 1.0, "en-US-JennyNeural", "1.00")]
    [InlineData("  en-US-AriaNeural ", 0.1, "en-US-AriaNeural", "0.50")]
    [InlineData("en-US-AriaNeural", 5, "en-US-AriaNeural", "2.00")]
    public void Azure_speech_markup_falls_back_to_a_default_voice_and_keeps_the_speed_in_range(string? voice, double rate, string expectedVoice, string expectedRate)
    {
        var ssml = new AzureTextToSpeech("k", "r", voice, rate).Ssml("Hi");

        Assert.Contains($"<voice name=\"{expectedVoice}\">", ssml);
        Assert.Contains($"rate=\"{expectedRate}\"", ssml);
    }

    [Fact]
    public void Azure_errors_are_turned_into_sentences_that_say_what_to_do()
    {
        Assert.Contains("rejected the speech key or region", AzureErrors.Describe(CancellationErrorCode.AuthenticationFailure, null));
        Assert.Contains("Could not reach Azure Speech", AzureErrors.Describe(CancellationErrorCode.ConnectionFailure, null));
        Assert.Contains("quota", AzureErrors.Describe(CancellationErrorCode.TooManyRequests, null));
        Assert.Contains("something odd", AzureErrors.Describe(CancellationErrorCode.RuntimeError, "something odd"));
        Assert.Contains("Microphone", AzureErrors.Describe(new ApplicationException("Exception with an error code: 0x8 (SPXERR_MIC_NOT_AVAILABLE)")));
        Assert.StartsWith("Could not start Azure Speech", AzureErrors.Describe(new InvalidOperationException("other")));
    }

    [Fact]
    public async Task Azure_dictation_with_nothing_to_start_reports_through_the_error_event_instead_of_throwing()
    {
        // An empty key and region cannot start a recognizer. Whatever the SDK does about that, the screen must hear about it as a message.
        await using var stt = new AzureSpeechToText("", "");
        var errors = new List<string>();
        stt.Error += (_, e) => errors.Add(e);

        await stt.StartAsync(CancellationToken.None);
        await stt.StopAsync();

        Assert.True(stt.SupportsPartials);
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.False(string.IsNullOrWhiteSpace(e)));
    }

    // ---- Windows voice

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(2.0, 10)]
    [InlineData(1.5, 5)]
    [InlineData(0.5, -10)]
    [InlineData(0.75, -5)]
    [InlineData(9.0, 10)]
    [InlineData(0.0, -10)]
    public void The_speed_multiplier_maps_onto_the_Windows_scale(double multiplier, int expected)
    {
        Assert.Equal(expected, WindowsTextToSpeech.ToWindowsRate(multiplier));
    }

    // ---- the factory

    private static SpeechFactory Factory(AppSettings s, FakeRecorder? recorder = null) => new(new MemorySettings(s), recorder ?? new FakeRecorder(), new FakePlayer());

    [Fact]
    public void Demo_mode_gets_fakes_that_are_always_ready_and_hear_a_sample_answer()
    {
        var factory = Factory(new AppSettings { DemoMode = true });

        Assert.True(factory.TextToSpeechReadiness.IsReady);
        Assert.True(factory.SpeechToTextReadiness.IsReady);
        Assert.IsType<FakeTextToSpeech>(factory.TextToSpeech);
        Assert.IsType<FakeSpeechToText>(factory.CreateSpeechToText());
    }

    [Fact]
    public async Task Each_demo_listening_session_hears_the_next_sample_answer()
    {
        var factory = Factory(new AppSettings { DemoMode = true });
        var heard = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var stt = factory.CreateSpeechToText();
            stt.FinalRecognized += (_, t) => heard.Add(t);
            await stt.StartAsync(CancellationToken.None);
            await stt.StopAsync();
        }

        Assert.Equal(4, heard.Count);
        Assert.NotEqual(heard[0], heard[1]);
        Assert.Equal(heard[0], heard[3]);   // three samples, then round again
    }

    [Fact]
    public void The_Windows_voice_needs_no_key_and_is_ready_straight_away()
    {
        var factory = Factory(new AppSettings { TextToSpeech = TtsProvider.Windows });

        Assert.True(factory.TextToSpeechReadiness.IsReady);
        Assert.IsType<WindowsTextToSpeech>(factory.TextToSpeech);
    }

    [Fact]
    public void An_Azure_voice_or_microphone_without_a_key_and_region_says_what_to_add()
    {
        var none = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        var region = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_SPEECH_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_SPEECH_REGION", null);
            var settings = new AppSettings { TextToSpeech = TtsProvider.Azure, SpeechToText = SttProvider.Azure };

            var factory = Factory(settings);

            Assert.False(factory.TextToSpeechReadiness.IsReady);
            Assert.Contains("Azure Speech key and region", factory.TextToSpeechReadiness.Message);
            Assert.Contains("Windows voice", factory.TextToSpeechReadiness.Message);
            Assert.False(factory.SpeechToTextReadiness.IsReady);
            Assert.Contains("Azure Speech key and region", factory.SpeechToTextReadiness.Message);
            Assert.Contains("type", factory.SpeechToTextReadiness.Message);

            settings.AzureSpeechKey = "k";
            Assert.False(factory.SpeechToTextReadiness.IsReady);       // the region is still missing
            settings.AzureSpeechRegion = "westeurope";
            Assert.True(factory.SpeechToTextReadiness.IsReady);
            Assert.True(factory.TextToSpeechReadiness.IsReady);
            Assert.IsType<AzureSpeechToText>(factory.CreateSpeechToText());
            Assert.IsType<AzureTextToSpeech>(factory.TextToSpeech);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_SPEECH_KEY", none);
            Environment.SetEnvironmentVariable("AZURE_SPEECH_REGION", region);
        }
    }

    [Fact]
    public void OpenAI_speech_needs_a_key_or_a_local_server_and_dictation_needs_a_microphone()
    {
        var oai = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var settings = new AppSettings { TextToSpeech = TtsProvider.OpenAi, SpeechToText = SttProvider.OpenAi };
            var factory = Factory(settings);

            Assert.False(factory.TextToSpeechReadiness.IsReady);
            Assert.Contains("OpenAI API key", factory.TextToSpeechReadiness.Message);
            Assert.False(factory.SpeechToTextReadiness.IsReady);

            settings.OpenAiBaseUrl = "http://localhost:8000/v1";       // a local server needs no key
            Assert.True(factory.TextToSpeechReadiness.IsReady);
            Assert.True(factory.SpeechToTextReadiness.IsReady);
            Assert.IsType<OpenAiSpeechToText>(factory.CreateSpeechToText());
            Assert.IsType<OpenAiTextToSpeech>(factory.TextToSpeech);

            var noMic = Factory(new AppSettings { OpenAiApiKey = "k", SpeechToText = SttProvider.OpenAi }, new FakeRecorder(available: false));
            Assert.False(noMic.SpeechToTextReadiness.IsReady);
            Assert.Contains("No microphone", noMic.SpeechToTextReadiness.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", oai);
        }
    }

    [Fact]
    public void The_same_speaker_is_kept_until_a_setting_that_affects_it_changes()
    {
        var settings = new AppSettings { TextToSpeech = TtsProvider.Windows, Voice = "A", SpeakingRate = 1.0 };
        var factory = Factory(settings);

        var first = factory.TextToSpeech;
        Assert.Same(first, factory.TextToSpeech);       // Stop reaches the voice that is speaking

        settings.SpeakingRate = 1.5;
        var faster = factory.TextToSpeech;
        Assert.NotSame(first, faster);
        Assert.Same(faster, factory.TextToSpeech);

        settings.Voice = "B";
        Assert.NotSame(faster, factory.TextToSpeech);

        settings.TextToSpeech = TtsProvider.OpenAi;
        Assert.IsType<OpenAiTextToSpeech>(factory.TextToSpeech);
    }

    [Fact]
    public void Switching_Demo_mode_on_and_off_switches_between_fakes_and_the_real_voice()
    {
        var settings = new AppSettings { TextToSpeech = TtsProvider.Windows };
        var factory = Factory(settings);
        Assert.IsType<WindowsTextToSpeech>(factory.TextToSpeech);

        settings.DemoMode = true;
        Assert.IsType<FakeTextToSpeech>(factory.TextToSpeech);

        settings.DemoMode = false;
        Assert.IsType<WindowsTextToSpeech>(factory.TextToSpeech);
    }
}
