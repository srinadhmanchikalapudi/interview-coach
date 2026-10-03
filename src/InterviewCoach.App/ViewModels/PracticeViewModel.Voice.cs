using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InterviewCoach.App.Services;
using InterviewCoach.Core.Abstractions;

namespace InterviewCoach.App.ViewModels;

/// <summary>
/// The voice side of Practice (spec 4.6): what the screen binds to for dictation (the work is done by <see cref="AnswerComposer"/>), and
/// hearing the question and the model answer. A speech problem never ends the session: it becomes a message under the box, the text
/// stays and typing still works.
/// </summary>
public partial class PracticeViewModel
{
    private readonly ISpeechFactory? _speech;
    private readonly Speaker? _speaker;
    private bool _readingAnswer;

    /// <summary>Starts or stops reading the model answer aloud.</summary>
    public IAsyncRelayCommand ReadAloudCommand { get; }

    /// <summary>Starts or stops the microphone (F2).</summary>
    public IAsyncRelayCommand ToggleMicCommand => Composer.ToggleMicCommand;

    /// <summary>Raised after dictated words were put in the box, so the view can move the cursor to the end of them.</summary>
    public event Action<int>? CaretRequested
    {
        add => Composer.CaretRequested += value;
        remove => Composer.CaretRequested -= value;
    }

    /// <summary>True when speech is built in at all; the microphone and Repeat buttons show only then.</summary>
    public bool HasSpeech => Composer.HasSpeech;

    public bool IsListening => Composer.IsListening;
    public string PartialText => Composer.PartialText;
    public bool HasPartial => Composer.HasPartial;
    public bool HasSpeechMessage => Composer.HasSpeechMessage;
    public string MicLabel => Composer.MicLabel;
    public string MicGlyph => Composer.MicGlyph;
    public string ListeningStatus => Composer.ListeningStatus;

    /// <summary>"typed", "voice" or "mixed": how the answer in the box was given. Sent to the Coach with the answer.</summary>
    public string InputMethod => Composer.InputMethod;

    public string SpeechMessage
    {
        get => Composer.SpeechMessage;
        set => Composer.SpeechMessage = value;
    }

    public bool IsSpeaking => _speaker?.IsSpeaking == true;
    public bool IsReadingAnswer => _readingAnswer && IsSpeaking;

    private void OnSpeakerChanged()
    {
        OnPropertyChanged(nameof(IsSpeaking));
        OnPropertyChanged(nameof(IsReadingAnswer));
        if (Coach is { } coach) coach.IsReading = _readingAnswer && IsSpeaking;
    }

    /// <summary>The view reports where the cursor is, so dictated words are inserted there.</summary>
    public void SetCaret(int index) => Composer.SetCaret(index);

    // ---- spoken questions

    /// <summary>Says the question again.</summary>
    [RelayCommand]
    private Task RepeatQuestionAsync() => SpeakQuestionAsync(automatic: false);

    private async Task SpeakQuestionAsync(bool automatic)
    {
        if (_speaker is null || Item is not { } item) return;

        var ready = _speaker.Readiness;
        if (!ready.IsReady)
        {
            if (!automatic) SpeechMessage = ready.Message;
            return;
        }

        var completed = await SpeakAsync(item.Question, readingAnswer: false);

        // After the question has been heard in full, the microphone opens, unless the user already started to answer.
        if (completed && automatic && _settings.Current.AutoListen && ReferenceEquals(Item, item) && IsAnswering && !IsListening && AnswerText.Trim().Length == 0)
            await Composer.StartListeningAsync(manual: false);
    }

    private async Task ReadAloudAsync()
    {
        if (_speaker is null || Coach is not { } coach) return;
        if (IsReadingAnswer)
        {
            StopSpeaking();
            return;
        }

        var ready = _speaker.Readiness;
        if (!ready.IsReady)
        {
            SpeechMessage = ready.Message;
            return;
        }

        await SpeakAsync(coach.ModelAnswer, readingAnswer: true);
    }

    /// <summary>Speaks the text; true when it was heard to the end (not stopped, not failed).</summary>
    private async Task<bool> SpeakAsync(string text, bool readingAnswer)
    {
        if (_speaker is null) return false;

        _readingAnswer = readingAnswer;
        var outcome = await _speaker.SpeakAsync(text);
        if (outcome.Error is { } error) SpeechMessage = error + " The text is on screen.";
        return outcome.Completed;
    }

    private void StopSpeaking()
    {
        _readingAnswer = false;
        _speaker?.Stop();
    }

    /// <summary>Leaving the question or the screen: nothing keeps talking and the microphone is closed.</summary>
    private void StopVoice()
    {
        StopSpeaking();
        Composer.CloseMicrophone();
    }
}
