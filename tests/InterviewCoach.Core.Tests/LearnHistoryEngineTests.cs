using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class LearnHistoryEngineTests
{
    private sealed class ThrowingHistory : ILearnHistory
    {
        public Task RecordAsync(LearnHistoryEntry entry, CancellationToken ct = default) => throw new IOException("disk full");
        public Task<IReadOnlyList<LearnHistoryEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LearnHistoryEntry>>([]);
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Harness
    {
        public BankScript Script { get; } = new();
        public InMemoryTechBankRepository Bank { get; } = new();
        public InMemoryLearnHistory History { get; } = new();
        public ScriptedLlmService Llm { get; }
        private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        public Harness() => Llm = new ScriptedLlmService(Script.Handle);

        public LearnEngine NewEngine(bool prefetch = false, ILearnHistory? history = null, Func<double>? random = null)
            => new(Llm, _prompts, new TechBank(Bank, Llm, _prompts, () => 0.0), random ?? (() => 0.0), history ?? History) { PrefetchNext = prefetch };
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "Orders platform", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = "We use C# and SQL Server.", ResumeText = "RESUME",
    };

    private static readonly QuestionType[] TechnicalOnly = [QuestionType.TechnicalConcept];

    [Fact]
    public async Task A_question_and_its_answer_are_kept_once_both_are_on_screen()
    {
        var h = new Harness();

        await h.NewEngine().StartAsync(Profile(), TechnicalOnly);

        var entry = Assert.Single(await h.History.ListAsync());
        Assert.Equal("C# concept question 1?", entry.Question);
        Assert.Equal("technical_concept", entry.QuestionType);
        Assert.Equal("C#", entry.Technology);
        Assert.Equal("Senior", entry.Seniority);
        Assert.True(entry.IsGeneral);
        Assert.Null(entry.ProfileName);          // a general answer belongs to no profile
        Assert.False(entry.IsFollowUp);
        Assert.Equal("GENERAL answer", entry.Coach.ModelAnswer);
    }

    [Fact]
    public async Task A_question_written_from_the_resume_is_kept_with_the_profile_it_was_for()
    {
        var h = new Harness();

        await h.NewEngine().StartAsync(Profile(), [QuestionType.Behavioral]);

        var entry = Assert.Single(await h.History.ListAsync());
        Assert.Equal("Model question 1?", entry.Question);
        Assert.Equal("behavioral", entry.QuestionType);
        Assert.False(entry.IsGeneral);
        Assert.Equal("Orders platform", entry.ProfileName);
        Assert.Equal("TAILORED answer", entry.Coach.ModelAnswer);
    }

    [Fact]
    public async Task A_question_is_not_kept_until_it_is_shown_even_if_it_was_prepared_in_the_background()
    {
        var h = new Harness();
        var engine = h.NewEngine(prefetch: true);
        await engine.StartAsync(Profile(), TechnicalOnly);

        // The next question is being prepared, but the user has not seen it.
        for (var i = 0; i < 20 && h.Llm.BankQuestionCalls.Count() < 2; i++) await Task.Delay(10);
        Assert.Single(await h.History.ListAsync());

        await engine.NextAsync();

        Assert.Equal(2, (await h.History.ListAsync()).Count);
    }

    [Fact]
    public async Task A_follow_up_is_kept_with_the_question_it_came_from_and_going_back_is_not_a_second_viewing()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);

        await engine.OpenFollowUpAsync(engine.Current!.Coach!.FollowUps[0]);
        engine.Back();

        var entries = await h.History.ListAsync();
        Assert.Equal(2, entries.Count);
        var followUp = entries.Single(e => e.IsFollowUp);
        Assert.Equal("Why that?", followUp.Question);
        Assert.Equal("C# concept question 1?", followUp.ParentQuestion);
        Assert.Equal("technical_concept", followUp.QuestionType);
        Assert.Equal(1, entries.Single(e => !e.IsFollowUp).TimesSeen);
    }

    [Fact]
    public async Task Tailoring_a_general_answer_keeps_the_tailored_one_as_a_second_entry()
    {
        var h = new Harness();
        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);

        await engine.PersonalizeAsync();

        var entries = await h.History.ListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal("GENERAL answer", entries.Single(e => e.IsGeneral).Coach.ModelAnswer);
        var tailored = entries.Single(e => !e.IsGeneral);
        Assert.Equal("TAILORED answer", tailored.Coach.ModelAnswer);
        Assert.Equal("Orders platform", tailored.ProfileName);
    }

    [Fact]
    public async Task Seeing_a_saved_question_again_in_a_new_session_counts_another_viewing()
    {
        var h = new Harness();
        await h.NewEngine().StartAsync(Profile(), TechnicalOnly);

        await h.NewEngine().StartAsync(Profile(), TechnicalOnly); // reuses the saved question and answer

        var entry = Assert.Single(await h.History.ListAsync());
        Assert.Equal(2, entry.TimesSeen);
    }

    [Fact]
    public async Task A_failed_answer_keeps_nothing()
    {
        var h = new Harness { Script = { FailGeneralAnswers = true } };

        var engine = h.NewEngine();
        await engine.StartAsync(Profile(), TechnicalOnly);

        Assert.Equal(LearnPhase.Failed, engine.Phase);
        Assert.Empty(await h.History.ListAsync());
    }

    [Fact]
    public async Task A_library_that_cannot_save_never_interrupts_a_session()
    {
        var h = new Harness();
        var engine = h.NewEngine(history: new ThrowingHistory());

        await engine.StartAsync(Profile(), TechnicalOnly);
        await engine.NextAsync();

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Null(engine.Error);
        Assert.EndsWith("concept question 2?", engine.Current!.Question); // the technology alternates, so only the number is checked
    }

    [Fact]
    public async Task Without_a_library_everything_works_as_before()
    {
        var h = new Harness();
        var engine = new LearnEngine(h.Llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")));

        await engine.StartAsync(Profile(), [QuestionType.Behavioral]);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
    }
}
