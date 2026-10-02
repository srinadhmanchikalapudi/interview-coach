using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Models;

/// <summary>Small parsers for rendering Coach output.</summary>
public static partial class CoachText
{
    public record Piece(string Text, bool IsPlaceholder);

    [GeneratedRegex(@"\[[^\[\]\r\n]+\]")]
    private static partial Regex Placeholder();

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
