using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class EmploymentTypeModelTests
{
    [Fact]
    public void Full_time_is_the_default_kind_of_interview()
    {
        Assert.Equal(EmploymentType.FullTime, new AppSettings().EmploymentType);
    }

    [Theory]
    [InlineData(EmploymentType.FullTime, "Full-time")]
    [InlineData(EmploymentType.Contract, "Contract")]
    public void Each_kind_has_a_label_that_is_also_what_the_prompts_are_given(EmploymentType type, string label)
    {
        Assert.Equal(label, type.Label());
        Assert.Equal(label, type.PromptValue());
    }

    [Fact]
    public void Each_kind_explains_in_one_line_what_changes()
    {
        Assert.Contains("culture fit", EmploymentType.FullTime.Description());
        Assert.Contains("availability and rate", EmploymentType.Contract.Description());
        Assert.NotEqual(EmploymentType.FullTime.Description(), EmploymentType.Contract.Description());
    }

    // ---- question types that belong to one kind of interview

    [Fact]
    public void Motivation_and_fit_is_for_full_time_and_availability_and_engagement_is_for_contract()
    {
        Assert.Equal(EmploymentType.FullTime, QuestionType.MotivationFit.OnlyFor());
        Assert.Equal(EmploymentType.Contract, QuestionType.Engagement.OnlyFor());
        Assert.All(new[]
        {
            QuestionType.TellMeAboutYourself, QuestionType.ResumeDeepDive, QuestionType.TechnicalConcept, QuestionType.SystemDesign,
            QuestionType.CodingTalkthrough, QuestionType.Behavioral, QuestionType.Scenario,
        }, t => Assert.Null(t.OnlyFor()));
    }

    [Fact]
    public void A_full_time_interview_offers_the_common_types_plus_motivation_and_fit()
    {
        var types = QuestionTypes.Applicable(EmploymentType.FullTime);

        Assert.Equal(8, types.Count);
        Assert.Contains(QuestionType.MotivationFit, types);
        Assert.DoesNotContain(QuestionType.Engagement, types);
    }

    [Fact]
    public void A_contract_interview_offers_the_common_types_plus_availability_and_engagement()
    {
        var types = QuestionTypes.Applicable(EmploymentType.Contract);

        Assert.Equal(8, types.Count);
        Assert.Contains(QuestionType.Engagement, types);
        Assert.DoesNotContain(QuestionType.MotivationFit, types);
    }

    [Fact]
    public void Both_kinds_share_the_same_seven_common_types_in_the_same_order()
    {
        var fullTime = QuestionTypes.Applicable(EmploymentType.FullTime).Where(t => t.OnlyFor() is null);
        var contract = QuestionTypes.Applicable(EmploymentType.Contract).Where(t => t.OnlyFor() is null);

        Assert.Equal(7, fullTime.Count());
        Assert.Equal(fullTime, contract);
    }

    [Fact]
    public void The_new_types_have_ids_labels_and_word_ranges()
    {
        Assert.Equal("motivation_fit", QuestionType.MotivationFit.Id());
        Assert.Equal("engagement", QuestionType.Engagement.Id());
        Assert.Equal("Motivation and fit", QuestionType.MotivationFit.Label());
        Assert.Equal("Availability and engagement", QuestionType.Engagement.Label());
        Assert.Equal("Availability and engagement", QuestionTypes.LabelFor("engagement"));
        Assert.Equal((90, 160), AnswerLength.Expectation("motivation_fit"));
        Assert.Equal((40, 100), AnswerLength.Expectation("engagement")); // logistics answers are short and direct
    }

    [Fact]
    public void Any_covers_every_type_and_the_filter_lists_applicable_types_explicitly_otherwise()
    {
        Assert.Equal("Any", QuestionTypes.RenderFilter([]));
        Assert.Equal("Any", QuestionTypes.RenderFilter(QuestionTypes.All.ToList()));

        var contractTypes = QuestionTypes.Applicable(EmploymentType.Contract);
        var rendered = QuestionTypes.RenderFilter(contractTypes);

        Assert.Contains("- engagement", rendered);
        Assert.DoesNotContain("motivation_fit", rendered);
    }
}

