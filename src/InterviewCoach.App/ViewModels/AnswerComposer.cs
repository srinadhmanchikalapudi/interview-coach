using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;

namespace InterviewCoach.App.ViewModels;

/// <summary>
/// The answer box with a microphone (spec 4.6), shared by Practice and Mock Interview: a text box the user can type in and dictate into,
/// a live word count, the words being heard, how the answer was given (typed, voice or mixed), and auto-submit after a pause. A speech
/// problem never ends a session: it becomes <see cref="SpeechMessage"/>, the text stays and typing still works.
/// <para>
/// Property names are the same ones the screens bind to, so a screen can relay this class's change notifications under the same names.
/// </para>
/// </summary>
public sealed partial class AnswerComposer : ObservableObject
{
    private readonly ISpeechFactory? _speech;
    private readonly ISettingsStore _settings;
    private readonly Func<bool> _isOpen;
    private readonly AnswerInputTracker _input = new();
    private readonly SilenceDetector _silence;

    private SynchronizationContext? _ui;
    private ISpeechToText? _stt;
    private bool _supportsPartials;
    private Task? _stopping;
    private bool _dictating;
    private bool _programmatic;
    private int _caret;

    /// <param name="isOpen">True while an answer may be given (the owner is waiting for one). The microphone opens only then.</param>
    public AnswerComposer(ISpeechFactory? speech, ISettingsStore settings, Func<DateTime> now, Func<bool> isOpen)
    {
        _speech = speech;
        _settings = settings;
        _isOpen = isOpen;
        _silence = new SilenceDetector(now);
    }

    [ObservableProperty] private string _answerText = "";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _partialText = "";
    [ObservableProperty] private string _speechMessage = "";

    /// <summary>Raised whenever the text changes, however it changed (typing, dictation or code).</summary>
    public event Action<string>? TextChanged;

    /// <summary>Raised when the microphone starts, so the owner can start its answer timer (spec 4.6: the first keystroke or the first mic use).</summary>
    public event Action? MicStarted;

    /// <summary>Raised when a pause in speech meets the auto-submit settings; the owner submits the answer.</summary>
    public event Action? SubmitRequested;

    /// <summary>Raised after dictated words were put in the box, so the view can move the cursor to the end of them.</summary>
    public event Action<int>? CaretRequested;

    /// <summary>
    /// Words and names likely to be spoken in this answer (technologies, employers, the role), given to the recognizer when the microphone starts so it
    /// prefers them to look-alikes. The owner sets and updates them; they only help, so nothing depends on them.
    /// </summary>
    public IReadOnlyList<string> Phrases { get; set; } = [];

    /// <summary>True when speech is built in at all; the microphone button shows only then.</summary>
    public bool HasSpeech => _speech is not null;

    public int WordCount => AnswerLength.CountWords(AnswerText);
    public string WordCountText => WordCount == 1 ? "1 word" : $"{WordCount} words";
    public bool HasPartial => PartialText.Length > 0;
    public bool HasSpeechMessage => SpeechMessage.Length > 0;
    public string MicLabel => IsListening ? "Stop" : "Speak";
    public string MicGlyph => IsListening ? "" : "";
    public string ListeningStatus => !IsListening ? "" : _supportsPartials ? "Listening… say your answer." : "Recording… your words appear when you click Stop.";

    /// <summary>"typed", "voice" or "mixed": how the answer in the box was given. Sent along with the answer.</summary>
    public string InputMethod => _input.Method;

