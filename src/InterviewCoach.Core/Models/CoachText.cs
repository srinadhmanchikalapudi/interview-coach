using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Models;

/// <summary>Small parsers for rendering Coach output.</summary>
public static partial class CoachText
{
    public record Piece(string Text, bool IsPlaceholder);

    [GeneratedRegex(@"\[[^\[\]\r\n]+\]")]
    private static partial Regex Placeholder();

    // A filler word is only removed when punctuation follows it ("Sure." "Yeah," "Okay,"), so real words that start a
    // sentence ("Right now", "Well-known", "So far") are left alone. "Yes" and "No" are answers, not filler, and stay.
    [GeneratedRegex(
        @"^\s*(?:" +
        @"(?:yeah|okay|ok|alright|right|well|sure),?\s+so\b,?\s*" +                                                  // "Yeah, so"  "Okay so" (before the single words, so the "so" goes too)
        @"|(?:sure|yeah|okay|ok|alright|right|well|so|honestly|great question|good question)\s*[,.!:;\u2014\u2013]+\s*" +   // "Sure."  "Yeah,"
        @"|(?:the\s+)?short\s+(?:version|answer)(?:\s+is(?:\s+that)?\s+|\s*[,.!:;\u2014\u2013]+\s*)" +            // "Short version:"  "Short version is that"
        @")", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningFiller();

    /// <summary>
    /// A text the model was meant to leave out: the JSON null, an empty string, or the word "null", "none" or "n/a" written as if it
    /// were the value (the log showed three feedback points whose quote was the string "null", which the screen would have shown as
    /// something the candidate said). Returns null for those and the trimmed text otherwise.
    /// </summary>
    public static string? CleanOptional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Trim('"', '\'', '(', ')', '.', ' ').ToLowerInvariant() is "null" or "none" or "n/a" or "na" or "nil" or "undefined"
            ? null
            : trimmed;
    }

    /// <summary>
    /// Removes warm-up words from the start of a spoken answer ("Sure. Short version: ...", "Yeah, so ..."), then capitalises
    /// what is left. The prompt asks the model not to write them; this catches the ones that slip through and the ones
    /// already saved. An answer that would be left almost empty is returned unchanged.
    /// </summary>
    public static string WithoutOpeningFiller(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";

        var result = text;
        for (var pass = 0; pass < 5; pass++)
        {
            var next = OpeningFiller().Replace(result, "", 1);
            if (next.Length == result.Length) break;
            result = next;
        }

        result = result.TrimStart();
        if (result.Length < 20 || result.Length == text.TrimStart().Length) return text;
        return char.IsLower(result[0]) ? char.ToUpperInvariant(result[0]) + result[1..] : result;
    }

    /// <summary>
    /// Splits a model answer into plain text and bracketed placeholders such as "[your actual p99]",
    /// so the UI can highlight the parts the candidate has to fill in with real values.
    /// </summary>
    public static IReadOnlyList<Piece> SplitPlaceholders(string text)
    {
        var pieces = new List<Piece>();
        var last = 0;
        foreach (Match m in Placeholder().Matches(text))
        {
            if (m.Index > last) pieces.Add(new Piece(text[last..m.Index], false));
            pieces.Add(new Piece(m.Value, true));
            last = m.Index + m.Length;
        }
        if (last < text.Length) pieces.Add(new Piece(text[last..], false));
        return pieces;
    }

    /// <summary>Splits "A → B → C" into its steps. Accepts "->" too, since models sometimes type that instead of the arrow.</summary>
    public static IReadOnlyList<string> SplitShape(string shape)
        => shape.Split(["→", "->", "=>"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
