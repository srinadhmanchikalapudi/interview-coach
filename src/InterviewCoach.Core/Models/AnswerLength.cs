using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Models;

/// <summary>Word counts and spoken-time maths for answers, and what interviewers typically expect per question type.</summary>
public static partial class AnswerLength
{
    /// <summary>Comfortable speaking pace for an interview answer.</summary>
    public const int WordsPerMinute = 130;

    public const int MinCustomWords = 30;
    public const int MaxCustomWords = 600;

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();

    public static int CountWords(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Word().Matches(text).Count;

    public static int SpokenSeconds(int words) => (int)Math.Round(words * 60.0 / WordsPerMinute);

    /// <summary>"45 sec", "1 min 10 sec"... short and speakable.</summary>
    public static string FormatSeconds(int seconds)
    {
        seconds = (int)(Math.Round(seconds / 5.0) * 5); // nobody speaks to the exact second
        if (seconds < 60) return $"{Math.Max(seconds, 5)} sec";
        var minutes = seconds / 60;
        var rest = seconds % 60;
        return rest == 0 ? $"{minutes} min" : $"{minutes} min {rest} sec";
    }

    public static string Spoken(int words) => FormatSeconds(SpokenSeconds(words));

    /// <summary>The {{ANSWER_LENGTH}} value for coach.md: a request for a word count, or null for "use your own targets".</summary>
    public static string? ForPrompt(int? words) =>
        words is { } w ? $"about {w} words (roughly {Spoken(w)} spoken)" : null;

    /// <summary>
    /// The {{ANSWER_LENGTH}} value when no word count was asked for: the range interviewers typically expect for this type of
    /// question, which is the same range the screen shows next to the answer. Null when the type is unknown.
    /// </summary>
    public static string? RangeForPrompt(string? questionTypeId) =>
        Expectation(questionTypeId) is { } r
            ? $"between {r.Min} and {r.Max} words (roughly {Spoken(r.Min)} to {Spoken(r.Max)} spoken)"
            : null;

    /// <summary>The {{ANSWER_LENGTH}} value to send: the requested word count if there is one, else the range for the question type.</summary>
    public static string? Describe(int? words, string? questionTypeId) => ForPrompt(words) ?? RangeForPrompt(questionTypeId);

    public static int ClampCustom(int words) => Math.Clamp(words, MinCustomWords, MaxCustomWords);

    /// <summary>Typical interviewer expectation in words for an answer to this type of question, from coach.md's targets.</summary>
    public static (int Min, int Max)? Expectation(string? questionTypeId) => questionTypeId?.Trim().ToLowerInvariant() switch
    {
        "tell_me_about_yourself" => (130, 200),
        "resume_deep_dive" => (150, 280),
        "behavioral" => (150, 280),
        "technical_concept" => (60, 150),
        "scenario" => (120, 220),
        "motivation_fit" => (90, 160),
        "engagement" => (40, 100),
        "system_design" or "coding_talkthrough" => (130, 260), // the first one to two minutes, then hand back to the interviewer
        _ => null,
    };

    public static string DescribeExpectation((int Min, int Max) range)
        => $"{range.Min}–{range.Max} words, about {Spoken(range.Min)} to {Spoken(range.Max)} spoken";
}
