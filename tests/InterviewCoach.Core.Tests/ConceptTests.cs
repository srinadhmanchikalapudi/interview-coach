using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

/// <summary>Technology concepts: the technologies of a job role are fetched once and saved, and a session needs no resume or job description.</summary>
public class ConceptTests
{
    private static (TechBank Bank, ScriptedLlmService Llm, InMemoryTechBankRepository Repo, BankScript Script) Create()
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var repo = new InMemoryTechBankRepository();
        var bank = new TechBank(repo, llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), random: () => 0.0);
        return (bank, llm, repo, script);
    }

    // ---- technologies of a role

    [Fact]
    public async Task The_model_is_asked_for_the_technologies_of_the_role_and_sees_only_the_role()
    {
        var (bank, llm, _, _) = Create();

        var technologies = await bank.GetRoleTechnologiesAsync("Data engineer");

        Assert.Equal(["Python", "SQL", "Spark"], technologies);
        var call = Assert.Single(llm.RoleTagCalls);
        Assert.Equal(LlmRole.QuestionGenerator, call.Role);
        Assert.Equal("List the technologies.", call.Messages.Single().Content);
        Assert.Contains("Data engineer", call.Prompt);
        Assert.Contains($"Give at most {TechBank.MaxRoleTechnologies}", call.Prompt);
    }

    [Fact]
    public async Task A_role_is_fetched_once_however_it_is_typed()
    {
        var (bank, llm, _, _) = Create();

        await bank.GetRoleTechnologiesAsync("Data Engineer");
        await bank.GetRoleTechnologiesAsync("data engineer");
        await bank.GetRoleTechnologiesAsync("  Data    ENGINEER ");
        var again = await bank.GetRoleTechnologiesAsync("Data engineer");

        Assert.Single(llm.RoleTagCalls);
        Assert.Equal(["Python", "SQL", "Spark"], again);
    }

    [Fact]
    public async Task A_different_role_is_fetched_separately()
    {
        var (bank, llm, _, script) = Create();
        await bank.GetRoleTechnologiesAsync("Data engineer");
        script.RoleTechnologies = ["Kotlin", "Android SDK"];

        var android = await bank.GetRoleTechnologiesAsync("Android developer");

        Assert.Equal(["Kotlin", "Android SDK"], android);
        Assert.Equal(2, llm.RoleTagCalls.Count());
    }

    [Fact]
    public async Task The_saved_list_can_be_read_without_calling_the_model()
    {
        var (bank, llm, _, _) = Create();
        Assert.Null(await bank.GetSavedRoleTechnologiesAsync("Data engineer")); // never fetched
        await bank.GetRoleTechnologiesAsync("Data engineer");

        var saved = await bank.GetSavedRoleTechnologiesAsync("DATA ENGINEER");

        Assert.Equal(["Python", "SQL", "Spark"], saved);
        Assert.Single(llm.RoleTagCalls);
        Assert.Null(await bank.GetSavedRoleTechnologiesAsync("   "));
    }

    [Fact]
    public async Task Asking_again_replaces_the_saved_list()
    {
        var (bank, llm, _, script) = Create();
        await bank.GetRoleTechnologiesAsync("Data engineer");
        script.RoleTechnologies = ["Python", "dbt", "Airflow"];

        var refreshed = await bank.GetRoleTechnologiesAsync("Data engineer", refresh: true);

        Assert.Equal(["Python", "dbt", "Airflow"], refreshed);
        Assert.Equal(2, llm.RoleTagCalls.Count());
        Assert.Equal(["Python", "dbt", "Airflow"], await bank.GetRoleTechnologiesAsync("data engineer")); // the new list is the saved one
        Assert.Equal(2, llm.RoleTagCalls.Count());
    }

    [Fact]
    public async Task A_blank_role_asks_nothing()
    {
        var (bank, llm, _, _) = Create();

        Assert.Empty(await bank.GetRoleTechnologiesAsync("   "));
        Assert.Empty(await bank.GetRoleTechnologiesAsync(""));

        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task The_list_is_tidied_and_capped()
    {
        var (bank, _, _, script) = Create();
        script.RoleTechnologies = ["  C# ", "c#", "", "  ", .. Enumerable.Range(1, 40).Select(i => $"Tech{i}")];

        var technologies = await bank.GetRoleTechnologiesAsync("Backend developer");

        Assert.Equal("C#", technologies[0]);
        Assert.Equal(1, technologies.Count(t => t.Equals("c#", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(TechBank.MaxRoleTechnologies, technologies.Count);
    }

    [Fact]
    public async Task An_empty_reply_is_not_saved_so_the_next_visit_asks_again()
    {
        var (bank, llm, _, script) = Create();
        script.RoleTechnologies = [];

        Assert.Empty(await bank.GetRoleTechnologiesAsync("Vague role"));
        script.RoleTechnologies = ["Excel"];
        var second = await bank.GetRoleTechnologiesAsync("Vague role");

        Assert.Equal(["Excel"], second);
        Assert.Equal(2, llm.RoleTagCalls.Count());
    }

    [Fact]
    public async Task A_failed_request_saves_nothing_and_says_so()
    {
        var (bank, llm, _, script) = Create();
        script.FailRoleTags = true;

        await Assert.ThrowsAsync<LlmException>(() => bank.GetRoleTechnologiesAsync("Data engineer"));

        Assert.Null(await bank.GetSavedRoleTechnologiesAsync("Data engineer"));
        script.FailRoleTags = false;
        Assert.Equal(3, (await bank.GetRoleTechnologiesAsync("Data engineer")).Count);
        Assert.Equal(2, llm.RoleTagCalls.Count());
    }

    // ---- difficulty

    [Theory]
    [InlineData(Difficulty.Beginner, Seniority.Junior)]
    [InlineData(Difficulty.Medium, Seniority.Mid)]
    [InlineData(Difficulty.Advanced, Seniority.Senior)]
    public void Each_difficulty_is_a_level_the_question_bank_already_has(Difficulty difficulty, Seniority expected)
    {
        Assert.Equal(expected, difficulty.ToSeniority());
        Assert.False(string.IsNullOrWhiteSpace(difficulty.Label()));
        Assert.False(string.IsNullOrWhiteSpace(difficulty.Description()));
    }

    // ---- a session without a resume

    [Fact]
    public void A_concept_session_profile_has_the_role_the_level_and_notes_instead_of_documents()
    {
        var profile = ConceptSession.Profile("  Data engineer ", Difficulty.Advanced);

        Assert.Equal("Data engineer", profile.JobRole);
        Assert.Equal("Concepts: Data engineer", profile.Name);
        Assert.Equal(Seniority.Senior, profile.Seniority);
        Assert.Contains("technology concepts session", profile.JobDescription);
        Assert.True(ConceptSession.IsConceptProfile(profile));
        Assert.False(ConceptSession.IsConceptProfile(new CandidateProfile { ResumeText = "My resume" }));
        Assert.Equal(ConceptSession.DefaultRole, ConceptSession.Profile(" ", Difficulty.Medium).JobRole);
    }

    private static async Task<(LearnEngine Engine, ScriptedLlmService Llm, InMemoryTechBankRepository Repo)> Started(Difficulty difficulty, params string[] technologies)
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var repo = new InMemoryTechBankRepository();
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var engine = new LearnEngine(llm, prompts, new TechBank(repo, llm, prompts, () => 0.0), () => 0.0);
        await engine.StartAsync(ConceptSession.Profile("Data engineer", difficulty), [QuestionType.TechnicalConcept], null, technologies, EmploymentType.FullTime);
        return (engine, llm, repo);
    }

    [Fact]
    public async Task A_concept_session_asks_only_about_the_chosen_technologies_with_no_resume_in_any_prompt()
    {
        var (engine, llm, _) = await Started(Difficulty.Medium, "Spark", "SQL");

        Assert.Contains(engine.Current!.Technology, new[] { "Spark", "SQL" });
        Assert.Equal("technical_concept", engine.Current.QuestionType);
        Assert.Empty(llm.ModelQuestionCalls);       // nothing written from a resume
        Assert.Empty(llm.TagCalls);                 // no job description to read
        Assert.Empty(llm.RoleTagCalls);             // the list was chosen already
        Assert.True(engine.Current.IsGeneric);       // a general answer, saved and reused, with nothing to tailor it to
    }

    [Theory]
    [InlineData(Difficulty.Beginner, Seniority.Junior)]
    [InlineData(Difficulty.Medium, Seniority.Mid)]
    [InlineData(Difficulty.Advanced, Seniority.Senior)]
    public async Task Questions_are_saved_and_found_at_the_level_of_the_difficulty(Difficulty difficulty, Seniority level)
    {
        var (engine, _, repo) = await Started(difficulty, "Spark");

        var saved = await repo.ListQuestionsAsync("Spark", level);
        Assert.Contains(saved, q => q.Question == engine.Current!.Question);
        foreach (var other in Enum.GetValues<Seniority>().Where(s => s != level))
            Assert.Empty(await repo.ListQuestionsAsync("Spark", other));
    }

    [Fact]
    public async Task The_question_prompt_carries_the_level_so_the_difficulty_matches()
    {
        var (_, llm, _) = await Started(Difficulty.Advanced, "Spark");

        Assert.Contains("Senior", llm.BankQuestionCalls.First().Prompt);
    }

    [Fact]
    public async Task A_later_session_at_the_same_difficulty_reuses_the_saved_questions_without_the_model()
    {
        var (_, first, repo) = await Started(Difficulty.Beginner, "Spark");
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var engine = new LearnEngine(llm, prompts, new TechBank(repo, llm, prompts, () => 0.0), () => 0.0);

        await engine.StartAsync(ConceptSession.Profile("Data engineer", Difficulty.Beginner), [QuestionType.TechnicalConcept], null, ["Spark"], EmploymentType.FullTime);

        Assert.Empty(llm.BankQuestionCalls);        // the question came from the saved ones
        Assert.NotEmpty(first.BankQuestionCalls);
    }
}