public class LearnEngineEmploymentTests
{
    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static CandidateProfile Profile() => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior,
        JobDescription = "We use C# and Redis.", ResumeText = "RESUME-MARKER",
    };

    private static (LearnEngine Engine, ScriptedLlmService Llm, BankScript Script) Create(bool withBank = false, bool prefetch = false, Func<double>? random = null)
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var bank = withBank ? new TechBank(new InMemoryTechBankRepository(), llm, Prompts, () => 0.0) : null;
        return (new LearnEngine(llm, Prompts, bank, random ?? (() => 0.0)) { PrefetchNext = prefetch }, llm, script);
    }

    // ---- what the model is told

    [Fact]
    public async Task Full_time_is_assumed_and_both_prompts_say_so()
    {
        var (engine, llm, _) = Create();

        await engine.StartAsync(Profile(), []);

        Assert.Contains("<employment_type>\nFull-time\n</employment_type>", llm.ModelQuestionCalls.Single().Prompt);
        Assert.Contains("Employment type: Full-time", llm.TailoredAnswerCalls.Single().Prompt);
    }

    [Fact]
    public async Task Contract_reaches_the_question_generator_and_the_coach()
    {
        var (engine, llm, _) = Create();

        await engine.StartAsync(Profile(), [], employment: EmploymentType.Contract);

        Assert.Contains("<employment_type>\nContract\n</employment_type>", llm.ModelQuestionCalls.Single().Prompt);
        Assert.Contains("Employment type: Contract", llm.TailoredAnswerCalls.Single().Prompt);
    }

    [Fact]
    public async Task Follow_ups_and_questions_prepared_in_the_background_keep_the_kind_of_interview()
    {
        var (engine, llm, _) = Create(prefetch: true);
        await engine.StartAsync(Profile(), [], employment: EmploymentType.Contract);

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);
        await engine.NextAsync();

        Assert.True(llm.To(InterviewCoach.Core.Models.LlmRole.Coach).Count() >= 3);
        Assert.All(llm.To(InterviewCoach.Core.Models.LlmRole.Coach), c => Assert.Contains("Employment type: Contract", c.Prompt));
        Assert.All(llm.ModelQuestionCalls, c => Assert.Contains("<employment_type>\nContract\n", c.Prompt));
    }

    [Fact]
    public async Task Tailoring_a_general_answer_uses_the_kind_of_interview()
    {
        var (engine, llm, _) = Create(withBank: true);
        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept], employment: EmploymentType.Contract);

        await engine.PersonalizeAsync();

        Assert.Contains("Employment type: Contract", llm.TailoredAnswerCalls.Single().Prompt);
    }

    [Fact]
    public async Task Saved_questions_and_answers_are_general_so_they_are_written_for_neither_kind()
    {
        var (engine, llm, _) = Create(withBank: true);

        await engine.StartAsync(Profile(), [QuestionType.TechnicalConcept], employment: EmploymentType.Contract);

        var bankPrompt = llm.BankQuestionCalls.Single().Prompt;
        Assert.DoesNotContain("Contract", bankPrompt);   // the saved-question prompts have no place for the kind of job at all
        Assert.DoesNotContain("<employment_type>", bankPrompt);
        Assert.Contains("Employment type: (none)", llm.GeneralAnswerCalls.Single().Prompt);
    }

    [Fact]
    public async Task A_saved_question_made_in_one_kind_of_interview_is_reused_in_the_other()
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var repo = new InMemoryTechBankRepository();
        LearnEngine New() => new(llm, Prompts, new TechBank(repo, llm, Prompts, () => 0.0), () => 0.0);
        await New().StartAsync(Profile(), [QuestionType.TechnicalConcept], employment: EmploymentType.FullTime);
        llm.Calls.Clear();

        var contract = New();
        await contract.StartAsync(Profile(), [QuestionType.TechnicalConcept], employment: EmploymentType.Contract);

        Assert.Equal("C# concept question 1?", contract.Current!.Question);
        Assert.Empty(llm.Calls); // free: the general question and answer do not depend on the kind of job
    }

    // ---- which types the model may be offered

    [Fact]
    public async Task With_the_bank_serving_technical_questions_a_contract_session_offers_engagement_not_motivation()
    {
        var (engine, llm, _) = Create(withBank: true, random: () => 0.99); // never lands in the bank's share

        await engine.StartAsync(Profile(), [], employment: EmploymentType.Contract);

        var prompt = llm.ModelQuestionCalls.Single().Prompt;
        Assert.Contains("<allowed_question_types>\n- tell_me_about_yourself\n- resume_deep_dive\n- system_design\n- coding_talkthrough\n- behavioral\n- scenario\n- engagement\n</allowed_question_types>", prompt);
        Assert.DoesNotContain("- motivation_fit\n</allowed", prompt);
    }

    [Fact]
    public async Task With_the_bank_serving_technical_questions_a_full_time_session_offers_motivation_not_engagement()
    {
        var (engine, llm, _) = Create(withBank: true, random: () => 0.99);

        await engine.StartAsync(Profile(), [], employment: EmploymentType.FullTime);

        var prompt = llm.ModelQuestionCalls.Single().Prompt;
        Assert.Contains("- scenario\n- motivation_fit\n</allowed_question_types>", prompt);
        Assert.DoesNotContain("- engagement\n</allowed", prompt);
    }

    [Fact]
    public async Task A_specific_choice_is_passed_through_unchanged()
    {
        var (engine, llm, _) = Create();

        await engine.StartAsync(Profile(), [QuestionType.Engagement], employment: EmploymentType.Contract);

        Assert.Contains("<allowed_question_types>\n- engagement\n</allowed_question_types>", llm.ModelQuestionCalls.Single().Prompt);
    }

    [Fact]
    public async Task Technology_questions_written_by_the_model_know_the_kind_of_interview()
    {
        var (engine, llm, _) = Create(withBank: false);

        await engine.StartAsync(Profile(), [], focusTechnologies: ["Redis"], employment: EmploymentType.Contract);

        Assert.Contains("<employment_type>\nContract\n", llm.BankQuestionCalls.Single().Prompt); // the model-written one, with resume
    }
}
