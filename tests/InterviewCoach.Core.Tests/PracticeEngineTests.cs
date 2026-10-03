using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class PracticeEngineTests
{
    private sealed class Rig
    {
        public BankScript Script { get; } = new() { ModelQuestionType = "behavioral" };
        public InMemoryTechBankRepository Bank { get; } = new();
        public ScriptedLlmService Llm { get; }
        public bool FailCoach { get; set; }
        public TaskCompletionSource<bool>? HoldCoach { get; set; }
        public int CoachReplies { get; private set; }
        private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        public Rig() => Llm = new ScriptedLlmService(Handle);

        private async Task<object> Handle(LlmCall call)
        {
            if (call.Role != LlmRole.Coach) return await Script.Handle(call);
            if (HoldCoach is { } hold) await hold.Task;
            if (FailCoach) throw new LlmException("the coach is unavailable");
            CoachReplies++;
            return new CoachOutput
            {
                WhatTheyreTesting = "signal",
                Feedback =
                [
                    new FeedbackPoint { Kind = "strength", Point = "You gave a number.", Quote = "cut p99 from 300ms to 80ms" },
                    new FeedbackPoint { Kind = "fix", Point = "Say what you chose.", Quote = null },
                ],
                ModelAnswer = "Sure. Short version: I cut p99 by caching the hot lookups.",
                Shape = "A → B",
                Delivery = "Good length.",
                FollowUps = [new FollowUp { Question = "Why Redis?", Hint = "Name the alternative." }, new FollowUp { Question = "How did you measure it?", Hint = "Say the metric." }],
            };
        }

        public PracticeEngine NewEngine(bool withBank = true) => new(Llm, _prompts, withBank ? new TechBank(Bank, Llm, _prompts, () => 0.0) : null, () => 0.0);
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = "JD-MARKER", ResumeText = "RESUME-MARKER",
    };

    private static readonly QuestionType[] Behavioral = [QuestionType.Behavioral];

    private static IEnumerable<LlmCall> CoachCalls(Rig r) => r.Llm.To(LlmRole.Coach);

    private static string Prompt(Rig r, int index = -1) => (index < 0 ? CoachCalls(r).Last() : CoachCalls(r).ElementAt(index)).Prompt;

    // ---- the rule: no coaching before an answer

    [Fact]
    public async Task A_new_session_shows_a_question_and_waits_without_calling_the_coach()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), Behavioral);

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("Model question 1?", engine.Current!.Question);
        Assert.True(engine.CanSubmit);
        Assert.Empty(CoachCalls(rig));
        Assert.Null(engine.LastAttempt);
    }

    [Fact]
    public async Task Choosing_the_next_question_or_a_follow_up_or_trying_again_never_calls_the_coach_by_itself()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.NextAsync();
        Assert.Empty(CoachCalls(rig));

        await engine.SubmitAsync("my first answer");
        Assert.Single(CoachCalls(rig));

        engine.TryAgain();
        Assert.Single(CoachCalls(rig));          // trying again only waits for the next answer
        Assert.Equal(PracticePhase.Answering, engine.Phase);

        await engine.SubmitAsync("my second answer");
        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);
        Assert.Equal(2, CoachCalls(rig).Count());
        Assert.Equal(PracticePhase.Answering, engine.Phase);

        await engine.NextAsync();
        Assert.Equal(2, CoachCalls(rig).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n \t ")]
    public async Task An_empty_answer_is_refused_and_nothing_is_sent(string answer)
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        var result = await engine.SubmitAsync(answer);

        Assert.Equal(SubmitResult.Empty, result);
        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Empty(CoachCalls(rig));
    }

    [Fact]
    public async Task I_dont_know_is_a_fine_answer()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        Assert.Equal(SubmitResult.Sent, await engine.SubmitAsync("I don't know"));
        Assert.Equal(PracticePhase.ShowingFeedback, engine.Phase);
    }

    [Fact]
    public async Task Submitting_when_no_question_is_waiting_sends_nothing()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();

        Assert.Equal(SubmitResult.NotReady, await engine.SubmitAsync("an answer before any question")); // idle

        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("an answer");
        Assert.Equal(SubmitResult.NotReady, await engine.SubmitAsync("a second answer to the same question")); // showing feedback

        Assert.Single(CoachCalls(rig));
    }

    [Fact]
    public async Task A_second_submit_while_the_coach_is_still_reading_is_ignored()
    {
        var rig = new Rig { HoldCoach = new TaskCompletionSource<bool>() };
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        var first = engine.SubmitAsync("my answer");
        Assert.Equal(PracticePhase.Coaching, engine.Phase);
        var second = await engine.SubmitAsync("my answer again");
        rig.HoldCoach.SetResult(true);
        await first;

        Assert.Equal(SubmitResult.NotReady, second);
        Assert.Single(CoachCalls(rig));
    }

    // ---- what the coach is told

    [Fact]
    public async Task The_coach_gets_the_answer_how_it_was_given_and_the_numbers_with_practice_mode()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral, answerWords: 120, employment: EmploymentType.Contract);

        await engine.SubmitAsync("  We cut p99 from 300ms to 80ms by adding Redis.  ", AnswerInputMethod.Typed, durationSeconds: 95);

        var prompt = Prompt(rig);
        Assert.Contains("Mode: practice", prompt);
        Assert.Contains("<question>\nModel question 1?\n</question>", prompt.Replace("\r\n", "\n"));
        Assert.Contains("<candidate_answer input_method=\"typed\" duration_seconds=\"(none)\" word_count=\"10\">", prompt);   // typing time is not speaking time
        Assert.Contains("We cut p99 from 300ms to 80ms by adding Redis.", prompt);
        Assert.Contains("Employment type: Contract", prompt);
        Assert.Contains("Requested model answer length: about 120 words", prompt);
        Assert.Contains("RESUME-MARKER", prompt);
        Assert.Contains("JD-MARKER", prompt);
        Assert.Equal(["Coach this."], CoachCalls(rig).Last().Messages.Select(m => m.Content).ToArray());
    }

    [Fact]
    public async Task A_first_answer_has_no_previous_attempt_and_no_transcript()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.SubmitAsync("an answer");

        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("<previous_attempt>\n(none)\n</previous_attempt>", prompt);
        Assert.Contains("Earlier in this session: (none)", prompt);
    }

    [Fact]
    public async Task A_typed_answer_never_reports_a_duration_but_a_spoken_one_does()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.SubmitAsync("typed answer", AnswerInputMethod.Typed, durationSeconds: 219);
        Assert.Contains("duration_seconds=\"(none)\"", Prompt(rig));
        Assert.Equal(219, engine.LastAttempt!.DurationSeconds);   // the screen still shows how long it took

        engine.TryAgain();
        await engine.SubmitAsync("spoken answer", AnswerInputMethod.Voice, durationSeconds: 61);
        Assert.Contains("input_method=\"voice\" duration_seconds=\"61\"", Prompt(rig));
    }

    [Fact]
    public async Task With_no_timing_the_duration_is_left_out()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.SubmitAsync("an answer", durationSeconds: 0);

        Assert.Contains("duration_seconds=\"(none)\"", Prompt(rig));
    }

    [Fact]
    public async Task The_feedback_is_kept_and_the_model_answer_loses_its_warm_up()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.SubmitAsync("We cut p99 from 300ms to 80ms.", AnswerInputMethod.Typed, 40);

        var attempt = engine.LastAttempt!;
        Assert.Equal(PracticePhase.ShowingFeedback, engine.Phase);
        Assert.Equal(2, attempt.Coach.Feedback.Count);          // unlike Learn mode, the feedback is the point
        Assert.Equal("cut p99 from 300ms to 80ms", attempt.Coach.Feedback[0].Quote);
        Assert.Equal("Good length.", attempt.Coach.Delivery);
        Assert.Equal("I cut p99 by caching the hot lookups.", attempt.Coach.ModelAnswer);
        Assert.Equal("We cut p99 from 300ms to 80ms.", attempt.AnswerText);
        Assert.Equal(7, attempt.WordCount);
        Assert.Equal(40, attempt.DurationSeconds);
        Assert.Equal("typed", attempt.InputMethod);
    }

    // ---- trying again

    [Fact]
    public async Task Trying_again_keeps_the_question_and_passes_the_last_answer_so_the_coach_can_compare()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("first try");

        engine.TryAgain();

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("Model question 1?", engine.Current!.Question);
        Assert.Equal(2, engine.AttemptNumber);
        Assert.Null(engine.LastAttempt);

        await engine.SubmitAsync("second try, better");

        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("<previous_attempt>\nfirst try\n</previous_attempt>", prompt);
        Assert.Contains("second try, better", prompt);
        Assert.Single(rig.Llm.To(LlmRole.QuestionGenerator)); // no new question was written
    }

    [Theory]
    [InlineData("first try")]
    [InlineData("  first try  ")]
    [InlineData("FIRST   TRY")]
    [InlineData("first\ntry")]
    public async Task The_same_answer_again_on_a_second_try_is_not_sent(string again)
    {
        // The log of 2 October 2026: a retry that was word for word the first answer cost a Coach call and the coach did not notice.
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("first try");
        engine.TryAgain();

        var result = await engine.SubmitAsync(again);

        Assert.Equal(SubmitResult.Unchanged, result);
        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Single(CoachCalls(rig));                   // no second call
        Assert.Equal(2, engine.AttemptNumber);

        Assert.Equal(SubmitResult.Sent, await engine.SubmitAsync("first try, with one more sentence"));
        Assert.Equal(2, CoachCalls(rig).Count());
    }

    [Fact]
    public async Task The_same_words_are_fine_for_a_different_question_or_a_follow_up()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("the same words");
        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);

        Assert.Equal(SubmitResult.Sent, await engine.SubmitAsync("the same words"));   // a follow-up is not a retry

        await engine.NextAsync();
        Assert.Equal(SubmitResult.Sent, await engine.SubmitAsync("the same words"));   // nor is a new question
    }

    [Fact]
    public async Task A_third_try_is_compared_with_the_second_not_the_first()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("first try");
        engine.TryAgain();
        await engine.SubmitAsync("second try");
        engine.TryAgain();

        await engine.SubmitAsync("third try");

        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("<previous_attempt>\nsecond try\n</previous_attempt>", prompt);
        Assert.Equal(3, engine.AttemptNumber);
    }

    [Fact]
    public async Task Trying_again_is_only_possible_with_feedback_on_screen()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        engine.TryAgain();   // still answering: nothing happens

        Assert.Equal(1, engine.AttemptNumber);
        Assert.False(engine.CanTryAgain);
        await engine.SubmitAsync("an answer");
        Assert.True(engine.CanTryAgain);
    }

    // ---- follow-ups

    [Fact]
    public async Task A_follow_up_becomes_the_next_question_and_the_coach_sees_the_earlier_question_and_answer()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("We added Redis in front of the lookups.");

        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);

        Assert.Equal("Why Redis?", engine.Current!.Question);
        Assert.True(engine.Current.IsFollowUp);
        Assert.Equal("Model question 1?", engine.Current.ParentQuestion);
        Assert.Equal("Name the alternative.", engine.Current.Hint);
        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal(1, engine.AttemptNumber);
        Assert.Single(rig.Llm.To(LlmRole.QuestionGenerator)); // no new question was written

        await engine.SubmitAsync("Memcached lacked per-key expiry.");

        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("Earlier in this session: Interviewer: Model question 1?\nYou: We added Redis in front of the lookups.", prompt);
        Assert.Contains("<previous_attempt>\n(none)\n</previous_attempt>", prompt);   // a follow-up is not a retry
    }

    [Fact]
    public async Task A_second_follow_up_carries_the_whole_thread()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("answer one");
        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);
        await engine.SubmitAsync("answer two");
        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[1]);

        await engine.SubmitAsync("answer three");

        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("Interviewer: Model question 1?\nYou: answer one\nInterviewer: Why Redis?\nYou: answer two", prompt);
        Assert.Equal("Why Redis?", engine.Current!.ParentQuestion);   // the second follow-up hangs off the first
    }

    [Fact]
    public async Task A_follow_up_is_remembered_as_asked_so_it_is_not_asked_again_as_a_new_question()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("answer");

        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);

        Assert.Contains("Why Redis?", engine.AskedQuestions);
    }

    // ---- next question

    [Fact]
    public async Task The_next_question_starts_clean_with_no_earlier_answers_or_attempts()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("answer to question one");
        engine.AnswerFollowUp(engine.LastAttempt!.Coach.FollowUps[0]);
        await engine.SubmitAsync("answer to the follow-up");

        await engine.NextAsync();

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("Model question 2?", engine.Current!.Question);
        Assert.False(engine.Current.IsFollowUp);
        Assert.Null(engine.LastAttempt);
        Assert.Equal(1, engine.AttemptNumber);

        await engine.SubmitAsync("answer to question two");
        var prompt = Prompt(rig).Replace("\r\n", "\n");
        Assert.Contains("Earlier in this session: (none)", prompt);
        Assert.Contains("<previous_attempt>\n(none)\n</previous_attempt>", prompt);
    }

    [Fact]
    public async Task Questions_do_not_repeat_within_a_session()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        for (var i = 0; i < 3; i++) await engine.NextAsync();

        Assert.Equal(4, engine.AskedQuestions.Distinct().Count());
        Assert.Contains("Model question 3?", rig.Llm.To(LlmRole.QuestionGenerator).Last().Prompt);   // the earlier ones are in the avoid list
    }

    // ---- failures keep the answer

    [Fact]
    public async Task If_the_coach_fails_the_answer_is_kept_and_a_retry_sends_the_same_answer()
    {
        var rig = new Rig { FailCoach = true };
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.SubmitAsync("an answer that must not be lost", AnswerInputMethod.Typed, 70);

        Assert.Equal(PracticePhase.Failed, engine.Phase);
        Assert.Contains("the coach is unavailable", engine.Error);
        Assert.Equal("an answer that must not be lost", engine.PendingAnswer);
        Assert.Equal("Model question 1?", engine.Current!.Question);

        rig.FailCoach = false;
        await engine.RetryAsync();

        Assert.Equal(PracticePhase.ShowingFeedback, engine.Phase);
        Assert.Null(engine.Error);
        Assert.Equal("an answer that must not be lost", engine.LastAttempt!.AnswerText);
        Assert.Equal(70, engine.LastAttempt.DurationSeconds);
        Assert.Contains("an answer that must not be lost", Prompt(rig));
        Assert.Single(rig.Llm.To(LlmRole.QuestionGenerator));   // retrying the coach does not write a new question
    }

    [Fact]
    public async Task After_a_failure_the_answer_can_be_reopened_changed_and_sent_again()
    {
        var rig = new Rig { FailCoach = true };
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("my first wording");

        engine.EditAnswer();

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("my first wording", engine.PendingAnswer);   // still there to put back in the box
        Assert.Null(engine.Error);

        rig.FailCoach = false;
        await engine.SubmitAsync("my better wording");

        Assert.Equal(PracticePhase.ShowingFeedback, engine.Phase);
        Assert.Equal("my better wording", engine.LastAttempt!.AnswerText);
        Assert.Contains("my better wording", Prompt(rig));
    }

    [Fact]
    public async Task Reopening_an_answer_only_works_after_a_failed_coach_call()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("an answer");

        engine.EditAnswer();   // feedback is showing: nothing happens

        Assert.Equal(PracticePhase.ShowingFeedback, engine.Phase);
    }

    [Fact]
    public async Task A_failed_first_question_is_retried_by_writing_it_again()
    {
        var rig = new Rig();
        rig.Script.FailBankQuestions = true;
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept], focusTechnologies: ["C#"]);

        Assert.Equal(PracticePhase.Failed, engine.Phase);
        Assert.Null(engine.Current);

        rig.Script.FailBankQuestions = false;
        await engine.RetryAsync();

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.NotNull(engine.Current);
    }

    [Fact]
    public async Task Retry_does_nothing_when_nothing_failed()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);

        await engine.RetryAsync();

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Single(rig.Llm.To(LlmRole.QuestionGenerator));
    }

    [Fact]
    public async Task Moving_on_while_the_coach_is_reading_discards_the_late_feedback()
    {
        var hold = new TaskCompletionSource<bool>();
        var rig = new Rig { HoldCoach = hold };
        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), Behavioral);
        var submitting = engine.SubmitAsync("an answer the user walked away from");
        Assert.Equal(PracticePhase.Coaching, engine.Phase);

        rig.HoldCoach = null;
        await engine.NextAsync();
        Assert.Equal("Model question 2?", engine.Current!.Question);
        hold.SetResult(true);          // the abandoned reading finishes late
        await submitting;

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("Model question 2?", engine.Current!.Question);
        Assert.Null(engine.LastAttempt);
    }

    // ---- where questions come from

    [Fact]
    public async Task Practice_questions_come_from_the_same_sources_as_Learn_including_saved_technical_ones()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept], focusTechnologies: ["C#"]);

        Assert.Equal("C# concept question 1?", engine.Current!.Question);
        Assert.Equal("C#", engine.Current.Technology);
        Assert.Equal("technical_concept", engine.Current.QuestionType);
        Assert.Null(engine.Current.Coach);   // a saved model answer is never shown in Practice
        Assert.Empty(CoachCalls(rig));
        Assert.Empty(rig.Llm.GeneralAnswerCalls);
    }

    [Fact]
    public async Task Resume_questions_take_turns_between_employers_in_Practice_too()
    {
        var rig = new Rig();
        rig.Script.ModelQuestionType = "resume_deep_dive";
        rig.Script.ResumeEntries = [new ResumeTopicEntryDto { Employer = "CPF", Highlights = ["a"] }, new ResumeTopicEntryDto { Employer = "EDF", Highlights = ["b"] }];
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), [QuestionType.ResumeDeepDive]);
        await engine.NextAsync();

        var prompts = rig.Llm.ModelQuestionCalls.Select(c => c.Prompt).ToList();
        Assert.Contains("CPF", prompts[0].Split("<resume_focus>")[1]);
        Assert.Contains("EDF", prompts[1].Split("<resume_focus>")[1]);
    }

    // ---- starting from a question the user already read

    [Fact]
    public async Task A_session_can_start_from_a_question_without_asking_the_model_for_one()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        var learned = new LearnItem
        {
            Question = "What is a struct?", QuestionType = "technical_concept", Technology = "C#", IsGeneric = true, TechQuestionId = 7,
            Coach = new CoachOutput { ModelAnswer = "A struct is a value type." },
        };

        engine.StartWithQuestion(Profile(), learned, [QuestionType.TechnicalConcept], focusTechnologies: ["C#"]);

        Assert.Equal(PracticePhase.Answering, engine.Phase);
        Assert.Equal("What is a struct?", engine.Current!.Question);
        Assert.Equal("C#", engine.Current.Technology);
        Assert.Null(engine.Current.Coach);                      // the model answer the user just read is not shown while they try
        Assert.Empty(rig.Llm.Calls);

        await engine.NextAsync();                                // then the same filter as the session it came from
        Assert.Equal("C# concept question 1?", engine.Current!.Question);
        Assert.Contains("What is a struct?", engine.AskedQuestions);
    }

    [Fact]
    public async Task Changed_is_raised_as_the_phase_moves_through_a_round()
    {
        var rig = new Rig();
        var engine = rig.NewEngine();
        var phases = new List<PracticePhase>();
        engine.Changed += () => phases.Add(engine.Phase);

        await engine.StartAsync(Profile(), Behavioral);
        await engine.SubmitAsync("an answer");

        Assert.Equal([PracticePhase.GeneratingQuestion, PracticePhase.Answering, PracticePhase.Coaching, PracticePhase.ShowingFeedback], phases);
    }
}
