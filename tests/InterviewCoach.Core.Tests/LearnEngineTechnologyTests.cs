using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

/// <summary>"By technology": the user ticks technologies from the job description and gets questions about those.</summary>
public class LearnEngineTechnologyTests
{
    private sealed class Harness
    {
        public BankScript Script { get; } = new();
        public InMemoryTechBankRepository Repo { get; } = new();
        public ScriptedLlmService Llm { get; }
        private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        public Harness() => Llm = new ScriptedLlmService(Script.Handle);

        public LearnEngine NewEngine(Func<double>? random = null, bool withBank = true)
            => new(Llm, _prompts, withBank ? new TechBank(Repo, Llm, _prompts, () => 0.0) : null, random ?? (() => 0.0));
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior,
        JobDescription = "We use C#, Redis and Docker.", ResumeText = "RESUME-MARKER",
    };

    // ---- technologies only

    [Fact]
    public async Task Only_the_ticked_technologies_are_asked_about_and_the_model_is_not_involved()
    {
        var h = new Harness();
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);

        Assert.Equal("Redis concept question 1?", engine.Current!.Question);
        Assert.Equal("Redis", engine.Current.Technology);
        Assert.Equal("technical_concept", engine.Current.QuestionType);
        Assert.Empty(h.Llm.ModelQuestionCalls);                    // nothing written from the resume
        Assert.Empty(h.Llm.TagCalls);                              // the technologies were already chosen: no need to read the job description
        Assert.Single(h.Llm.BankQuestionCalls);
        Assert.Contains("<focus_technology>\nRedis\n</focus_technology>", h.Llm.BankQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task Every_question_in_the_session_stays_on_the_ticked_technologies()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.0);
        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis", "Docker"]);
        var technologies = new List<string> { engine.Current!.Technology! };

        for (var i = 0; i < 5; i++)
        {
            await engine.NextAsync();
            technologies.Add(engine.Current!.Technology!);
        }

