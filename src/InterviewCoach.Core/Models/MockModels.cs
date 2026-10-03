using InterviewCoach.Core.Engines;

namespace InterviewCoach.Core.Models;

/// <summary>The kind of round a Mock Interview simulates (spec 4.1).</summary>
public enum RoundType { RecruiterScreen, Technical, SystemDesign, Behavioral, HiringManager, Mixed }

public static class RoundTypes
{
    public static IReadOnlyList<RoundType> All { get; } = Enum.GetValues<RoundType>();

    /// <summary>The lengths offered, in minutes.</summary>
    public static IReadOnlyList<int> Durations { get; } = [15, 30, 45, 60];

    public const int DefaultDuration = 30;

    public static string Label(this RoundType type) => type switch
    {
        RoundType.RecruiterScreen => "Recruiter screen",
        RoundType.Technical => "Technical",
        RoundType.SystemDesign => "System design",
        RoundType.Behavioral => "Behavioral",
        RoundType.HiringManager => "Hiring manager",
        RoundType.Mixed => "Mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>One line shown on screen about what the round is like.</summary>
    public static string Description(this RoundType type) => type switch
    {
        RoundType.RecruiterScreen => "Background, motivation and the practical questions a recruiter asks.",
        RoundType.Technical => "Hands-on questions about the technologies and how you have used them.",
        RoundType.SystemDesign => "Design a system out loud and defend the trade-offs.",
        RoundType.Behavioral => "Stories about how you work with people, handle conflict and own results.",
        RoundType.HiringManager => "Ownership, judgment and impact, from the person you would work for.",
        RoundType.Mixed => "A bit of everything, the way a full loop can feel.",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The value sent as {{ROUND_TYPE}} in the prompts.</summary>
    public static string PromptValue(this RoundType type) => type.Label();
}

// ---- JSON shapes of the mock prompts (spec 8.1, 8.2, 8.5). Property names map to snake_case in JsonResponseParser.

/// <summary>Reply from planner.md.</summary>
public class InterviewPlanDto
{
    public List<FocusAreaDto> FocusAreas { get; init; } = [];
    public List<ClaimProbeDto> ResumeClaimsToProbe { get; init; } = [];
    public List<PhaseDto> Phases { get; init; } = [];
    public string OpeningLine { get; init; } = "";
}

public class FocusAreaDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Why { get; init; } = "";
    public string Source { get; init; } = "";
}

public class ClaimProbeDto
{
    public string Claim { get; init; } = "";
    public string Probe { get; init; } = "";
}

public class PhaseDto
{
    public string Phase { get; init; } = "";
    public int TargetMinutes { get; init; }
    public List<string> Topics { get; init; } = [];
}

/// <summary>Reply from interviewer.md: one turn of the live interviewer.</summary>
public class InterviewerTurnDto
{
    public string Say { get; init; } = "";
    public string TurnType { get; init; } = "";
    public string Phase { get; init; } = "";
    public string? FocusAreaId { get; init; }
    public bool EndInterview { get; init; }
}

/// <summary>Reply from debrief.md.</summary>
public class DebriefDto
{
    public string OverallSummary { get; init; } = "";
    public string HireSignal { get; init; } = "";
    public List<FocusRatingDto> FocusAreaRatings { get; init; } = [];
    public List<string> Strengths { get; init; } = [];
    public List<TopFixDto> TopFixes { get; init; } = [];
    public List<string> PracticeNext { get; init; } = [];
}

public class FocusRatingDto
{
    public string FocusAreaId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>1 to 4, or null when the focus area never came up.</summary>
    public int? Rating { get; init; }
    public string Evidence { get; init; } = "";
}

public class TopFixDto
{
    public string Fix { get; init; } = "";
    public string Example { get; init; } = "";
    public string HowToPractice { get; init; } = "";
}

/// <summary>How the hire signal of a debrief reads on screen.</summary>
public enum SignalTone { Negative, Neutral, Positive }

public static class HireSignals
{
    public static string Label(string? signal) => Normalise(signal) switch
    {
        "strong_no" => "Strong no",
        "no" => "No",
        "lean_no" => "Lean no",
        "lean_yes" => "Lean yes",
        "yes" => "Yes",
        "strong_yes" => "Strong yes",
        "" => "No signal",
        var other => char.ToUpperInvariant(other[0]) + other[1..].Replace('_', ' '),
    };

    public static SignalTone Tone(string? signal) => Normalise(signal) switch
    {
        "strong_no" or "no" => SignalTone.Negative,
        "yes" or "strong_yes" => SignalTone.Positive,
        _ => SignalTone.Neutral,
    };

