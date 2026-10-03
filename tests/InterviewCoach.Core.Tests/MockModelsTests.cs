using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;

namespace InterviewCoach.Core.Tests;

public class MockModelsTests
{
    private static int _index;

    private static MockTurn I(string text, string type, string? phase = "technical")
        => new() { Index = _index++, Speaker = MockSpeaker.Interviewer, Text = text, TurnType = type, Phase = phase, FocusAreaId = "fa1" };

    private static MockTurn C(string text, string method = "typed", int seconds = 0)
        => new() { Index = _index++, Speaker = MockSpeaker.Candidate, Text = text, InputMethod = method, DurationSeconds = seconds };

    // ---- building threads

    [Fact]
    public void A_main_question_starts_a_thread_and_follow_ups_hints_and_clarifications_stay_in_it()
    {
        var threads = MockText.BuildThreads(
        [
            I("Hi!", "smalltalk", "opener"), C("Hello."),
            I("Walk me through the cache.", "main_question"), C("I added Redis."),
            I("Why Redis?", "follow_up"), C("Shared and fast."),
            I("Can you rephrase?", "clarification"), C("Sure, I mean the choice."),
            I("Think about eviction.", "hint"), C("LRU."),
            I("Tell me about a conflict.", "main_question", "behavioral"), C("Once..."),
        ]);

        Assert.Equal(2, threads.Count);
        Assert.Equal("Walk me through the cache.", threads[0].Question);
        Assert.Equal(8, threads[0].Turns.Count);
        Assert.Equal("technical", threads[0].Phase);
        Assert.Equal("fa1", threads[0].FocusAreaId);
        Assert.Equal("behavioral", threads[1].Phase);
        Assert.Equal(2, threads[1].Turns.Count);
    }

    [Fact]
    public void Small_talk_the_candidates_own_questions_and_the_closing_are_not_part_of_any_thread()
    {
        var threads = MockText.BuildThreads(
        [
            I("Hi!", "smalltalk", "opener"), C("Hello, fine."),
            I("Describe the cache.", "main_question"), C("Redis."),
            I("Any questions for me?", "candidate_questions", "candidate_questions"), C("What does the team look like?"),
            I("It is five people.", "candidate_questions", "candidate_questions"), C("Thanks!"),
            I("Thanks, goodbye.", "closing", "candidate_questions"),
        ]);

        var thread = Assert.Single(threads);
        Assert.Equal(2, thread.Turns.Count);
        Assert.DoesNotContain("team look like", thread.Transcript);
        Assert.DoesNotContain("goodbye", thread.Transcript);
        Assert.DoesNotContain("Hello, fine.", thread.Transcript);
    }

    [Fact]
    public void A_question_with_no_answer_is_marked_not_answered_and_the_rest_wait_to_be_coached()
    {
        var threads = MockText.BuildThreads([I("Q1?", "main_question"), I("Q2?", "main_question"), C("An answer.")]);

        Assert.Equal([ThreadStatus.NotAnswered, ThreadStatus.Coaching], threads.Select(t => t.Status));
        Assert.False(threads[0].HasAnswer);
    }

    [Fact]
    public void An_interview_with_no_main_question_has_no_threads()
    {
        Assert.Empty(MockText.BuildThreads([I("Hi!", "smalltalk", "opener"), C("Hello.")]));
        Assert.Empty(MockText.BuildThreads([]));
    }

    [Fact]
    public void The_transcript_is_interviewer_and_you_lines()
    {
        var threads = MockText.BuildThreads([I("Q?", "main_question"), C("A."), I("Why?", "follow_up"), C("B.")]);

        Assert.Equal("Interviewer: Q?\nYou: A.\nInterviewer: Why?\nYou: B.", threads[0].Transcript);
        Assert.Equal("Interviewer: Q?\nYou: A.", MockText.Transcript([I("Q?", "main_question"), C("A.")]));
    }