        Assert.All(technologies, t => Assert.Contains(t, new[] { "Redis", "Docker" }));
        Assert.All(h.Llm.BankQuestionCalls, c => Assert.Matches("<focus_technology>\n(Redis|Docker)\n</focus_technology>", c.Prompt));
        Assert.Empty(h.Llm.ModelQuestionCalls);
    }

    [Fact]
    public async Task With_several_technologies_the_same_one_is_not_asked_twice_in_a_row()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.0); // always picks the first candidate, which would be Redis every time
        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis", "Docker"]);
        var technologies = new List<string> { engine.Current!.Technology! };

        for (var i = 0; i < 4; i++)
        {
            await engine.NextAsync();
            technologies.Add(engine.Current!.Technology!);
        }

        for (var i = 1; i < technologies.Count; i++)
            Assert.NotEqual(technologies[i - 1], technologies[i]);
    }

    [Fact]
    public async Task A_single_ticked_technology_is_asked_about_every_time()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);

        await engine.NextAsync();
        await engine.NextAsync();

        Assert.Equal("Redis", engine.Current!.Technology);
        Assert.Equal(3, h.Llm.BankQuestionCalls.Count());
        Assert.Equal(new TechBankStats(3, 3), await h.Repo.GetStatsAsync());
    }

    [Fact]
    public async Task A_technology_not_listed_in_the_job_description_works_the_same()
    {
        var h = new Harness();
        h.Script.Technologies = ["C#"];
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Kafka"]);

        Assert.Equal("Kafka", engine.Current!.Technology);
    }

    [Fact]
    public async Task Saved_questions_for_a_ticked_technology_are_reused_in_a_later_session()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(), [], focusTechnologies: ["Redis"]);
        h.Llm.Calls.Clear();

        var again = h.NewEngine();
        await again.StartAsync(Profile(), [], focusTechnologies: ["redis"]); // case does not matter

        Assert.Equal("Redis concept question 1?", again.Current!.Question);
        Assert.Empty(h.Llm.Calls);
    }

    [Fact]
    public async Task Technology_names_are_trimmed_and_deduplicated_ignoring_case()
    {
        var h = new Harness();
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [], focusTechnologies: [" Redis ", "redis", "  "]);
        await engine.NextAsync();

        Assert.Equal("Redis", engine.Current!.Technology);
        Assert.All(h.Llm.BankQuestionCalls, c => Assert.Contains("<focus_technology>\nRedis\n", c.Prompt));
    }

    [Fact]
    public async Task The_general_answer_for_a_technology_question_is_written_without_the_resume()
    {
        var h = new Harness();
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);

        Assert.True(engine.Current!.IsGeneric);
        Assert.Equal("GENERAL answer", engine.Current.Coach!.ModelAnswer);
        Assert.DoesNotContain("RESUME-MARKER", string.Concat(h.Llm.Calls.Select(c => c.Prompt)));
    }

    // ---- technologies together with other types

    [Fact]
    public async Task With_another_type_ticked_the_technologies_get_half_and_the_model_the_other_half()
    {
        var h = new Harness();
        var rolls = new Queue<double>([0.2, 0.0, 0.9]); // route (0.2 < 0.5: technology), pick technology, route (0.9: model)
        var engine = h.NewEngine(random: () => rolls.Count > 0 ? rolls.Dequeue() : 0.0);

        await engine.StartAsync(Profile(), [QuestionType.Behavioral], focusTechnologies: ["Redis"]);
        Assert.Equal("Redis", engine.Current!.Technology);

        await engine.NextAsync();

        Assert.Null(engine.Current!.Technology);
        var modelCall = h.Llm.ModelQuestionCalls.Single();
        Assert.Contains("<allowed_question_types>\n- behavioral\n</allowed_question_types>", modelCall.Prompt);
    }

    [Fact]
    public async Task Ticking_Technical_concept_as_well_does_not_add_a_second_share_for_the_technologies()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99); // would route to the model if there were another share

        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept], focusTechnologies: ["Redis"]);

        Assert.Equal("Redis", engine.Current!.Technology);
        Assert.Empty(h.Llm.ModelQuestionCalls);
    }

    [Fact]
    public async Task The_model_is_never_offered_technical_concept_while_technologies_are_being_served()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99);

        await engine.StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.Scenario], focusTechnologies: ["Redis"]);

        var prompt = h.Llm.ModelQuestionCalls.Single().Prompt;
        Assert.Contains("<allowed_question_types>\n- behavioral\n- scenario\n</allowed_question_types>", prompt);
    }

    // ---- the bank switched off

    [Fact]
    public async Task With_the_bank_off_the_model_writes_a_question_about_the_technology_tailored_to_the_resume()
    {
        var h = new Harness();
        var engine = h.NewEngine(withBank: false);

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Redis", engine.Current!.Technology);
        Assert.False(engine.Current.IsGeneric);
        Assert.Equal("TAILORED answer", engine.Current.Coach!.ModelAnswer);
        var prompt = Assert.Single(h.Llm.BankQuestionCalls).Prompt;   // the generator call that names a technology
        Assert.Contains("<focus_technology>\nRedis\n</focus_technology>", prompt);
        Assert.Contains("<allowed_question_types>\n- technical_concept\n</allowed_question_types>", prompt);
        Assert.Contains("RESUME-MARKER", prompt);                     // written with the resume, as every non-bank question is
        Assert.Equal(new TechBankStats(0, 0), await h.Repo.GetStatsAsync());
    }

    // ---- failures

    [Fact]
    public async Task When_only_technologies_were_asked_for_a_failure_is_shown_with_retry_instead_of_asking_about_something_else()
    {
        var h = new Harness();
        h.Script.FailBankQuestions = true;
        var engine = h.NewEngine();

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);

        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Null(engine.Current);
        Assert.Empty(h.Llm.ModelQuestionCalls);     // it did not quietly switch to a different kind of question

        h.Script.FailBankQuestions = false;
        await engine.RetryAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("Redis", engine.Current!.Technology);
    }

    [Fact]
    public async Task With_other_types_ticked_a_failed_technology_question_falls_back_to_the_model()
    {
        var h = new Harness();
        h.Script.FailBankQuestions = true;
        var engine = h.NewEngine(random: () => 0.0);

        await engine.StartAsync(Profile(), [QuestionType.Behavioral], focusTechnologies: ["Redis"]);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Null(engine.Current!.Technology);
        Assert.Contains("<allowed_question_types>\n- behavioral\n</allowed_question_types>", h.Llm.ModelQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task A_model_that_keeps_repeating_one_question_ends_in_a_clear_retryable_message()
    {
        var llm = new ScriptedLlmService(call => call.Role switch
        {
            LlmRole.QuestionGenerator => Task.FromResult<object>(new QuestionDto { Question = "What is a cache?", QuestionType = "technical_concept", Source = "fundamentals", Focus = "caching" }),
            _ => Task.FromResult<object>(new CoachOutput { ModelAnswer = "answer", Shape = "A → B" }),
        });
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var engine = new LearnEngine(llm, prompts, new TechBank(new InMemoryTechBankRepository(), llm, prompts, () => 0.0), () => 0.0);
        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);
        Assert.Equal("What is a cache?", engine.Current!.Question);

        await engine.NextAsync();

        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Contains("Redis", engine.Error);
        Assert.Contains("Try again", engine.Error);
    }

    // ---- sessions are independent

    [Fact]
    public async Task A_new_session_without_technologies_goes_back_to_the_normal_mix()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99);
        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);
        Assert.Equal("Redis", engine.Current!.Technology);

        await engine.StartAsync(Profile(), [QuestionType.Behavioral]); // no technologies this time

        Assert.Null(engine.Current!.Technology);
        Assert.Equal("Model question 1?", engine.Current.Question);
    }

    [Fact]
    public async Task Choosing_no_technologies_means_the_old_behaviour_for_Any()
    {
        var h = new Harness();
        var engine = h.NewEngine(random: () => 0.99);

        await engine.StartAsync(Profile(), [], focusTechnologies: []);

        Assert.Single(h.Llm.TagCalls);                       // Any still reads the job description for technologies
        Assert.Equal("Model question 1?", engine.Current!.Question);
    }

    [Fact]
    public async Task Prefetching_stays_on_the_ticked_technologies()
    {
        var h = new Harness();
        var llm = h.Llm;
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var engine = new LearnEngine(llm, prompts, new TechBank(h.Repo, llm, prompts, () => 0.0), () => 0.0) { PrefetchNext = true };

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"]);
        await engine.NextAsync();

        Assert.Equal("Redis concept question 2?", engine.Current!.Question);
        Assert.All(h.Llm.BankQuestionCalls, c => Assert.Contains("<focus_technology>\nRedis\n", c.Prompt));
        Assert.Empty(h.Llm.ModelQuestionCalls);
    }
}