    private static string Normalise(string? signal) => (signal ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    /// <summary>"3 of 4", or "Not covered" for a focus area that never came up.</summary>
    public static string RatingText(int? rating) => rating is >= 1 and <= 4 ? $"{rating} of 4" : "Not covered";
}

// ---- the conversation

public enum MockSpeaker { Interviewer, Candidate }

/// <summary>One line of the interview, in order.</summary>
public sealed class MockTurn
{
    public int Index { get; init; }
    public MockSpeaker Speaker { get; init; }
    public required string Text { get; init; }
    /// <summary>The raw JSON the interviewer returned (interviewer turns only), kept so the next request repeats it as it was.</summary>
    public string? RawJson { get; init; }
    public string? TurnType { get; init; }
    public string? Phase { get; init; }
    public string? FocusAreaId { get; init; }
    public string? InputMethod { get; init; }
    public int DurationSeconds { get; init; }
    public DateTime At { get; init; }
    /// <summary>Seconds into the interview when this turn was added (the interview's own clock, which does not run while it is left).</summary>
    public int ElapsedSeconds { get; init; }
}

public enum ThreadStatus { NotAnswered, Coaching, Done, Failed }

/// <summary>
/// One main question with its follow-ups, hints and clarifications, and the candidate's answers to them (spec 6.1). Each becomes a card
/// on the debrief with the Coach's view of the exchange.
/// </summary>
public sealed class MockThread
{
    public required string Question { get; init; }
    public string? Phase { get; init; }
    public string? FocusAreaId { get; init; }
    public List<MockTurn> Turns { get; } = [];

    public ThreadStatus Status { get; set; } = ThreadStatus.NotAnswered;
    public CoachOutput? Coach { get; set; }
    public string? Error { get; set; }

    public IEnumerable<MockTurn> CandidateTurns => Turns.Where(t => t.Speaker == MockSpeaker.Candidate);

    public bool HasAnswer => CandidateTurns.Any(t => !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>The exchange as "Interviewer: ... / You: ..." lines (spec 9), which is what the Coach is given.</summary>
    public string Transcript => MockText.Transcript(Turns);

    /// <summary>
    /// The exchange for the Coach: like <see cref="Transcript"/>, but each answer that was spoken is labelled with how long it took
    /// ("You (52s): ..."), so the total duration is not mistaken for the length of one answer.
    /// </summary>
    public string CoachTranscript => MockText.Transcript(Turns, withSpeakingTime: true);

    /// <summary>"typed", "voice" or "mixed" over every answer in the thread.</summary>
    public string InputMethod
    {
        get
        {
            var methods = CandidateTurns.Select(t => t.InputMethod ?? AnswerInputMethod.Typed).Distinct().ToList();
            return methods.Count == 0 ? AnswerInputMethod.Typed : methods.Count == 1 ? methods[0] : AnswerInputMethod.Mixed;
        }
    }

    /// <summary>Seconds spent answering by voice or mixed input; typing time says nothing about delivery, so typed answers count as zero.</summary>
    public int SpokenSeconds => CandidateTurns.Where(t => (t.InputMethod ?? AnswerInputMethod.Typed) != AnswerInputMethod.Typed).Sum(t => t.DurationSeconds);

    public int WordCount => CandidateTurns.Sum(t => AnswerLength.CountWords(t.Text));
}

public static class MockText
{
    public static string Transcript(IEnumerable<MockTurn> turns, bool withSpeakingTime = false)
        => string.Join("\n", turns.Select(t =>
        {
            if (t.Speaker == MockSpeaker.Interviewer) return $"Interviewer: {t.Text}";
            var spoken = withSpeakingTime && (t.InputMethod ?? AnswerInputMethod.Typed) != AnswerInputMethod.Typed && t.DurationSeconds > 0;
            return spoken ? $"You ({t.DurationSeconds}s): {t.Text}" : $"You: {t.Text}";
        }));

    /// <summary>
    /// Groups the interview into question threads (spec 6.1): each main question starts one; the follow-ups, hints and clarifications after
    /// it, and every answer of the candidate, belong to it until the next main question. Small talk, the candidate's own questions and the
    /// closing end the thread and are left out.
    /// </summary>
    public static List<MockThread> BuildThreads(IReadOnlyList<MockTurn> turns)
    {
        var threads = new List<MockThread>();
        MockThread? current = null;
        foreach (var turn in turns)
        {
            if (turn.Speaker == MockSpeaker.Interviewer)
            {
                switch ((turn.TurnType ?? "").Trim().ToLowerInvariant())
                {
                    case "main_question":
                        current = new MockThread { Question = turn.Text, Phase = turn.Phase, FocusAreaId = turn.FocusAreaId };
                        current.Turns.Add(turn);
                        threads.Add(current);
                        break;
                    case "follow_up" or "hint" or "clarification":
                        current?.Turns.Add(turn);
                        break;
                    default:
                        current = null; // smalltalk, candidate_questions, closing
                        break;
                }
            }
            else
            {
                current?.Turns.Add(turn);
            }
        }

        foreach (var thread in threads)
            thread.Status = thread.HasAnswer ? ThreadStatus.Coaching : ThreadStatus.NotAnswered;
        return threads;
    }
}