    [Fact]
    public void A_thread_reports_how_its_answers_were_given_and_only_counts_speaking_time()
    {
        var typed = MockText.BuildThreads([I("Q?", "main_question"), C("one two", "typed", 50)])[0];
        Assert.Equal("typed", typed.InputMethod);
        Assert.Equal(0, typed.SpokenSeconds);                      // typing time says nothing about delivery
        Assert.Equal(2, typed.WordCount);

        var voice = MockText.BuildThreads([I("Q?", "main_question"), C("a b c", "voice", 30), I("Why?", "follow_up"), C("d e", "voice", 20)])[0];
        Assert.Equal("voice", voice.InputMethod);
        Assert.Equal(50, voice.SpokenSeconds);
        Assert.Equal(5, voice.WordCount);

        var mixed = MockText.BuildThreads([I("Q?", "main_question"), C("a", "typed", 10), I("Why?", "follow_up"), C("b", "voice", 20)])[0];
        Assert.Equal("mixed", mixed.InputMethod);
        Assert.Equal(20, mixed.SpokenSeconds);
    }

    // ---- hire signal and ratings

    [Theory]
    [InlineData("strong_no", "Strong no", SignalTone.Negative)]
    [InlineData("no", "No", SignalTone.Negative)]
    [InlineData("lean_no", "Lean no", SignalTone.Neutral)]
    [InlineData("lean_yes", "Lean yes", SignalTone.Neutral)]
    [InlineData("yes", "Yes", SignalTone.Positive)]
    [InlineData("strong_yes", "Strong yes", SignalTone.Positive)]
    [InlineData("Lean Yes", "Lean yes", SignalTone.Neutral)]
    [InlineData("", "No signal", SignalTone.Neutral)]
    [InlineData(null, "No signal", SignalTone.Neutral)]
    [InlineData("maybe_later", "Maybe later", SignalTone.Neutral)]
    public void The_hire_signal_reads_in_words_with_a_tone(string? signal, string label, SignalTone tone)
    {
        Assert.Equal(label, HireSignals.Label(signal));
        Assert.Equal(tone, HireSignals.Tone(signal));
    }

    [Theory]
    [InlineData(1, "1 of 4")]
    [InlineData(4, "4 of 4")]
    [InlineData(null, "Not covered")]
    [InlineData(0, "Not covered")]
    [InlineData(7, "Not covered")]
    public void A_rating_reads_as_a_score_or_as_not_covered(int? rating, string text) => Assert.Equal(text, HireSignals.RatingText(rating));

    // ---- round types

