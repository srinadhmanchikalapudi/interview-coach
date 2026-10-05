using System.Text;
using System.Text.RegularExpressions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Speech;

/// <summary>
/// The words a recognizer should expect to hear in an interview: the technologies, the employers, the role, the focus areas. Speech
/// recognition gets general English right and technical terms wrong (".NET" as "dot net", "queue" as "cube"); telling it which terms are likely
/// makes it prefer them. Azure takes a phrase list; Whisper takes a prompt (see <see cref="AsPrompt"/>).
/// </summary>
public static partial class SpeechPhrases
{
    /// <summary>The most phrases handed to a recognizer: more dilutes the help.</summary>
    public const int MaxPhrases = 100;

    /// <summary>The longest phrase kept, in characters.</summary>
    public const int MaxLength = 40;

    /// <summary>The most words in one phrase: a longer one is a sentence, not a term.</summary>
    public const int MaxWords = 4;

    [GeneratedRegex(@"^\s*(?:[-•*]\s*)?(?:technical skills|skills|technologies|tech stack|environment|tools|languages|frameworks|databases|cloud|platforms)\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SkillLine();

    // "SQL Server 2012", "C# 5.0/6.0/7.0": the version numbers are noise to a recognizer.
    [GeneratedRegex(@"\s+v?[\d][\d.\-/x]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    /// <summary>
    /// Merges lists of terms, most important first, into one list a recognizer can use: tidied, without duplicates (ignoring case), without
    /// anything that is not a short term, and at most <see cref="MaxPhrases"/> long. A term from an earlier list is kept in preference to a later one.
    /// </summary>
    public static IReadOnlyList<string> Build(params IEnumerable<string?>?[] lists)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();
        foreach (var list in lists)
        {
            if (list is null) continue;
            foreach (var raw in list)
            {
                var term = Tidy(raw);
                if (term is null || !seen.Add(term)) continue;
                phrases.Add(term);
                if (phrases.Count == MaxPhrases) return phrases;
            }
        }
        return phrases;
    }

    /// <summary>The terms on the skills, technologies and environment lines of a resume (a line such as "Skills: C#, .NET, Redis").</summary>
    public static IReadOnlyList<string> FromResume(string? resume)
    {
        if (string.IsNullOrWhiteSpace(resume)) return [];
        var terms = new List<string>();
        foreach (var line in resume.Split('\n'))
        {
            if (SkillLine().Match(line.TrimEnd('\r')) is not { Success: true } m) continue;
            terms.AddRange(m.Groups[1].Value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return terms;
    }

    /// <summary>The terms a Practice or Mock session has to hand: the picked technologies, the job description's, the role, the resume's skills and employers.</summary>
    public static IReadOnlyList<string> ForProfile(
        CandidateProfile profile, IEnumerable<string>? picked, IEnumerable<string>? jobTechnologies, IEnumerable<ResumeTopic>? topics)
        => Build(
            picked,
            jobTechnologies,
            FromResume(profile.ResumeText),
            topics?.Select(t => t.Employer),
            topics?.Select(t => t.Project),
            [profile.JobRole]);

    /// <summary>
    /// The text for Whisper's prompt (it takes a free-text hint, not a list): a short sentence and the terms, cut to <paramref name="maxChars"/> at a
    /// term boundary. Empty when there are no terms.
    /// </summary>
    public static string AsPrompt(IReadOnlyList<string> phrases, int maxChars = 600)
    {
        if (phrases.Count == 0) return "";
        var text = new StringBuilder("A technical job interview. Terms that may be spoken: ");
        var first = true;
        foreach (var phrase in phrases)
        {
            var piece = (first ? "" : ", ") + phrase;
            if (text.Length + piece.Length + 1 > maxChars) break;
            text.Append(piece);
            first = false;
        }
        return first ? "" : text.Append('.').ToString();
    }

    private static string? Tidy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var term = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.', ',', ';', ':', '(', ')', '[', ']', '"', '\'', '-', '•', '*');
        // keep a leading dot of ".NET": it was trimmed above only from the ends, so restore it when the original term began with one
        if (raw.TrimStart().StartsWith('.') && term.Length > 0 && !term.StartsWith('.') && char.IsLetter(term[0])) term = "." + term;
        term = TrailingVersion().Replace(term, "").Trim();
        if (term.Length < 2 || term.Length > MaxLength) return null;
        if (term.Split(' ').Length > MaxWords) return null;
        if (!term.Any(char.IsLetter)) return null;
        return term;
    }
}

/// <summary>
/// Finds the terms for a session without calling the model: the technologies of the job description and the employers of the resume, when they
/// were read before and saved, plus the resume's own skills lines. It never causes a model call, so starting the microphone costs nothing.
/// </summary>
public sealed class SpeechPhraseSource(TechBank? bank)
{
    public async Task<IReadOnlyList<string>> ForProfileAsync(CandidateProfile profile, IEnumerable<string>? picked = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> jobTechnologies = [];
        IReadOnlyList<ResumeTopic> topics = [];
        if (bank is not null)
        {
            try
            {
                jobTechnologies = await bank.GetSavedTechnologiesAsync(profile, ct);
                topics = await bank.GetSavedResumeTopicsAsync(profile, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The terms only help recognition; the session goes on without them.
            }
        }
        return SpeechPhrases.ForProfile(profile, picked, jobTechnologies, topics);
    }
}
