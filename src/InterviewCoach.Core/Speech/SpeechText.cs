using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Speech;

/// <summary>Tidies text before it is read aloud, so a voice does not read formatting characters or stumble on them.</summary>
public static partial class SpeechText
{
    [GeneratedRegex(@"[*_`#>~|]+")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string ForSpeaking(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Replace("[", "").Replace("]", "")          // a placeholder such as [your actual p99] is read as the words inside
                    .Replace("→", ", then ")               // an arrow
                    .Replace("—", ", ").Replace("–", ", ")
                    .Replace("·", ",");
        s = Markup().Replace(s, "");
        return Whitespace().Replace(s, " ").Trim();
    }
}