    [Fact]
    public void Every_round_type_has_a_label_and_a_description_and_the_durations_are_the_four_in_the_spec()
    {
        Assert.Equal(6, RoundTypes.All.Count);
        Assert.All(RoundTypes.All, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Label()));
            Assert.False(string.IsNullOrWhiteSpace(r.Description()));
            Assert.Equal(r.Label(), r.PromptValue());
        });
        Assert.Equal(["Recruiter screen", "Technical", "System design", "Behavioral", "Hiring manager", "Mixed"], RoundTypes.All.Select(r => r.Label()));
        Assert.Equal([15, 30, 45, 60], RoundTypes.Durations);
        Assert.Contains(RoundTypes.DefaultDuration, RoundTypes.Durations);
    }

    // ---- Markdown export

    private static DebriefDto Debrief() => new()
    {
        OverallSummary = "You were clear on caching.\nVague on measuring.",
        HireSignal = "lean_yes",
        FocusAreaRatings =
        [
            new FocusRatingDto { Name = "Caching | Redis", Rating = 3, Evidence = "\"cut p99\"" },
            new FocusRatingDto { Name = "Design", Rating = null, Evidence = "never came up" },
        ],
        Strengths = ["Specific numbers"],
        TopFixes = [new TopFixDto { Fix = "Say how you measured", Example = "the p99 claim", HowToPractice = "Name the metric first" }],
        PracticeNext = ["How do you measure latency?"],
    };

    private static MockThread AnsweredThread()
    {
        var thread = MockText.BuildThreads([I("Walk me through the cache.", "main_question"), C("I added Redis and cut p99."), I("How did you measure it?", "follow_up"), C("On the dashboard.")])[0];
        thread.Status = ThreadStatus.Done;
        thread.Coach = new CoachOutput
        {
            WhatTheyreTesting = "ownership",
            Feedback = [new FeedbackPoint { Kind = "strength", Point = "Concrete.", Quote = "cut p99" }, new FeedbackPoint { Kind = "fix", Point = "Name the metric.", Quote = "null" }],
            ModelAnswer = "I measured p99 before and after.",
            Delivery = "none",
        };
        return thread;
    }

    [Fact]
    public void The_export_has_the_header_summary_ratings_strengths_fixes_and_what_to_practise()
    {
        var md = DebriefMarkdown.Render("Backend Engineer", RoundType.Technical, 30, new DateTime(2026, 10, 3, 12, 5, 0), 1_000, Debrief(), []);

        Assert.StartsWith("# Mock interview debrief: Backend Engineer", md);
        Assert.Contains("- Round: Technical, 30 minutes planned, 16:40 spent", md);
        Assert.Contains("- Date: 2026-10-03 12:05", md);
        Assert.Contains("- Hire signal: **Lean yes**", md);
        Assert.Contains("You were clear on caching. Vague on measuring.", md);      // one paragraph, no stray line break
        Assert.Contains("| Caching \\| Redis | 3 of 4 | \"cut p99\" |", md);           // a pipe in a cell is escaped
        Assert.Contains("| Design | Not covered | never came up |", md);
        Assert.Contains("- Specific numbers", md);
        Assert.Contains("1. **Say how you measured**", md);
        Assert.Contains("   - How to practise: Name the metric first", md);
        Assert.Contains("- How do you measure latency?", md);
        Assert.DoesNotContain("## Question by question", md);
        Assert.EndsWith(Environment.NewLine, md);
    }

    [Fact]
    public void The_export_has_each_question_with_its_exchange_and_the_coaching_without_a_null_quote_or_delivery()
    {
        var md = DebriefMarkdown.Render("Backend Engineer", RoundType.Technical, 30, DateTime.Now, 600, Debrief(), [AnsweredThread()]);

        Assert.Contains("### 1. Walk me through the cache.", md);
        Assert.Contains("> **Interviewer:** How did you measure it?", md);
        Assert.Contains("> **You:** On the dashboard.", md);
        Assert.Contains("**What they were testing:** ownership", md);
        Assert.Contains("- **Worked:** Concrete. (\"cut p99\")", md);
        Assert.Contains("- **Fix:** Name the metric.", md);
        Assert.DoesNotContain("(\"null\")", md);
        Assert.DoesNotContain("**Delivery:**", md);
        Assert.Contains("> I measured p99 before and after.", md);
    }

    [Fact]
    public void An_unanswered_failed_or_unfinished_thread_says_so_in_the_export()
    {
        var unanswered = MockText.BuildThreads([I("Q1?", "main_question")])[0];
        var failed = MockText.BuildThreads([I("Q2?", "main_question"), C("Answer.")])[0];
        failed.Status = ThreadStatus.Failed;
        failed.Error = "the coach is unavailable";
        var pending = MockText.BuildThreads([I("Q3?", "main_question"), C("Answer.")])[0];

        var md = DebriefMarkdown.Render("Dev", RoundType.Mixed, 15, DateTime.Now, 60, Debrief(), [unanswered, failed, pending]);

        Assert.Contains("_Not answered, so there is nothing to coach._", md);
        Assert.Contains("_The coaching for this question could not be written: the coach is unavailable_", md);
        Assert.Contains("_The coaching for this question was still being written when this was exported._", md);
    }
}
