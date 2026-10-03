using System.Text.Json;
using InterviewCoach.Core.Engines;

namespace InterviewCoach.Core.Models;

/// <summary>
/// A mock interview kept for the History screen: who it was for, the plan, the whole conversation, the debrief and the coaching of each
/// question. The parts are stored as JSON, written by <c>MockEngine</c>. The record is written as the interview goes (so a round that was
/// left can be resumed) and finished when it ends.
/// </summary>
public sealed class MockRecord
{
    public int Id { get; set; }
    public int ProfileId { get; init; }
    public string ProfileName { get; init; } = "";
    public string JobRole { get; init; } = "";
    public string RoundType { get; init; } = "";
    /// <summary>The label of the role type (Full-time or Contract), which the coach is told about.</summary>
    public string Employment { get; init; } = "";
    public int DurationMinutes { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; set; }
    public int ElapsedSeconds { get; set; }
    /// <summary>True once the round has ended (the interviewer closed it or the user ended it); false for one that was left midway.</summary>
    public bool Finished { get; set; }
    public string? PlanJson { get; init; }
    public string TurnsJson { get; set; } = "[]";
    public string? DebriefJson { get; set; }
    public string? ThreadsJson { get; set; }
    public string? HireSignal { get; set; }

    /// <summary>A round that was left midway: it can be picked up where it stopped.</summary>
    public bool IsUnfinished => !Finished;

    /// <summary>A round that ended but whose debrief was never written (it failed, or the app was closed): the debrief can be written now.</summary>
    public bool NeedsDebrief => Finished && string.IsNullOrEmpty(DebriefJson);
}

/// <summary>What was stored for one question thread, next to the conversation (see <see cref="MockEngine"/>).</summary>
public sealed class StoredThread
{
    public string Question { get; init; } = "";
    public string? Phase { get; init; }
    public string? FocusAreaId { get; init; }
    public string Status { get; init; } = "";
    public CoachOutput? Coach { get; init; }
    public string? Error { get; init; }
    public string? Transcript { get; init; }
}

/// <summary>A stored mock interview read back into the shapes the screens use. Any part that could not be read is null or empty.</summary>
public sealed class RestoredMock
{
    public IReadOnlyList<MockTurn> Turns { get; init; } = [];
    public InterviewPlanDto? Plan { get; init; }
    public DebriefDto? Debrief { get; init; }
    public IReadOnlyList<MockThread> Threads { get; init; } = [];
}

public static class MockRecords
{
    /// <summary>
    /// Reads a record back: the conversation, the plan, the debrief, and the question threads (rebuilt from the conversation, with the
    /// coaching that was stored for each). A thread whose coaching was never finished is shown as failed, since nothing will finish it now.
    /// </summary>
    public static RestoredMock Restore(MockRecord record)
    {
        var turns = Read<List<MockTurn>>(record.TurnsJson) ?? [];
        var plan = Read<InterviewPlanDto>(record.PlanJson);
        var debrief = Read<DebriefDto>(record.DebriefJson);
        var stored = Read<List<StoredThread>>(record.ThreadsJson) ?? [];

        var threads = MockText.BuildThreads(turns);
        for (var i = 0; i < threads.Count; i++)
        {
            var thread = threads[i];
            var saved = i < stored.Count && stored[i].Question == thread.Question ? stored[i] : null;
            if (thread.Status == ThreadStatus.NotAnswered) continue;

            if (saved is { Coach: { } coach } && saved.Status == nameof(ThreadStatus.Done))
            {
                thread.Status = ThreadStatus.Done;
                thread.Coach = coach;
            }
            else
            {
                thread.Status = ThreadStatus.Failed;
                thread.Error = saved?.Error ?? "The coaching for this question was never finished.";
            }
        }
        return new RestoredMock { Turns = turns, Plan = plan, Debrief = debrief, Threads = threads };
    }

    /// <summary>The round type for a stored label, or Mixed when it is not one of ours.</summary>
    public static RoundType RoundOf(string? label)
        => RoundTypes.All.Cast<RoundType?>().FirstOrDefault(r => r!.Value.Label().Equals(label?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? RoundType.Mixed;

    /// <summary>The role type for a stored label, or Full-time when it is not one of ours.</summary>
    public static EmploymentType EmploymentOf(string? label)
        => EmploymentTypes.All.Cast<EmploymentType?>().FirstOrDefault(e => e!.Value.Label().Equals(label?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? EmploymentType.FullTime;

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, MockJson.Options);
        }
        catch (JsonException)
        {
            return null; // an unreadable part is treated as missing rather than breaking the whole history
        }
    }
}
