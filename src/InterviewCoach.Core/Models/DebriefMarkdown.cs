using System.Text;

namespace InterviewCoach.Core.Models;

/// <summary>Writes a mock interview's debrief as a Markdown document the user can keep (spec 4.3, Export).</summary>
public static class DebriefMarkdown
{
    public static string Render(
        string jobRole, RoundType round, int durationMinutes, DateTime when, int elapsedSeconds, DebriefDto debrief, IReadOnlyList<MockThread> threads)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Mock interview debrief: {Line(jobRole)}");
        md.AppendLine();
        md.AppendLine($"- Round: {round.Label()}, {durationMinutes} minutes planned, {elapsedSeconds / 60}:{elapsedSeconds % 60:00} spent");
        md.AppendLine($"- Date: {when:yyyy-MM-dd HH:mm}");
        md.AppendLine($"- Hire signal: **{HireSignals.Label(debrief.HireSignal)}**");
        md.AppendLine();

        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine(Paragraph(debrief.OverallSummary));
        md.AppendLine();

        if (debrief.FocusAreaRatings.Count > 0)
        {
            md.AppendLine("## Focus areas");
            md.AppendLine();
            md.AppendLine("| Area | Rating | Evidence |");
            md.AppendLine("|---|---|---|");
            foreach (var r in debrief.FocusAreaRatings)
                md.AppendLine($"| {Cell(r.Name)} | {HireSignals.RatingText(r.Rating)} | {Cell(r.Evidence)} |");
            md.AppendLine();
        }

        if (debrief.Strengths.Count > 0)
        {
            md.AppendLine("## Strengths");
            md.AppendLine();
            foreach (var s in debrief.Strengths) md.AppendLine($"- {Line(s)}");
            md.AppendLine();
        }

        if (debrief.TopFixes.Count > 0)
        {
            md.AppendLine("## Top fixes");
            md.AppendLine();
            for (var i = 0; i < debrief.TopFixes.Count; i++)
            {
                var fix = debrief.TopFixes[i];
                md.AppendLine($"{i + 1}. **{Line(fix.Fix)}**");
                if (!string.IsNullOrWhiteSpace(fix.Example)) md.AppendLine($"   - Where it showed up: {Line(fix.Example)}");
                if (!string.IsNullOrWhiteSpace(fix.HowToPractice)) md.AppendLine($"   - How to practise: {Line(fix.HowToPractice)}");
            }
            md.AppendLine();
        }

        if (debrief.PracticeNext.Count > 0)
        {
            md.AppendLine("## Practise next");
            md.AppendLine();
            foreach (var p in debrief.PracticeNext) md.AppendLine($"- {Line(p)}");
            md.AppendLine();
        }

        if (threads.Count > 0)
        {
            md.AppendLine("## Question by question");
            md.AppendLine();
            for (var i = 0; i < threads.Count; i++) AppendThread(md, i + 1, threads[i]);
        }

        return md.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendThread(StringBuilder md, int number, MockThread thread)
    {
        md.AppendLine($"### {number}. {Line(thread.Question)}");
        md.AppendLine();
        foreach (var turn in thread.Turns)
            md.AppendLine($"> **{(turn.Speaker == MockSpeaker.Interviewer ? "Interviewer" : "You")}:** {Line(turn.Text)}");
        md.AppendLine();

        switch (thread.Status)
        {
            case ThreadStatus.NotAnswered:
                md.AppendLine("_Not answered, so there is nothing to coach._");
                md.AppendLine();
                return;
            case ThreadStatus.Failed:
                md.AppendLine($"_The coaching for this question could not be written: {Line(thread.Error ?? "unknown error")}_");
                md.AppendLine();
                return;
            case ThreadStatus.Coaching:
                md.AppendLine("_The coaching for this question was still being written when this was exported._");
                md.AppendLine();
                return;
        }

        var coach = thread.Coach!;
        md.AppendLine($"**What they were testing:** {Line(coach.WhatTheyreTesting)}");
        md.AppendLine();
        foreach (var f in coach.Feedback)
        {
            var label = f.Kind.Trim().ToLowerInvariant() switch { "strength" => "Worked", "fix" => "Fix", "missing" => "Missing", _ => "Note" };
            var quote = CoachText.CleanOptional(f.Quote);
            md.AppendLine($"- **{label}:** {Line(f.Point)}{(quote is null ? "" : $" (\"{Line(quote)}\")")}");
        }
        if (coach.Feedback.Count > 0) md.AppendLine();
        if (CoachText.CleanOptional(coach.Delivery) is { } delivery)
        {
            md.AppendLine($"**Delivery:** {Line(delivery)}");
            md.AppendLine();
        }
        md.AppendLine("**A strong answer:**");
        md.AppendLine();
        md.AppendLine($"> {Line(coach.ModelAnswer)}");
        md.AppendLine();
    }

    private static string Line(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Paragraph(string? text) => string.IsNullOrWhiteSpace(text) ? "_No summary._" : Line(text);

    // A pipe or a line break would end a table cell.
    private static string Cell(string? text) => Line(text).Replace("|", "\\|");
}
