using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class OpeningFillerTests
{
    // The exact openings seen in the debug log of 2 October 2026, from Claude Sonnet and from GPT-5 Mini.
    [Theory]
    [InlineData("Sure. Short version: static abstract members let you declare static methods on an interface.",
                "Static abstract members let you declare static methods on an interface.")]
    [InlineData("Yeah, short version: C# 11 added static abstract members on interfaces.",
                "C# 11 added static abstract members on interfaces.")]
    [InlineData("Short answer: IAsyncEnumerable<T> streams items as they become available.",
                "IAsyncEnumerable<T> streams items as they become available.")]
    [InlineData("Short version: a struct is a value type, a class is a reference type.",
                "A struct is a value type, a class is a reference type.")]
    [InlineData("Yeah, so async doesn't make anything run on another thread.",
                "Async doesn't make anything run on another thread.")]
    [InlineData("Short version is that the LOH is not compacted by default, so gaps build up.",
                "The LOH is not compacted by default, so gaps build up.")]
    [InlineData("Sure. Short version: I default to an interface when I'm defining a capability.",
                "I default to an interface when I'm defining a capability.")]
    public void Warm_up_openings_seen_in_real_answers_are_removed(string answer, string expected)
    {
        Assert.Equal(expected, CoachText.WithoutOpeningFiller(answer));
    }

    [Theory]
    [InlineData("Okay, so a struct lives on the stack when it is a local variable.", "A struct lives on the stack when it is a local variable.")]
    [InlineData("Well, the garbage collector decides what to free by reachability.", "The garbage collector decides what to free by reachability.")]
    [InlineData("Honestly, I would reach for an interface first in most cases.", "I would reach for an interface first in most cases.")]
    [InlineData("Great question. The difference is where the data lives.", "The difference is where the data lives.")]
    [InlineData("Sure. Yeah, so the difference is where the data lives.", "The difference is where the data lives.")]
    public void Other_common_fillers_are_removed_too(string answer, string expected)
    {
        Assert.Equal(expected, CoachText.WithoutOpeningFiller(answer));
    }

    [Theory]
    [InlineData("A struct is a value type, so it is copied on assignment.")]
    [InlineData("Right now the GC does not compact the large object heap by default.")]
    [InlineData("Well-known patterns like the factory use this feature.")]
    [InlineData("So far the only way to do that is through a generic constraint.")]
    [InlineData("Yes, you can call it, but only through a generic type parameter.")]
    [InlineData("No, not by itself. An async method starts running synchronously.")]
    [InlineData("It depends on whether the type is a record, but generally yes.")]
    [InlineData("The short answer to that is rarely, and only with a measured reason.")]
    [InlineData("[Company] used this to cut allocations in the hot path.")]
    public void Real_openings_and_words_that_only_look_like_fillers_are_left_alone(string answer)
    {
        Assert.Equal(answer, CoachText.WithoutOpeningFiller(answer));
    }

    [Fact]
    public void Only_the_start_is_touched_never_the_middle()
    {
        const string answer = "A struct is a value type. Sure, you can box it. Short version: avoid boxing in hot paths.";

        Assert.Equal(answer, CoachText.WithoutOpeningFiller(answer));
    }

    [Theory]
    [InlineData("Sure.")]
    [InlineData("Yeah, short version: ok.")]
    [InlineData("")]
    [InlineData("   ")]
    public void An_answer_that_would_be_left_almost_empty_is_returned_as_it_was(string answer)
    {
        Assert.Equal(answer, CoachText.WithoutOpeningFiller(answer));
    }

    [Fact]
    public void A_missing_answer_becomes_empty_text()
    {
        Assert.Equal("", CoachText.WithoutOpeningFiller(null));
    }

    [Fact]
    public void Placeholders_in_the_rest_of_the_answer_are_kept()
    {
        var cleaned = CoachText.WithoutOpeningFiller("Sure. Short version: at [Company] we moved to [the new approach] and cut allocations.");

        Assert.Equal("At [Company] we moved to [the new approach] and cut allocations.", cleaned);
    }

    // ---- Where it is applied

    private static CoachOutput Reply(string answer) => new()
    {
        WhatTheyreTesting = "testing",
        ModelAnswer = answer,
        Shape = "shape",
        Feedback = [new FeedbackPoint { Kind = "fix", Point = "invented" }],
        Delivery = "invented",
        FollowUps = [new FollowUp { Question = "Why?", Hint = "Because." }],
    };

    [Fact]
    public void For_learning_drops_invented_feedback_and_the_warm_up_and_keeps_everything_else()
    {
        var output = Reply("Sure. Short version: a struct is a value type, a class is a reference type.").ForLearning();

        Assert.Equal("A struct is a value type, a class is a reference type.", output.ModelAnswer);
        Assert.Empty(output.Feedback);
        Assert.Null(output.Delivery);
        Assert.Equal("testing", output.WhatTheyreTesting);
        Assert.Equal("shape", output.Shape);
        Assert.Equal("Why?", Assert.Single(output.FollowUps).Question);
    }

    [Fact]
    public async Task A_general_answer_written_by_the_model_is_cleaned_before_it_is_used_and_saved()
    {
        var llm = new ScriptedLlmService(_ => Task.FromResult<object>(Reply("Yeah, short version: C# 11 added static abstract members on interfaces.")));
        var repo = new InMemoryTechBankRepository();
        var bank = new TechBank(repo, llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => 0.0);

        var answer = await bank.WriteGeneralAnswerAsync("What are static abstract members?", Seniority.Senior, answerWords: null, transcript: null);

        Assert.Equal("C# 11 added static abstract members on interfaces.", answer.ModelAnswer);
    }

    [Fact]
    public async Task An_answer_saved_before_the_change_is_cleaned_when_it_is_shown()
    {
        var script = new BankScript();
        var repo = new InMemoryTechBankRepository();
        var llm = new ScriptedLlmService(script.Handle);
        var prompts = new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir"));
        var saved = await repo.AddQuestionAsync("C#", Seniority.Senior, "Saved question?", "f");
        await repo.SaveAnswerAsync(saved.Id, TechBank.InterviewerNormKey, Reply("Sure. Short version: a struct is a value type, a class is a reference type."));
        var engine = new LearnEngine(llm, prompts, new TechBank(repo, llm, prompts, () => 0.0), () => 0.0) { PrefetchNext = false };

        await engine.StartAsync(new CandidateProfile
        {
            Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = "We use C#.", ResumeText = "r",
        }, [QuestionType.TechnicalConcept]);

        Assert.Equal("Saved question?", engine.Current!.Question);
        Assert.Equal("A struct is a value type, a class is a reference type.", engine.Current.Coach!.ModelAnswer);
        Assert.Empty(llm.GeneralAnswerCalls); // cleaned on the way out, not rewritten by the model
    }

    [Fact]
    public async Task A_question_that_is_both_saved_and_asked_appears_once_in_the_list_the_model_is_told_to_avoid()
    {
        var script = new BankScript();
        var repo = new InMemoryTechBankRepository();
        var llm = new ScriptedLlmService(script.Handle);
        var bank = new TechBank(repo, llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => 0.0);
        await repo.AddQuestionAsync("C#", Seniority.Senior, "What's the difference between a struct and a class?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "What does the async keyword do?", "f");

        // Both saved questions were asked in this session, so the bank has to write a new one.
        await bank.NextQuestionAsync("C#", Seniority.Senior,
            ["What's the difference between a struct and a class?", "What does the async keyword do?"]);

        var prompt = Assert.Single(llm.BankQuestionCalls).Prompt;
        Assert.Equal(1, Count(prompt, "What's the difference between a struct and a class?"));
        Assert.Equal(1, Count(prompt, "What does the async keyword do?"));
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