    partial void OnAnswerTextChanged(string value)
    {
        OnPropertyChanged(nameof(WordCount));
        OnPropertyChanged(nameof(WordCountText));
        NoteTextChanged(value);
        TextChanged?.Invoke(value);
    }

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(MicLabel));
        OnPropertyChanged(nameof(MicGlyph));
        OnPropertyChanged(nameof(ListeningStatus));
    }

    partial void OnPartialTextChanged(string value) => OnPropertyChanged(nameof(HasPartial));

    partial void OnSpeechMessageChanged(string value) => OnPropertyChanged(nameof(HasSpeechMessage));

    /// <summary>The view reports where the cursor is, so dictated words are inserted there.</summary>
    public void SetCaret(int index) => _caret = Math.Max(0, index);

    // ---- the text and how it was given

    /// <summary>Sets the box from code (a new question, "start from my last answer"): not typing, so it does not count as typed input.</summary>
    public void SetText(string text, bool typed = false)
    {
        _programmatic = true;
        try { AnswerText = text; }
        finally { _programmatic = false; }
        _caret = text.Length;
        _input.Reset();
        if (typed && text.Trim().Length > 0) _input.NoteTyped();
        OnPropertyChanged(nameof(InputMethod));
    }

    private void NoteTextChanged(string value)
    {
        if (_dictating || _programmatic) return;
        if (value.Trim().Length == 0) _input.Reset(); // starting over: the next words decide how this answer was given
        else _input.NoteTyped();
        _caret = Math.Min(_caret, value.Length);
        OnPropertyChanged(nameof(InputMethod));
    }

    // ---- dictation

    [RelayCommand]
    private async Task ToggleMicAsync()
    {
        if (IsListening) await StopListeningAsync();
        else await StartListeningAsync(manual: true);
    }

    /// <summary>Opens the microphone. <paramref name="manual"/> is false when the app opens it on its own (auto-listen): a microphone that is not set up is then no error.</summary>
    public async Task StartListeningAsync(bool manual)
    {
        if (_speech is null || !_isOpen() || IsListening) return;

        var ready = _speech.SpeechToTextReadiness;
        if (!ready.IsReady)
        {
            if (manual) SpeechMessage = ready.Message;
            return;
        }

        SpeechMessage = "";
        _ui ??= SynchronizationContext.Current;

        ISpeechToText stt;
        try
        {
            stt = _speech.CreateSpeechToText();
            if (Phrases.Count > 0) stt.SetPhrases(Phrases);
        }
        catch (Exception ex)
        {
            SpeechMessage = ex.Message;
            return;
        }

        stt.PartialRecognized += (_, text) => OnUi(() => { if (_stt == stt) { PartialText = text; _silence.NoteSpeech(); } });
        stt.FinalRecognized += (_, text) => OnUi(() => { if (_stt == stt) Dictated(text); });
        stt.Error += (_, message) => OnUi(() => { if (_stt == stt) _ = FailListeningAsync(stt, message); });

        _stt = stt;
        _stopping = null;
        _supportsPartials = stt.SupportsPartials;
        PartialText = "";
        IsListening = true;
        MicStarted?.Invoke();
        _silence.Start();
        try
        {
            await stt.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            await FailListeningAsync(stt, ex.Message);
        }
    }

    // The words heard so far go into the box at the cursor, so the user can dictate, fix a word and carry on in the same box.
    private void Dictated(string text)
    {
        var inserted = DictationText.Insert(AnswerText, _caret, text);
        _dictating = true;
        try { AnswerText = inserted.Text; }
        finally { _dictating = false; }
        _caret = inserted.Caret;
        _input.NoteVoice();
        PartialText = "";
        _silence.NoteSpeech();
        OnPropertyChanged(nameof(InputMethod));
        CaretRequested?.Invoke(inserted.Caret);
    }

    /// <summary>Stops the microphone. The last words are put in the box before this completes. Safe to call when not listening.</summary>
    public Task StopListeningAsync()
    {
        var stt = _stt;
        if (stt is null) return Task.CompletedTask;
        return _stopping ??= StopAsync(stt);
    }

    private async Task StopAsync(ISpeechToText stt)
    {
        IsListening = false;
        _silence.Stop();
        try
        {
            await stt.StopAsync(); // a record-then-transcribe provider delivers its words here
        }
        catch (Exception ex)
        {
            SpeechMessage = ex.Message;
        }
        PartialText = "";
        if (_stt == stt) _stt = null;
        _stopping = null;
        try { await stt.DisposeAsync(); }
        catch (Exception) { /* nothing useful can be said about a recognizer that is being thrown away */ }
    }

    // Something went wrong while listening: say what, keep the text, and let the user carry on typing.
    private async Task FailListeningAsync(ISpeechToText stt, string message)
    {
        SpeechMessage = message;
        if (_stt != stt) return;
        _stt = null;
        _stopping = null;
        IsListening = false;
        PartialText = "";
        _silence.Stop();
        try { await stt.StopAsync(); }
        catch (Exception) { /* already failed */ }
        try { await stt.DisposeAsync(); }
        catch (Exception) { /* already failed */ }
    }

    /// <summary>Called about once a second: asks the owner to submit after a pause, when that setting is on (spec 4.6).</summary>
    public void CheckSilence()
    {
        if (!IsListening || !_isOpen()) return;
        var s = _settings.Current;
        if (_silence.ShouldSubmit(s.SilenceAutoSubmit, s.SilenceSeconds, WordCount)) SubmitRequested?.Invoke();
    }

    /// <summary>The owner moved on (a new question, the screen was left): close the microphone.</summary>
    public void CloseMicrophone() => _ = StopListeningAsync();

    // Speech SDK events arrive on their own threads; the box and the buttons must only be touched on the UI thread.
    private void OnUi(Action action)
    {
        var ui = _ui;
        if (ui is null || SynchronizationContext.Current == ui) action();
        else ui.Post(_ => action(), null);
    }
}
