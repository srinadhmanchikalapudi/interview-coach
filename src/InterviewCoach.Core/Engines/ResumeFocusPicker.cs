using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Engines;

/// <summary>
/// Chooses what each resume question is about so a session covers the whole resume instead of the current job over and over.
/// The employer used least so far goes first (ties are broken at random and never repeat the previous employer when there is a
/// choice), then the least used highlight within that employer. A rotating first word (Why, How, What...) also breaks the habit of
/// every question opening the same way. Nothing is counted until <see cref="Commit"/>, so a topic is only used up by a question that
/// was really asked about it.
/// </summary>
public sealed class ResumeFocusPicker(Func<double> random)
{
    private static readonly string[] Starters = ["Why", "How", "What", "When", "Which"];

    private readonly Dictionary<string, int> _employerUses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _topicUses = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastEmployer;
    private string? _lastWord;

    public ResumeFocus? Pick(IReadOnlyList<ResumeTopic> topics)
    {
        if (topics.Count == 0) return null;

        var employers = topics.Select(t => t.Employer).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var fewest = employers.Min(e => Uses(_employerUses, e));
        var candidates = employers.Where(e => Uses(_employerUses, e) == fewest).ToList();
        if (candidates.Count > 1 && _lastEmployer is not null)
            candidates = candidates.Where(e => !e.Equals(_lastEmployer, StringComparison.OrdinalIgnoreCase)).ToList();
        var employer = Choose(candidates);

        var own = topics.Where(t => t.Employer.Equals(employer, StringComparison.OrdinalIgnoreCase)).ToList();
        var least = own.Min(t => Uses(_topicUses, Key(t)));
        var topic = Choose(own.Where(t => Uses(_topicUses, Key(t)) == least).ToList());

        var word = Choose(Starters.Where(w => w != _lastWord).ToList());
        return new ResumeFocus(topic, word);
    }

    /// <summary>Counts the focus as used. Call it when a question about it has been asked.</summary>
    public void Commit(ResumeFocus focus)
    {
        _employerUses[focus.Topic.Employer] = Uses(_employerUses, focus.Topic.Employer) + 1;
        _topicUses[Key(focus.Topic)] = Uses(_topicUses, Key(focus.Topic)) + 1;
        _lastEmployer = focus.Topic.Employer;
        _lastWord = focus.Word;
    }

    private static string Key(ResumeTopic t) => $"{t.Employer}|{t.Project}|{t.Highlight}";

    private static int Uses(Dictionary<string, int> uses, string key) => uses.GetValueOrDefault(key);

    private T Choose<T>(List<T> items) => items[Math.Min(items.Count - 1, (int)(random() * items.Count))];
}
