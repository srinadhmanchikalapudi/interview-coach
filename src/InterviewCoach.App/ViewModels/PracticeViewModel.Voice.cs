using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;

namespace InterviewCoach.App.ViewModels;

/// <summary>
/// The voice side of Practice (spec 4.6): dictating into the answer box, hearing the question, reading the model answer aloud.
/// A speech problem never ends the session: it becomes a message under the box, the text stays, and typing still works.
/// </summary>
public partial class PracticeViewModel
{
    private readonly ISpeechFactory? _speech;
    private readonly AnswerInputTracker _input = new();
    private readonly SilenceDetector _silence;

    private SynchronizationContext? _ui;
    private ISpeechToText? _stt;
    private bool _supportsPartials;
    private Task? _stopping;
    private CancellationTokenSource? _speaking;
    private bool _readingAnswer;
    private bool _dictating;
    private bool _programmatic;
    private int _caret;

    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private string _partialText = "";
    [ObservableProperty] private string _speechMessage = "";
    [ObservableProperty] private bool _isSpeaking;

    /// <summary>Starts or stops reading the model answer aloud.</summary>
    public IAsyncRelayCommand ReadAloudCommand { get; }

    /// <summary>Raised after dictated words were put in the box, so the view can move the cursor to the end of them.</summary>
    public event Action<int>? CaretRequested;

    /// <summary>True when speech is built in at all; the microphone and Repeat buttons show only then.</summary>
    public bool HasSpeech => _speech is not null;

    public bool HasPartial => PartialText.Length > 0;
    public bool HasSpeechMessage => SpeechMessage.Length > 0;
    public string MicLabel => IsListening ? "Stop" : "Speak";
    public string MicGlyph => IsListening ? "" : "";

    /// <summary>"typed", "voice" or "mixed": how the answer in the box was given. Sent to the Coach with the answer.</summary>
    public string InputMethod => _input.Method;

    public string ListeningStatus => !IsListening ? "" : _supportsPartials ? "Listening… say your answer." : "Recording… your words appear when you click Stop.";
    public bool IsReadingAnswer => _readingAnswer && IsSpeaking;

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(MicLabel));
        OnPropertyChanged(nameof(MicGlyph));
        OnPropertyChanged(nameof(ListeningStatus));
    }

    partial void OnPartialTextChanged(string value) => OnPropertyChanged(nameof(HasPartial));

    partial void OnSpeechMessageChanged(string value) => OnPropertyChanged(nameof(HasSpeechMessage));

    partial void OnIsSpeakingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReadingAnswer));
        if (Coach is { } coach) coach.IsReading = value && _readingAnswer;
    }

    /// <summary>The view reports where the cursor is, so dictated words are inserted there.</summary>
    public void SetCaret(int index) => _caret = Math.Max(0, index);

    // ---- the answer text and how it was given

    // Sets the box from code (a new question, "start from my last answer"): not typing, so it must not count as typed input.
    private void SetAnswer(string text, bool typed = false)
    {
        _programmatic = true;
        try { AnswerText = text; }
        finally { _programmatic = false; }
        _caret = text.Length;
        _input.Reset();
        if (typed && text.Trim().Length > 0) _input.NoteTyped();
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

    private async Task StartListeningAsync(bool manual)
    {
        if (_speech is null || !IsAnswering || IsListening) return;

        var ready = _speech.SpeechToTextReadiness;
        if (!ready.IsReady)
        {
            if (manual) SpeechMessage = ready.Message;
            return;
        }

        StopSpeaking(); // talking over the interviewer ends the question
        SpeechMessage = "";
        _ui ??= SynchronizationContext.Current;

        ISpeechToText stt;
        try
        {
            stt = _speech.CreateSpeechToText();
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
        // The timer starts at the first keystroke or the first time the microphone is used (spec 4.6).
        if (_startedAt is null && _frozenSeconds == 0) _startedAt = _now();
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

    /// <summary>Stops the microphone. The last words are put in the box before this returns. Safe to call when not listening.</summary>
    private Task StopListeningAsync()
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

    /// <summary>Called by <see cref="Tick"/>: submits for the user after a pause, when that setting is on (spec 4.6).</summary>
    private void CheckSilence()
    {
        if (!IsListening || !IsAnswering) return;
        var s = _settings.Current;
        if (_silence.ShouldSubmit(s.SilenceAutoSubmit, s.SilenceSeconds, WordCount)) _ = SubmitCommand.ExecuteAsync(null);
    }

    // ---- spoken questions

    /// <summary>Says the question again.</summary>
    [RelayCommand]
    private Task RepeatQuestionAsync() => SpeakQuestionAsync(automatic: false);

    private async Task SpeakQuestionAsync(bool automatic)
    {
        if (_speech is null || Item is not { } item) return;

        var ready = _speech.TextToSpeechReadiness;
        if (!ready.IsReady)
        {
            if (!automatic) SpeechMessage = ready.Message;
            return;
        }

        _ui ??= SynchronizationContext.Current;
        var completed = await SpeakAsync(SpeechText.ForSpeaking(item.Question), readingAnswer: false);

        // After the question has been heard in full, the microphone opens, unless the user already started to answer.
        if (completed && automatic && _settings.Current.AutoListen && ReferenceEquals(Item, item) && IsAnswering && !IsListening && AnswerText.Trim().Length == 0)
            await StartListeningAsync(manual: false);
    }

    private async Task ReadAloudAsync()
    {
        if (_speech is null || Coach is not { } coach) return;
        if (IsReadingAnswer)
        {
            StopSpeaking();
            return;
        }

        var ready = _speech.TextToSpeechReadiness;
        if (!ready.IsReady)
        {
            SpeechMessage = ready.Message;
            return;
        }

        _ui ??= SynchronizationContext.Current;
        await SpeakAsync(SpeechText.ForSpeaking(coach.ModelAnswer), readingAnswer: true);
    }

    /// <summary>Speaks the text; true when it was heard to the end (not stopped, not failed).</summary>
    private async Task<bool> SpeakAsync(string text, bool readingAnswer)
    {
        if (_speech is null || text.Length == 0) return false;

        StopSpeaking();
        var cts = new CancellationTokenSource();
        _speaking = cts;
        _readingAnswer = readingAnswer;
        IsSpeaking = true;
        try
        {
            await _speech.TextToSpeech.SpeakAsync(text, cts.Token);
            return !cts.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SpeechMessage = ex is SpeechException ? ex.Message + " The text is on screen." : "The voice stopped: " + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_speaking, cts))
            {
                _speaking = null;
                _readingAnswer = false;
                IsSpeaking = false;
            }
        }
    }

    private void StopSpeaking()
    {
        var current = _speaking;
        if (current is null) return;
        _speaking = null;
        _readingAnswer = false;
        IsSpeaking = false;
        try { current.Cancel(); }
        catch (ObjectDisposedException) { }
        _speech?.TextToSpeech.Stop();
    }

    /// <summary>Leaving the question or the screen: nothing keeps talking and the microphone is closed.</summary>
    private void StopVoice()
    {
        StopSpeaking();
        _ = StopListeningAsync();
    }

    // Speech SDK events arrive on their own threads; the box and the buttons must only be touched on the UI thread.
    private void OnUi(Action action)
    {
        var ui = _ui;
        if (ui is null || SynchronizationContext.Current == ui) action();
        else ui.Post(_ => action(), null);
    }
}
