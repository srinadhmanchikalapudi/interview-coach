namespace InterviewCoach.Core.Speech;

/// <summary>
/// Remembers how an answer was given: typed, spoken, or both (spec 4.6). Speech put into the box counts as voice; the user changing
/// the text by hand (typing, or fixing a dictated word) counts as typed, and an answer with both is mixed.
/// </summary>
public sealed class AnswerInputTracker
{
    private bool _voice;
    private bool _typed;

    public void NoteVoice() => _voice = true;

    public void NoteTyped() => _typed = true;

    public void Reset()
    {
        _voice = false;
        _typed = false;
    }

    /// <summary>"typed", "voice" or "mixed". An answer that was never touched counts as typed.</summary>
    public string Method => _voice && _typed ? "mixed" : _voice ? "voice" : "typed";

    public bool UsedVoice => _voice;
}
