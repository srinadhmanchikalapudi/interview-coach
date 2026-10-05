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

    // Words that carry no topic, so two questions are compared by what they are about and not by how they are phrased.
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "was", "were", "be", "been", "do", "does", "did",
        "you", "your", "we", "our", "it", "its", "this", "that", "what", "why", "how", "when", "which", "who", "would", "could", "should",
        "can", "will", "at", "as", "by", "from", "about", "into", "than", "then", "there", "their", "they", "them", "have", "has", "had",
        "not", "no", "if", "so", "but", "vs", "versus", "between", "difference", "different", "over", "under", "up", "out", "ve", "re", "ll",
        "don", "didn", "isn", "me", "my", "any", "some", "one", "other", "tell", "walk", "through", "actually", "really", "specific",
    };

    private static HashSet<string> TopicWords(string question)
        => NormalizeQuestion(question).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !FillerWords.Contains(w)).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True when two questions ask about the same thing in different words, for example "Why did you choose Kafka over RabbitMQ
    /// for specific workloads?" and "How would you choose between Kafka and RabbitMQ for a new workload?". They count as the same
    /// when they share at least four topic words and those make up at least half of the shorter question's topic words.
    /// </summary>
    public static bool IsNearDuplicate(string a, string b)
    {
        if (SameQuestion(a, b)) return true;
        var first = TopicWords(a);
        var second = TopicWords(b);
        if (first.Count == 0 || second.Count == 0) return false;
        var shared = first.Count(second.Contains);
        return shared >= 4 && shared >= 0.5 * Math.Min(first.Count, second.Count);
    }

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
