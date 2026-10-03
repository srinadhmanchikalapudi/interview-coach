namespace InterviewCoach.Core.Speech;

/// <summary>The answer text after a dictated segment was put in, and where the cursor should go.</summary>
public readonly record struct DictationResult(string Text, int Caret);

/// <summary>
/// Puts a dictated segment into the answer box at the cursor, so the user can dictate, fix a word and keep talking in the same box.
/// Spaces are added only where they are needed: not at the start, not twice, and not before punctuation that follows.
/// </summary>
public static class DictationText
{
    public static DictationResult Insert(string text, int caret, string segment)
    {
        text ??= "";
        var piece = segment?.Trim() ?? "";
        caret = Math.Clamp(caret, 0, text.Length);
        if (piece.Length == 0) return new DictationResult(text, caret);

        var before = text[..caret];
        var after = text[caret..];

        var needSpaceBefore = before.Length > 0 && !char.IsWhiteSpace(before[^1]);
        var needSpaceAfter = after.Length > 0 && !char.IsWhiteSpace(after[0]) && !IsClosingPunctuation(after[0]);

        var inserted = (needSpaceBefore ? " " : "") + piece + (needSpaceAfter ? " " : "");
        return new DictationResult(before + inserted + after, caret + inserted.Length);
    }

    private static bool IsClosingPunctuation(char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '"' or '\'';
}
