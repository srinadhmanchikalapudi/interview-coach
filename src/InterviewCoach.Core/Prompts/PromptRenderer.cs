using System.Text.RegularExpressions;

namespace InterviewCoach.Core.Prompts;

public static partial class PromptRenderer
{
    public const string Absent = "(none)";

    [GeneratedRegex(@"\{\{([A-Z0-9_]+)\}\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// Fills {{VAR}} placeholders in a single pass, so a value that itself contains "{{X}}" is left alone.
    /// A placeholder with no entry in <paramref name="vars"/> is a bug and throws; a null or blank value renders as "(none)".
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> vars)
    {
        var missing = new SortedSet<string>();
        var result = Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (!vars.TryGetValue(key, out var value))
            {
                missing.Add(key);
                return m.Value;
            }
            return string.IsNullOrWhiteSpace(value) ? Absent : value;
        });

        if (missing.Count > 0)
            throw new InvalidOperationException($"Unresolved prompt variable(s): {string.Join(", ", missing)}");
        return result;
    }
}
