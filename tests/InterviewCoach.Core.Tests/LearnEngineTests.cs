using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class LearnEngineTests
{
    private sealed record Call(LlmRole Role, string SystemPrompt, IReadOnlyList<ChatTurn> Messages);

    /// <summary>An LLM whose replies the test controls, including making a call wait until released.</summary>
    private sealed class ScriptedLlm : ILlmService
    {
        public List<Call> Calls { get; } = [];
        public Func<Call, Task<object>> Handler { get; set; } = _ => throw new InvalidOperationException("no handler");

        public async Task<T> GetJsonAsync<T>(LlmRole role, string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct)
        {
            var call = new Call(role, systemPrompt, messages);
            Calls.Add(call);
            return (T)await Handler(call);
        }

        public Task<IReadOnlyList<ConnectionTestResult>> TestConnectionAsync(AppSettings settings, CancellationToken ct)
            => throw new NotSupportedException();

        public IEnumerable<Call> To(LlmRole role) => Calls.Where(c => c.Role == role);
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "p",
        JobRole = "Backend Engineer",
        Seniority = Seniority.Senior,
        JobDescription = "JD-MARKER build services",
        ResumeText = "RESUME-MARKER ranking service",
    };

    private static QuestionDto Question(string text, string type = "behavioral") =>
        new() { Question = text, QuestionType = type, Source = "resume", Focus = "focus" };

    private static CoachOutput Coach(string answer = "model answer") => new()
    {
        WhatTheyreTesting = "signal",
        ModelAnswer = answer,
        Shape = "A → B",
        FollowUps = [new FollowUp { Question = "Why that?", Hint = "hint" }, new FollowUp { Question = "How did you measure it?", Hint = "hint 2" }],
    };

    private static (LearnEngine Engine, ScriptedLlm Llm) Create(Func<Call, Task<object>> handler)
    {
        var llm = new ScriptedLlm { Handler = handler };
        return (new LearnEngine(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"))), llm);
    }

    private static Func<Call, Task<object>> Sequence(params string[] questions)
    {
        var i = 0;
        return call => Task.FromResult<object>(call.Role == LlmRole.QuestionGenerator ? Question(questions[i++]) : Coach());
    }

    [Fact]
    public async Task First_question_then_coach_with_no_candidate_answer()
    {
        var (engine, llm) = Create(Sequence("Tell me about the ranking service."));

        await engine.StartAsync(Profile(), []);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Tell me about the ranking service.", engine.Current!.Question);
        Assert.Equal("model answer", engine.Current.Coach!.ModelAnswer);

        var gen = Assert.Single(llm.To(LlmRole.QuestionGenerator));
        Assert.Equal("Give me the next question.", gen.Messages.Single().Content);
        var coach = Assert.Single(llm.To(LlmRole.Coach));
        Assert.Equal("Coach this.", coach.Messages.Single().Content);

        // What the Coach prompt actually contains: learn mode, the question, and an empty answer.
        Assert.Contains("Mode: learn", coach.SystemPrompt);
        Assert.Contains("<question>\nTell me about the ranking service.\n</question>", coach.SystemPrompt.Replace("\r\n", "\n"));
        Assert.Contains("(none)", coach.SystemPrompt);
        Assert.Contains("RESUME-MARKER", coach.SystemPrompt);
        Assert.Contains("JD-MARKER", coach.SystemPrompt);
        Assert.DoesNotContain("{{", coach.SystemPrompt);
    }

    [Fact]
    public async Task Feedback_and_delivery_invented_by_the_model_are_dropped_in_learn_mode()
    {
        var chatty = new CoachOutput
        {
            WhatTheyreTesting = "signal",
            ModelAnswer = "the answer",
            Shape = "A → B",
            Delivery = "You spoke for 3:40",
            Feedback = [new FeedbackPoint { Kind = "missing", Point = "invented", Quote = null }],
            FollowUps = [new FollowUp { Question = "Why?", Hint = "h" }],
        };
        var (engine, _) = Create(call => Task.FromResult<object>(call.Role == LlmRole.QuestionGenerator ? Question("Q1") : chatty));

        await engine.StartAsync(Profile(), []);

        Assert.Empty(engine.Current!.Coach!.Feedback);
        Assert.Null(engine.Current.Coach.Delivery);
        Assert.Equal("the answer", engine.Current.Coach.ModelAnswer);
        Assert.Single(engine.Current.Coach.FollowUps);
    }

    [Fact]
    public async Task Coach_prompt_has_an_empty_answer_block_and_no_previous_attempt()
    {
        var (engine, llm) = Create(Sequence("Q1"));

        await engine.StartAsync(Profile(), []);

        var prompt = llm.To(LlmRole.Coach).Single().SystemPrompt.Replace("\r\n", "\n");
        Assert.Contains("<candidate_answer input_method=\"(none)\" duration_seconds=\"(none)\" word_count=\"(none)\">\n(none)\n</candidate_answer>", prompt);
        Assert.Contains("<previous_attempt>\n(none)\n</previous_attempt>", prompt);
    }

    [Fact]
    public async Task Question_is_visible_while_the_coach_is_still_working()
    {
        var release = new TaskCompletionSource<object>();
        var (engine, _) = Create(call => call.Role == LlmRole.QuestionGenerator ? Task.FromResult<object>(Question("Q1")) : release.Task);

        var running = engine.StartAsync(Profile(), []);

        Assert.Equal(LearnPhase.LoadingAnswer, engine.Phase);
        Assert.Equal("Q1", engine.Current!.Question);
        Assert.Null(engine.Current.Coach);

        release.SetResult(Coach());
        await running;
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.NotNull(engine.Current.Coach);
    }

    [Fact]
    public async Task First_generator_prompt_has_no_earlier_questions_and_later_ones_are_numbered()
    {
        var (engine, llm) = Create(Sequence("First question?", "Second question?", "Third question?"));

        await engine.StartAsync(Profile(), []);
        await engine.NextAsync();
        await engine.NextAsync();

        var prompts = llm.To(LlmRole.QuestionGenerator).Select(c => c.SystemPrompt.Replace("\r\n", "\n")).ToList();
        Assert.Contains("<already_asked_this_session>\n(none)\n</already_asked_this_session>", prompts[0]);
        Assert.Contains("1. First question?", prompts[1]);
        Assert.DoesNotContain("Second question?", prompts[1]);
        Assert.Contains("1. First question?\n2. Second question?", prompts[2]);
        Assert.Equal(["First question?", "Second question?", "Third question?"], engine.AskedQuestions);
    }

    [Fact]
    public async Task Type_filter_reaches_the_generator_prompt()
    {
        var (any, anyLlm) = Create(Sequence("Q"));
        await any.StartAsync(Profile(), []);
        Assert.Contains("<allowed_question_types>\nAny\n</allowed_question_types>", anyLlm.To(LlmRole.QuestionGenerator).Single().SystemPrompt.Replace("\r\n", "\n"));

        var (some, someLlm) = Create(Sequence("Q"));
        await some.StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.SystemDesign]);
        var prompt = someLlm.To(LlmRole.QuestionGenerator).Single().SystemPrompt.Replace("\r\n", "\n");
        Assert.Contains("<allowed_question_types>\n- system_design\n- behavioral\n</allowed_question_types>", prompt);
    }

    [Fact]
    public async Task A_word_for_word_repeat_is_asked_again_once()
    {
        var (engine, llm) = Create(Sequence("Q1", "Q1", "A different one?"));

        await engine.StartAsync(Profile(), []);
        await engine.NextAsync();

        Assert.Equal("A different one?", engine.Current!.Question);
        Assert.Equal(3, llm.To(LlmRole.QuestionGenerator).Count());
    }

    [Fact]
    public async Task Repeat_check_ignores_case_and_punctuation_but_gives_up_after_one_retry()
    {
        var (engine, llm) = Create(Sequence("Why Redis?", "why redis", "WHY REDIS!!"));

        await engine.StartAsync(Profile(), []);
        await engine.NextAsync();

        // Retried once, then accepted rather than looping forever.
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal(3, llm.To(LlmRole.QuestionGenerator).Count());
    }

    [Fact]
    public async Task Coach_failure_keeps_the_question_and_retry_only_repeats_the_coach_call()
    {
        var coachCalls = 0;
        var (engine, llm) = Create(call =>
        {
            if (call.Role == LlmRole.QuestionGenerator) return Task.FromResult<object>(Question("Q1"));
            return ++coachCalls == 1 ? throw new LlmException("network down") : Task.FromResult<object>(Coach());
        });

        await engine.StartAsync(Profile(), []);

        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Equal("network down", engine.Error);
        Assert.Equal("Q1", engine.Current!.Question);

        await engine.RetryAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Null(engine.Error);
        Assert.Single(llm.To(LlmRole.QuestionGenerator));
        Assert.Equal(2, llm.To(LlmRole.Coach).Count());
    }

    [Fact]
    public async Task Generator_failure_then_retry_generates_again()
    {
        var generatorCalls = 0;
        var (engine, llm) = Create(call =>
        {
            if (call.Role == LlmRole.Coach) return Task.FromResult<object>(Coach());
            return ++generatorCalls == 1 ? throw new LlmException("timed out") : Task.FromResult<object>(Question("Q1"));
        });

        await engine.StartAsync(Profile(), []);
        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Null(engine.Current);

        await engine.RetryAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Q1", engine.Current!.Question);
        Assert.Equal(2, llm.To(LlmRole.QuestionGenerator).Count());
    }

    [Fact]
    public async Task Next_while_the_coach_is_still_working_abandons_it_and_a_late_reply_cannot_overwrite()
    {
        var firstCoach = new TaskCompletionSource<object>();
        var questions = new Queue<string>(["Q1", "Q2"]);
        var coachCalls = 0;
        var (engine, _) = Create(call =>
        {
            if (call.Role == LlmRole.QuestionGenerator) return Task.FromResult<object>(Question(questions.Dequeue()));
            return ++coachCalls == 1 ? firstCoach.Task : Task.FromResult<object>(Coach("answer for Q2"));
        });

        var first = engine.StartAsync(Profile(), []);
        Assert.Equal("Q1", engine.Current!.Question);

        await engine.NextAsync(); // user skips ahead
        Assert.Equal("Q2", engine.Current!.Question);
        Assert.Equal("answer for Q2", engine.Current.Coach!.ModelAnswer);

        firstCoach.SetResult(Coach("stale answer for Q1"));
        await first;

        Assert.Equal("Q2", engine.Current!.Question);
        Assert.Equal("answer for Q2", engine.Current.Coach!.ModelAnswer);
        Assert.Equal(LearnPhase.Ready, engine.Phase);
    }

    [Fact]
    public async Task Follow_up_gets_its_own_coach_call_with_the_earlier_exchange_as_context()
    {
        var (engine, llm) = Create(Sequence("Walk me through the ranking service."));
        await engine.StartAsync(Profile(), []);

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        Assert.True(engine.Current.IsFollowUp);
        Assert.Equal("Why that?", engine.Current.Question);
        Assert.Equal("Walk me through the ranking service.", engine.Current.ParentQuestion);
        Assert.Equal("hint", engine.Current.Hint);
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.True(engine.CanGoBack);

        var prompt = llm.To(LlmRole.Coach).Last().SystemPrompt.Replace("\r\n", "\n");
        Assert.Contains("<question>\nWhy that?\n</question>", prompt);
        Assert.Contains("Interviewer: Walk me through the ranking service.", prompt);
        Assert.Contains("Model answer the candidate was shown: model answer", prompt);
        Assert.Contains("Mode: learn", prompt);
    }

    [Fact]
    public async Task Back_returns_to_the_main_question_without_another_llm_call()
    {
        var (engine, llm) = Create(Sequence("Main question?"));
        await engine.StartAsync(Profile(), []);
        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);
        var callsBefore = llm.Calls.Count;

        engine.Back();

        Assert.Equal("Main question?", engine.Current!.Question);
        Assert.False(engine.Current.IsFollowUp);
        Assert.NotNull(engine.Current.Coach);
        Assert.False(engine.CanGoBack);
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal(callsBefore, llm.Calls.Count);
    }

    [Fact]
    public async Task Next_after_a_follow_up_starts_a_fresh_main_question_and_forgets_the_trail()
    {
        var (engine, llm) = Create(Sequence("Q1", "Q2"));
        await engine.StartAsync(Profile(), []);
        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        await engine.NextAsync();

        Assert.Equal("Q2", engine.Current!.Question);
        Assert.False(engine.CanGoBack);
        Assert.Contains("Why that?", engine.AskedQuestions); // follow-ups that were studied count as asked
        var secondGen = llm.To(LlmRole.QuestionGenerator).Last().SystemPrompt;
        Assert.Contains("Why that?", secondGen);
        // The second main question's coach prompt must not drag in the earlier exchange.
        Assert.DoesNotContain("Model answer the candidate was shown", llm.To(LlmRole.Coach).Last().SystemPrompt);
    }

    [Fact]
    public async Task Opening_a_follow_up_before_the_answer_exists_does_nothing()
    {
        var release = new TaskCompletionSource<object>();
        var (engine, llm) = Create(call => call.Role == LlmRole.QuestionGenerator ? Task.FromResult<object>(Question("Q1")) : release.Task);
        var running = engine.StartAsync(Profile(), []);

        await engine.OpenFollowUpAsync(new FollowUp { Question = "x", Hint = "y" });

        Assert.Equal("Q1", engine.Current!.Question);
        Assert.Single(llm.To(LlmRole.Coach));
        release.SetResult(Coach());
        await running;
    }

    // ---- answer length and the scenario type

    [Fact]
    public async Task Without_a_requested_length_the_coach_is_given_the_range_interviewers_expect_for_the_question_type()
    {
        // Left to guess, the Coach wrote 250 to 300 words even for short concept questions. It is now told the same range
        // the screen shows beside the answer. Sequence() makes behavioral questions: 150 to 280 words.
        var (engine, llm) = Create(Sequence("Q1"));

        await engine.StartAsync(Profile(), []);

        var prompt = llm.To(LlmRole.Coach).Single().SystemPrompt;
        Assert.Contains("Requested model answer length: between 150 and 280 words (roughly 1 min 10 sec to 2 min 10 sec spoken)", prompt);
        Assert.Contains("Question type: behavioral", prompt);
    }

    [Fact]
    public async Task The_range_follows_the_question_type_and_matches_what_the_screen_shows()
    {
        var (engine, llm) = Create(call => Task.FromResult<object>(call.Role == LlmRole.QuestionGenerator
            ? new QuestionDto { Question = "What is a closure?", QuestionType = "technical_concept", Source = "fundamentals", Focus = "closures" }
            : Coach()));

        await engine.StartAsync(Profile(), []);

        var prompt = llm.To(LlmRole.Coach).Single().SystemPrompt;
        Assert.Contains("Requested model answer length: between 60 and 150 words (roughly 30 sec to 1 min 10 sec spoken)", prompt);
        Assert.Contains("Question type: technical_concept", prompt);
        Assert.Equal((60, 150), AnswerLength.Expectation("technical_concept")); // the screen's expectation uses the same table
    }

    [Fact]
    public async Task A_type_the_app_does_not_know_leaves_the_length_to_the_coach()
    {
        var (engine, llm) = Create(call => Task.FromResult<object>(call.Role == LlmRole.QuestionGenerator
            ? new QuestionDto { Question = "Something odd?", QuestionType = "mystery_type", Source = "jd", Focus = "x" }
            : Coach()));

        await engine.StartAsync(Profile(), []);

        Assert.Contains("Requested model answer length: (none)", llm.To(LlmRole.Coach).Single().SystemPrompt);
    }

    [Fact]
    public async Task An_explicit_word_count_wins_over_the_range()
    {
        var (engine, llm) = Create(Sequence("Q1"));

        await engine.StartAsync(Profile(), [], answerWords: 80);

        var prompt = llm.To(LlmRole.Coach).Single().SystemPrompt;
        Assert.Contains("Requested model answer length: about 80 words", prompt);
        Assert.DoesNotContain("Requested model answer length: between", prompt);
    }

    [Fact]
    public async Task Follow_ups_keep_the_parents_type_and_so_the_same_range()
    {
        var (engine, llm) = Create(Sequence("Q1"));
        await engine.StartAsync(Profile(), []);

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        var followUpPrompt = llm.To(LlmRole.Coach).Last().SystemPrompt;
        Assert.Contains("Question type: behavioral", followUpPrompt);
        Assert.Contains("between 150 and 280 words", followUpPrompt);
    }

    [Fact]
    public async Task A_requested_length_reaches_every_coach_prompt_including_follow_ups_and_background_work()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));

        await engine.StartAsync(Profile(), [], answerWords: 120);
        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);
        await engine.NextAsync();

        var coachPrompts = llm.To(LlmRole.Coach).Select(c => c.SystemPrompt).ToList();
        Assert.True(coachPrompts.Count >= 4);
        Assert.All(coachPrompts, p => Assert.Contains("Requested model answer length: about 120 words (roughly 55 sec spoken)", p));
    }

    [Fact]
    public async Task Choosing_the_scenario_type_puts_it_in_the_generator_filter()
    {
        var (engine, llm) = Create(Sequence("Q1"));

        await engine.StartAsync(Profile(), [QuestionType.Scenario, QuestionType.Behavioral]);

        var prompt = llm.To(LlmRole.QuestionGenerator).Single().SystemPrompt.Replace("\r\n", "\n");
        Assert.Contains("<allowed_question_types>\n- behavioral\n- scenario\n</allowed_question_types>", prompt);
    }

    [Fact]
    public async Task Any_still_means_every_type_when_all_of_them_are_ticked()
    {
        var (engine, llm) = Create(Sequence("Q1"));

        await engine.StartAsync(Profile(), QuestionTypes.All.ToList());

        Assert.Contains("<allowed_question_types>\nAny\n", llm.To(LlmRole.QuestionGenerator).Single().SystemPrompt.Replace("\r\n", "\n"));
    }

    // ---- prefetching the next question in the background

    private static (LearnEngine Engine, ScriptedLlm Llm) CreatePrefetching(Func<Call, Task<object>> handler)
    {
        var llm = new ScriptedLlm { Handler = handler };
        var engine = new LearnEngine(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"))) { PrefetchNext = true };
        return (engine, llm);
    }

    [Fact]
    public async Task Prefetch_is_off_unless_asked_for()
    {
        var (engine, llm) = Create(Sequence("Q1", "Q2"));

        await engine.StartAsync(Profile(), []);

        Assert.Single(llm.To(LlmRole.QuestionGenerator));
        Assert.Single(llm.To(LlmRole.Coach));
    }

    [Fact]
    public async Task Next_question_is_prepared_in_the_background_once_the_current_one_is_ready()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));

        await engine.StartAsync(Profile(), []);

        Assert.Equal("Q1", engine.Current!.Question);              // what is shown has not changed
        Assert.Equal(2, llm.To(LlmRole.QuestionGenerator).Count()); // but Q2 and its answer are already on the way
        Assert.Equal(2, llm.To(LlmRole.Coach).Count());
        Assert.Equal(["Q1", "Q2"], engine.AskedQuestions);
    }

    [Fact]
    public async Task Next_shows_the_prepared_question_without_another_wait_and_prepares_the_one_after()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));
        await engine.StartAsync(Profile(), []);

        await engine.NextAsync();

        Assert.Equal("Q2", engine.Current!.Question);
        Assert.NotNull(engine.Current.Coach);
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal(3, llm.To(LlmRole.QuestionGenerator).Count());  // Q3 is now being prepared
        Assert.Equal(["Q1", "Q2", "Q3"], engine.AskedQuestions);
    }

    [Fact]
    public async Task The_prepared_question_is_known_to_the_generator_so_it_is_not_repeated()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));

        await engine.StartAsync(Profile(), []);
        await engine.NextAsync();

        var thirdPrompt = llm.To(LlmRole.QuestionGenerator).Last().SystemPrompt.Replace("\r\n", "\n");
        Assert.Contains("1. Q1\n2. Q2", thirdPrompt);
    }

    [Fact]
    public async Task Next_while_the_background_answer_is_still_running_waits_for_it_instead_of_starting_over()
    {
        var release = new TaskCompletionSource<object>();
        var coachCalls = 0;
        var questions = new Queue<string>(["Q1", "Q2", "Q3"]);
        var (engine, llm) = CreatePrefetching(call =>
        {
            if (call.Role == LlmRole.QuestionGenerator) return Task.FromResult<object>(Question(questions.Dequeue()));
            return ++coachCalls == 2 ? release.Task : Task.FromResult<object>(Coach());
        });
        await engine.StartAsync(Profile(), []);

        var next = engine.NextAsync();

        Assert.False(next.IsCompleted);
        Assert.Equal(LearnPhase.GeneratingQuestion, engine.Phase);
        Assert.Equal(2, llm.To(LlmRole.QuestionGenerator).Count()); // no second attempt was started

        release.SetResult(Coach("prepared answer"));
        await next;

        Assert.Equal("Q2", engine.Current!.Question);
        Assert.Equal("prepared answer", engine.Current.Coach!.ModelAnswer);
        Assert.Equal(LearnPhase.Ready, engine.Phase);
    }

    [Fact]
    public async Task A_failed_background_attempt_falls_back_to_the_normal_path()
    {
        var coachCalls = 0;
        var questions = new Queue<string>(["Q1", "Q2", "Q3", "Q4"]);
        var (engine, _) = CreatePrefetching(call =>
        {
            if (call.Role == LlmRole.QuestionGenerator) return Task.FromResult<object>(Question(questions.Dequeue()));
            return ++coachCalls == 2 ? throw new LlmException("overloaded") : Task.FromResult<object>(Coach());
        });
        await engine.StartAsync(Profile(), []);

        await engine.NextAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Q3", engine.Current!.Question); // Q2's background attempt died, so a fresh question was generated
        Assert.Null(engine.Error);
        Assert.NotNull(engine.Current.Coach);
    }

    [Fact]
    public async Task Cancel_discards_background_work_and_a_late_reply_changes_nothing()
    {
        var release = new TaskCompletionSource<object>();
        var coachCalls = 0;
        var questions = new Queue<string>(["Q1", "Q2", "Q3", "Q4"]);
        var (engine, llm) = CreatePrefetching(call =>
        {
            if (call.Role == LlmRole.QuestionGenerator) return Task.FromResult<object>(Question(questions.Dequeue()));
            return ++coachCalls == 2 ? release.Task : Task.FromResult<object>(Coach());
        });
        await engine.StartAsync(Profile(), []);

        engine.Cancel();
        release.SetResult(Coach("too late"));
        await Task.Yield();

        Assert.Equal("Q1", engine.Current!.Question);
        Assert.Equal(LearnPhase.Ready, engine.Phase);

        await engine.NextAsync(); // the discarded background question must not be reused
        Assert.Equal("Q3", engine.Current!.Question);
        Assert.Equal("model answer", engine.Current.Coach!.ModelAnswer); // never the "too late" reply
        Assert.DoesNotContain("too late", engine.Current.Coach.ModelAnswer);
    }

    [Fact]
    public async Task Opening_a_follow_up_does_not_trigger_more_background_work()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));
        await engine.StartAsync(Profile(), []);
        var generatorCalls = llm.To(LlmRole.QuestionGenerator).Count();

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        Assert.Equal(generatorCalls, llm.To(LlmRole.QuestionGenerator).Count());
    }

    [Fact]
    public async Task The_background_answer_is_written_without_follow_up_context()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3"));
        await engine.StartAsync(Profile(), []);
        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        await engine.NextAsync();

        // The Coach prompt for the prepared Q2 must not carry the Q1 follow-up exchange.
        var q2Prompt = llm.To(LlmRole.Coach).Single(c => c.SystemPrompt.Replace("\r\n", "\n").Contains("<question>\nQ2\n</question>"));
        Assert.DoesNotContain("Model answer the candidate was shown", q2Prompt.SystemPrompt);
    }

    [Fact]
    public async Task Starting_a_new_session_drops_questions_prepared_for_the_old_one()
    {
        var (engine, llm) = CreatePrefetching(Sequence("Q1", "Q2", "Q3", "Q4"));
        await engine.StartAsync(Profile(), []);

        await engine.StartAsync(Profile(), []);

        Assert.Equal("Q3", engine.Current!.Question); // not the Q2 prepared for the first session
        Assert.Equal(["Q3", "Q4"], engine.AskedQuestions);
    }

    [Fact]
    public async Task Changed_is_raised_through_each_phase()
    {
        var (engine, _) = Create(Sequence("Q1"));
        var phases = new List<LearnPhase>();
        engine.Changed += () => phases.Add(engine.Phase);

        await engine.StartAsync(Profile(), []);

        Assert.Equal([LearnPhase.GeneratingQuestion, LearnPhase.LoadingAnswer, LearnPhase.Ready], phases);
    }
}
