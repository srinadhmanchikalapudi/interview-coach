using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class LearnEngineBankTests
{
    private sealed class Harness
    {
        public BankScript Script { get; } = new();
        public InMemoryTechBankRepository Repo { get; } = new();
        public ScriptedLlmService Llm { get; }
        private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        public Harness() => Llm = new ScriptedLlmService(Script.Handle);

        /// <summary>A new engine over the same saved bank, like reopening the app. <paramref name="random"/> decides bank vs model.</summary>
        public LearnEngine NewEngine(Func<double>? random = null, bool prefetch = false)
            => new(Llm, _prompts, new TechBank(Repo, Llm, _prompts, () => 0.0), random ?? (() => 0.0)) { PrefetchNext = prefetch };
    }

    private static CandidateProfile Profile(string jd = "We use C# and SQL Server.", string resume = "RESUME-MARKER") => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = jd, ResumeText = resume,
    };

    private static readonly QuestionType[] TechnicalOnly = [QuestionType.TechnicalConcept];

    // ---- serving technical questions from the bank

    [Fact]
    public async Task A_technical_question_comes_from_the_bank_with_a_general_answer_that_is_saved()
    {
        var h = new Harness();
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), TechnicalOnly);

        var item = engine.Current!;
        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("C# concept question 1?", item.Question);
        Assert.Equal("C#", item.Technology);
        Assert.Equal("technical_concept", item.QuestionType);
        Assert.True(item.IsGeneric);
        Assert.Equal("GENERAL answer", item.Coach!.ModelAnswer);
        Assert.Empty(h.Llm.ModelQuestionCalls);                 // the model was not asked to write it from the resume
        Assert.Empty(h.Llm.TailoredAnswerCalls);
        Assert.Equal(new TechBankStats(1, 1), await h.Repo.GetStatsAsync());
        Assert.DoesNotContain("RESUME-MARKER", string.Concat(h.Llm.Calls.Select(c => c.Prompt)));  // nothing personal was sent at all
    }

    [Fact]
    public async Task A_second_session_reuses_the_saved_question_and_answer_for_free()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(), TechnicalOnly);
        h.Llm.Calls.Clear();

        var again = h.NewEngine();
        await again.StartAsync(Profile(), TechnicalOnly);

        Assert.Equal("C# concept question 1?", again.Current!.Question);
        Assert.Equal("GENERAL answer", again.Current.Coach!.ModelAnswer);
        Assert.Equal(LearnPhase.Ready, again.Phase);
        Assert.Empty(h.Llm.Calls); // no tags call, no question call, no answer call
    }

    [Fact]
    public async Task Editing_the_resume_changes_nothing_about_what_is_reused()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(resume: "old resume"), TechnicalOnly);
        h.Llm.Calls.Clear();

        var again = h.NewEngine();
        await again.StartAsync(Profile(resume: "a brand new resume with different projects"), TechnicalOnly);

        Assert.Empty(h.Llm.Calls);
        Assert.Equal("C# concept question 1?", again.Current!.Question);
    }

    [Fact]
    public async Task Editing_the_job_description_only_costs_one_cheap_tags_call_and_reuses_shared_technologies()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(jd: "We use C# and SQL Server."), TechnicalOnly);
        h.Llm.Calls.Clear();

        var again = h.NewEngine();
        await again.StartAsync(Profile(jd: "Now also needs Docker. We use C# and SQL Server."), TechnicalOnly);

        Assert.Single(h.Llm.TagCalls);                  // the new job description is read once
        Assert.Empty(h.Llm.BankQuestionCalls);          // C# is still in the bank, so no new question is written
        Assert.Empty(h.Llm.GeneralAnswerCalls);         // and its answer is still saved
        Assert.Equal("C# concept question 1?", again.Current!.Question);
    }

    [Fact]
    public async Task The_next_technical_question_is_a_new_one_only_after_the_saved_ones_are_used_up()
    {
        var h = new Harness();
        h.Script.Technologies = ["C#"];
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);

        await engine.NextAsync();

        Assert.Equal("C# concept question 2?", engine.Current!.Question);
        Assert.Equal(2, h.Llm.BankQuestionCalls.Count());
        Assert.Equal(new TechBankStats(2, 2), await h.Repo.GetStatsAsync());
    }

    [Fact]
    public async Task Answers_are_saved_per_requested_length()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(), TechnicalOnly, answerWords: 120);
        h.Llm.Calls.Clear();

        var shortAgain = h.NewEngine();
        await shortAgain.StartAsync(Profile(), TechnicalOnly, answerWords: 120);
        Assert.Empty(h.Llm.Calls); // same question, same length: reused

        var normal = h.NewEngine();
        await normal.StartAsync(Profile(), TechnicalOnly, answerWords: null);
        Assert.Equal("C# concept question 1?", normal.Current!.Question); // the question is reused...
        Assert.Single(h.Llm.GeneralAnswerCalls);                          // ...but a new length needs a new answer
        Assert.Equal(new TechBankStats(1, 2), await h.Repo.GetStatsAsync());
    }

    // ---- how the bank and the model share the work

    [Fact]
    public async Task With_Any_type_the_bank_supplies_its_share_and_the_model_the_rest_without_technical_questions()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99); // never lands in the bank's share

        await engine.StartAsync(Profile(), []);

        Assert.Equal("Model question 1?", engine.Current!.Question);
        var prompt = Assert.Single(h.Llm.ModelQuestionCalls).Prompt;
        Assert.Contains("<allowed_question_types>\n- tell_me_about_yourself\n- resume_deep_dive\n- system_design\n- coding_talkthrough\n- behavioral\n- scenario\n- motivation_fit\n</allowed_question_types>", prompt);
        Assert.Empty(h.Llm.BankQuestionCalls);
    }

    [Fact]
    public async Task With_Any_type_a_low_roll_picks_the_bank()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.0);

        await engine.StartAsync(Profile(), []);

        Assert.True(engine.Current!.IsGeneric);
        Assert.Equal("C#", engine.Current.Technology);
        Assert.Empty(h.Llm.ModelQuestionCalls);
    }

    [Fact]
    public async Task The_bank_gets_one_in_N_of_the_allowed_types()
    {
        var h = new Harness();
        // With two types the bank gets one half. Draws in order: route (0.49: bank), pick a technology, route (0.99: model).
        var rolls = new Queue<double>([0.49, 0.0, 0.99]);
        var engine = h.NewEngine(random: () => rolls.Count > 0 ? rolls.Dequeue() : 0.0);

        await engine.StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.TechnicalConcept]); // 0.49 < 0.5: bank
        Assert.True(engine.Current!.IsGeneric);

        await engine.NextAsync(); // 0.99 >= 0.5: model, told only about behavioral
        Assert.False(engine.Current!.IsGeneric);
        Assert.Contains("<allowed_question_types>\n- behavioral\n</allowed_question_types>", h.Llm.ModelQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task Without_technical_concept_in_the_filter_the_bank_is_never_touched()
    {
        var h = new Harness();
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.Scenario]);

        Assert.Empty(h.Llm.TagCalls);
        Assert.Empty(h.Llm.BankQuestionCalls);
        Assert.False(engine.Current!.IsGeneric);
        Assert.Equal(new TechBankStats(0, 0), await h.Repo.GetStatsAsync());
    }

    [Fact]
    public async Task When_the_job_description_names_no_technologies_the_model_keeps_technical_questions()
    {
        var h = new Harness();
        h.Script.Technologies = [];
        var engine = h.NewEngine(random: () => 0.99);

        await engine.StartAsync(Profile(), []);

        Assert.Contains("<allowed_question_types>\nAny\n", h.Llm.ModelQuestionCalls.Single().Prompt); // nothing was taken away from the model
        Assert.False(engine.Current!.IsGeneric);
    }

    [Fact]
    public async Task A_failed_tags_call_does_not_fail_the_session()
    {
        var h = new Harness();
        h.Script.FailTags = true;
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), TechnicalOnly);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Model question 1?", engine.Current!.Question);
        Assert.Contains("<allowed_question_types>\n- technical_concept\n", h.Llm.ModelQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task A_failed_bank_question_falls_back_to_the_model_and_keeps_technical_questions_allowed()
    {
        var h = new Harness();
        h.Script.FailBankQuestions = true;
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), []);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.False(engine.Current!.IsGeneric);
        Assert.Contains("<allowed_question_types>\nAny\n", h.Llm.ModelQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task A_failed_general_answer_shows_the_question_and_retry_writes_it_again()
    {
        var h = new Harness();
        h.Script.FailGeneralAnswers = true;
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), TechnicalOnly);

        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Equal("C# concept question 1?", engine.Current!.Question);
        Assert.Equal(new TechBankStats(1, 0), await h.Repo.GetStatsAsync()); // the question is saved, the answer is not

        h.Script.FailGeneralAnswers = false;
        await engine.RetryAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("GENERAL answer", engine.Current.Coach!.ModelAnswer);
        Assert.Equal(new TechBankStats(1, 1), await h.Repo.GetStatsAsync());
    }

    [Fact]
    public async Task Without_a_bank_nothing_changes()
    {
        var h = new Harness();
        var engine = new LearnEngine(h.Llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")));

        await engine.StartAsync(Profile(), []);

        Assert.Empty(h.Llm.TagCalls);
        Assert.False(engine.Current!.IsGeneric);
        Assert.Contains("<allowed_question_types>\nAny\n", h.Llm.ModelQuestionCalls.Single().Prompt);
    }

    // ---- follow-ups and tailoring

    [Fact]
    public async Task A_follow_up_to_a_general_answer_is_general_too_and_is_not_saved()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);
        var saved = await h.Repo.GetStatsAsync();

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);

        Assert.True(engine.Current.IsGeneric);
        Assert.Equal("C#", engine.Current.Technology);
        Assert.Equal("GENERAL answer", engine.Current.Coach!.ModelAnswer);
        Assert.Equal(saved, await h.Repo.GetStatsAsync());
        var last = h.Llm.GeneralAnswerCalls.Last().Prompt;
        Assert.Contains("Interviewer: C# concept question 1?", last); // it still knows what it follows
        Assert.DoesNotContain("RESUME-MARKER", last);
    }

    [Fact]
    public async Task Tailoring_writes_a_normal_answer_from_the_resume_and_does_not_save_it()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);
        var statsBefore = await h.Repo.GetStatsAsync();

        await engine.PersonalizeAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.False(engine.Current!.IsGeneric);
        Assert.Equal("TAILORED answer", engine.Current.Coach!.ModelAnswer);
        var call = Assert.Single(h.Llm.TailoredAnswerCalls);
        Assert.Contains("RESUME-MARKER", call.Prompt);
        Assert.Contains("We use C# and SQL Server.", call.Prompt);
        Assert.Equal(statsBefore, await h.Repo.GetStatsAsync()); // the general answer in the bank is untouched
    }

    [Fact]
    public async Task Tailoring_shows_the_loading_state_and_a_failure_can_be_retried()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);
        var phases = new List<LearnPhase>();
        engine.Changed += () => phases.Add(engine.Phase);

        await engine.PersonalizeAsync();

        Assert.Equal([LearnPhase.LoadingAnswer, LearnPhase.Ready], phases);
    }

    [Fact]
    public async Task Tailoring_something_that_is_not_a_general_answer_does_nothing()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99);
        await engine.StartAsync(Profile(), []);
        var calls = h.Llm.Calls.Count;

        await engine.PersonalizeAsync();

        Assert.Equal(calls, h.Llm.Calls.Count);
        Assert.Equal("TAILORED answer", engine.Current!.Coach!.ModelAnswer);
    }

    [Fact]
    public async Task A_tailored_answer_survives_going_back_and_forth_in_the_trail()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);
        await engine.PersonalizeAsync();

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);
        engine.Back();

        Assert.Equal("TAILORED answer", engine.Current!.Coach!.ModelAnswer);
        Assert.False(engine.Current.IsGeneric);
    }

    // ---- prefetching works with the bank

    [Fact]
    public async Task Background_preparation_uses_the_bank_too_and_tailoring_does_not_throw_it_away()
    {
        var h = new Harness();
        h.Script.Technologies = ["C#"];
        var engine = h.NewEngine(prefetch: true);

        await engine.StartAsync(Profile(), TechnicalOnly);
        Assert.Equal(2, h.Llm.BankQuestionCalls.Count()); // the second question is already being prepared

        await engine.PersonalizeAsync();                   // must not restart the preparation
        Assert.Equal(2, h.Llm.BankQuestionCalls.Count());

        await engine.NextAsync();
        Assert.Equal("C# concept question 2?", engine.Current!.Question);
        Assert.Equal("GENERAL answer", engine.Current.Coach!.ModelAnswer);
    }
}
