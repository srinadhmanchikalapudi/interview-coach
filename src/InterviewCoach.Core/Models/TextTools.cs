namespace InterviewCoach.Core.Models;

public static partial class TextTools
{
    /// <summary>
    /// A stable fingerprint of a text for "have I seen exactly this before?" checks. Differences in whitespace
    /// (line endings, extra blank lines, trailing spaces) do not change it; any change to the words does.
    /// </summary>
    public static string Fingerprint(string text)
    {
        var normalized = Whitespace().Replace(text, " ").Trim();
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    /// <summary>Lower-cases and strips punctuation, so "Why Redis?" and "why redis" count as the same question.</summary>
    public static string NormalizeQuestion(string text)
        => NonWord().Replace(text.ToLowerInvariant(), " ").Trim();

    public static bool SameQuestion(string a, string b) => NormalizeQuestion(a) == NormalizeQuestion(b);

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Whitespace();

    [System.Text.RegularExpressions.GeneratedRegex(@"[^a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex NonWord();

    /// <summary>
    /// Cuts text down to at most <paramref name="max"/> characters, preferring to stop at a paragraph break,
    /// then a line break, so the result does not end mid-sentence when it can be avoided.
    /// </summary>
    public static string TrimTo(string text, int max)
    {
        if (text.Length <= max) return text;

        var window = text[..max];
        var floor = max * 3 / 4; // do not throw away more than a quarter just to find a tidy boundary
        foreach (var boundary in new[] { "\n\n", "\n" })
        {
            var at = window.LastIndexOf(boundary, StringComparison.Ordinal);
            if (at >= floor) return window[..at].TrimEnd();
        }
        return window.TrimEnd();
    }
}
