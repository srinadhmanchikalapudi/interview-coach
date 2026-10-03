namespace InterviewCoach.Core.Speech;

/// <summary>
/// Decides when a spoken answer may be submitted for the user (spec 4.6): the setting is on, at least ten words have been said, and
/// nothing has been heard for the chosen number of seconds. It asks once per listening session so a submit cannot repeat. Off by default
/// because people pause to think.
/// </summary>
public sealed class SilenceDetector(Func<DateTime>? now = null)
{
    public const int MinimumWords = 10;

    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    private DateTime _lastSpeech;
    private bool _listening;
    private bool _fired;

    /// <summary>Begins a listening session; the silence is counted from now.</summary>
    public void Start()
    {
        _listening = true;
        _fired = false;
        _lastSpeech = _now();
    }

    public void Stop() => _listening = false;

    /// <summary>Called whenever speech is heard (a partial or a final result).</summary>
    public void NoteSpeech() => _lastSpeech = _now();

    /// <summary>True once, when the conditions are met. Call it every second or so while listening.</summary>
    public bool ShouldSubmit(bool enabled, int seconds, int wordCount)
    {
        if (!_listening || _fired || !enabled || wordCount < MinimumWords) return false;
        if ((_now() - _lastSpeech).TotalSeconds < Math.Max(1, seconds)) return false;
        _fired = true;
        return true;
    }
}
