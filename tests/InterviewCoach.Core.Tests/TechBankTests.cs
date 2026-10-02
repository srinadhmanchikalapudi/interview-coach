using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class TechBankTests
{
    private static (TechBank Bank, ScriptedLlmService Llm, InMemoryTechBankRepository Repo, BankScript Script) Create(BankScript? script = null)
    {
        script ??= new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var repo = new InMemoryTechBankRepository();
        var bank = new TechBank(repo, llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), random: () => 0.0);
        return (bank, llm, repo, script);
    }

    private static CandidateProfile Profile(string jd = "We build services in C# and .NET with SQL Server.") => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = jd, ResumeText = "RESUME-MARKER",
    };

    // ---- technologies from the job description

    [Fact]
    public async Task Technologies_are_asked_of_the_model_from_the_job_description_only()
    {
        var (bank, llm, _, _) = Create();

        var technologies = await bank.GetTechnologiesAsync(Profile("JD-MARKER we use C#"));

        Assert.Equal(["C#", "SQL Server"], technologies);
        var call = Assert.Single(llm.TagCalls);
        Assert.Equal(LlmRole.QuestionGenerator, call.Role);
        Assert.Equal("List the technologies.", call.Messages.Single().Content);
        Assert.Contains("JD-MARKER", call.Prompt);
        Assert.DoesNotContain("RESUME-MARKER", call.Prompt); // the tags prompt never sees the resume
    }

    [Fact]
    public async Task The_same_job_description_is_never_asked_about_twice()
    {
        var (bank, llm, _, _) = Create();

        await bank.GetTechnologiesAsync(Profile());
        await bank.GetTechnologiesAsync(Profile());

        Assert.Single(llm.TagCalls);
    }

    [Fact]
    public async Task Editing_the_resume_or_only_the_whitespace_of_the_job_description_costs_nothing()
    {
        var (bank, llm, _, _) = Create();
        await bank.GetTechnologiesAsync(Profile("Line one\nLine two"));

        var editedResume = Profile("Line one\nLine two");
        editedResume.ResumeText = "a completely different resume";
        await bank.GetTechnologiesAsync(editedResume);
        await bank.GetTechnologiesAsync(Profile("Line one\r\n\r\n  Line two   \r\n"));

        Assert.Single(llm.TagCalls);
    }

    [Fact]
    public async Task Changing_the_job_description_extracts_again()
    {
        var (bank, llm, _, _) = Create();

        await bank.GetTechnologiesAsync(Profile("We use C#"));
        await bank.GetTechnologiesAsync(Profile("We use Python"));

        Assert.Equal(2, llm.TagCalls.Count());
    }

    [Fact]
    public async Task An_empty_job_description_needs_no_model_call()
    {
        var (bank, llm, _, _) = Create();

        Assert.Empty(await bank.GetTechnologiesAsync(Profile("   ")));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task Technologies_are_trimmed_deduplicated_and_capped()
    {
        var script = new BankScript { Technologies = ["  C# ", "c#", "", ".NET", "A", "B", "C", "D", "E", "F", "G"] };
        var (bank, _, _, _) = Create(script);

        var technologies = await bank.GetTechnologiesAsync(Profile());

        Assert.Equal(TechBank.MaxTechnologies, technologies.Count);
        Assert.Equal(["C#", ".NET", "A", "B", "C", "D", "E", "F"], technologies);
    }

    [Fact]
    public async Task A_job_description_with_no_technologies_is_remembered_too()
    {
        var (bank, llm, _, _) = Create(new BankScript { Technologies = [] });

        Assert.Empty(await bank.GetTechnologiesAsync(Profile("Be nice")));
        Assert.Empty(await bank.GetTechnologiesAsync(Profile("Be nice")));

        Assert.Single(llm.TagCalls);
    }

    // ---- questions

    [Fact]
    public async Task A_new_question_is_written_without_the_resume_or_job_description_and_saved()
    {
        var (bank, llm, repo, _) = Create();

        var question = await bank.NextQuestionAsync("SQL Server", Seniority.Senior, []);

        Assert.Equal("SQL Server concept question 1?", question.Question);
        Assert.NotEqual(0, question.Id);
        var call = Assert.Single(llm.BankQuestionCalls);
        Assert.Contains("<focus_technology>\nSQL Server\n</focus_technology>", call.Prompt);
        Assert.DoesNotContain("<job_description>", call.Prompt);   // the batch prompt never has a place for personal details
        Assert.DoesNotContain("<candidate_resume>", call.Prompt);
        Assert.Contains("software engineer", call.Prompt);
        Assert.Single(await repo.ListQuestionsAsync("sql server", Seniority.Senior)); // saved, found ignoring case
    }

    [Fact]
    public async Task Saved_questions_are_reused_without_a_model_call()
    {
        var (bank, llm, _, _) = Create();
        var first = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        llm.Calls.Clear();

        var again = await bank.NextQuestionAsync("C#", Seniority.Senior, []); // a new session: nothing asked yet

        Assert.Equal(first.Id, again.Id);
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task A_new_question_is_written_only_when_every_saved_one_has_been_seen_this_session()
    {
        var (bank, llm, _, _) = Create();
        var first = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        llm.Calls.Clear();

        var second = await bank.NextQuestionAsync("C#", Seniority.Senior, [first.Question]);

        Assert.NotEqual(first.Id, second.Id);
        var call = Assert.Single(llm.BankQuestionCalls);
        Assert.Contains("1. " + first.Question, call.Prompt); // told what to avoid
    }

    [Fact]
    public async Task Questions_are_kept_apart_by_technology_and_by_seniority()
    {
        var (bank, llm, _, _) = Create();
        var senior = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        var junior = await bank.NextQuestionAsync("C#", Seniority.Junior, []);
        var other = await bank.NextQuestionAsync("SQL Server", Seniority.Senior, []);

        Assert.Equal(3, new[] { senior.Id, junior.Id, other.Id }.Distinct().Count());
        Assert.Equal(3, llm.BankQuestionCalls.Count());
        Assert.Contains("Difficulty must match Junior", llm.BankQuestionCalls.Skip(1).First().Prompt); // the level is part of the key
    }

    [Fact]
    public async Task A_model_that_only_repeats_a_question_is_tried_as_a_batch_then_twice_singly_and_never_stored_twice()
    {
        var repeats = 0;
        var llm = new ScriptedLlmService(call =>
        {
            repeats++;
            return Task.FromResult<object>(call.Prompt.Contains(BankScript.BatchMarker)
                ? new QuestionBatchDto { Questions = [new BatchQuestionDto { Question = "What is a closure?", Area = "closures" }] }
                : new QuestionDto { Question = "What is a closure?", QuestionType = "technical_concept", Source = "fundamentals", Focus = "closures" });
        });
        var repo = new InMemoryTechBankRepository();
        var bank = new TechBank(repo, llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => 0.0);
        var first = await bank.NextQuestionAsync("JavaScript", Seniority.Mid, []);
        repeats = 0;

        var second = await bank.NextQuestionAsync("JavaScript", Seniority.Mid, [first.Question]);

        Assert.Equal(3, repeats);                       // one batch with nothing new, then two single tries, then gave up
        Assert.Equal(first.Id, second.Id);              // the repeat collapses onto the stored question
        Assert.Single(await repo.ListQuestionsAsync("JavaScript", Seniority.Mid));
    }

    [Fact]
    public async Task An_empty_question_from_the_model_is_an_error()
    {
        var llm = new ScriptedLlmService(call => Task.FromResult<object>(
            call.Prompt.Contains(BankScript.BatchMarker) ? new QuestionBatchDto() : new QuestionDto()));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")));

        await Assert.ThrowsAsync<LlmException>(() => bank.NextQuestionAsync("Go", Seniority.Mid, []));
    }

    // ---- batches of common questions

    [Fact]
    public async Task When_the_saved_questions_run_out_a_batch_is_written_in_one_call_and_all_of_it_is_saved()
    {
        var (bank, llm, repo, script) = Create();
        script.BatchCount = 4;

        var first = await bank.NextQuestionAsync("C#", Seniority.Senior, []);

        Assert.Equal("C# concept question 1?", first.Question);       // the one asked for first leads the batch
        Assert.Single(llm.BatchCalls);
        Assert.Empty(llm.SingleQuestionCalls);
        Assert.Equal(4, (await repo.ListQuestionsAsync("C#", Seniority.Senior)).Count);
    }

    [Fact]
    public async Task The_rest_of_a_batch_is_served_from_the_bank_without_another_call()
    {
        var (bank, llm, _, script) = Create();
        script.BatchCount = 3;
        var asked = new List<string>();
        for (var i = 0; i < 3; i++)
            asked.Add((await bank.NextQuestionAsync("C#", Seniority.Senior, asked)).Question);

        Assert.Equal(3, asked.Distinct().Count());
        Assert.Single(llm.BankQuestionCalls); // three questions, one model call
    }

    [Fact]
    public async Task A_batch_asks_for_ten_and_keeps_at_most_ten()
    {
        var (bank, llm, repo, script) = Create();
        script.BatchCount = 14;

        await bank.NextQuestionAsync("C#", Seniority.Senior, []);

        Assert.Equal(TechBank.BatchSize, (await repo.ListQuestionsAsync("C#", Seniority.Senior)).Count);
        Assert.Contains("Write 10 new technical questions", llm.BatchCalls.Single().Prompt);
    }

    [Fact]
    public async Task The_batch_prompt_lists_what_is_saved_and_asked_once_each_and_names_the_technology_and_level()
    {
        var (bank, llm, repo, _) = Create();
        await repo.AddQuestionAsync("C#", Seniority.Senior, "What does the async keyword do?", "f");
        await repo.AddQuestionAsync("C#", Seniority.Senior, "What is a struct?", "f");

        await bank.NextQuestionAsync("C#", Seniority.Senior, ["What does the async keyword do?", "What is a struct?", "Something asked elsewhere?"]);

        var prompt = llm.BatchCalls.Single().Prompt;
        Assert.Contains("<focus_technology>\nC#\n</focus_technology>", prompt);
        Assert.Contains("for a Senior candidate", prompt);
        Assert.Equal(1, Count(prompt, "What does the async keyword do?"));
        Assert.Equal(1, Count(prompt, "What is a struct?"));
        Assert.Contains("Something asked elsewhere?", prompt);
    }

    [Fact]
    public async Task Questions_a_batch_repeats_from_the_bank_are_dropped()
    {
        var (bank, _, repo, script) = Create();
        await repo.AddQuestionAsync("C#", Seniority.Senior, "C# concept question 1?", "f"); // the script's first batch question
        script.BatchCount = 2;

        var next = await bank.NextQuestionAsync("C#", Seniority.Senior, ["C# concept question 1?"]);

        Assert.Equal("C# concept question 2?", next.Question);
        Assert.Equal(2, (await repo.ListQuestionsAsync("C#", Seniority.Senior)).Count); // the repeat was not stored again
    }

    [Fact]
    public async Task If_the_batch_fails_a_single_question_is_written_instead()
    {
        var (bank, llm, _, script) = Create();
        script.FailBatches = true;

        var question = await bank.NextQuestionAsync("C#", Seniority.Senior, []);

        Assert.Equal("C# concept question 1?", question.Question);
        Assert.Single(llm.BatchCalls);
        Assert.Single(llm.SingleQuestionCalls);
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---- answers

    [Fact]
    public async Task A_general_answer_is_written_without_any_personal_details()
    {
        var (bank, llm, _, _) = Create();

        var answer = await bank.WriteGeneralAnswerAsync("What is a closure?", Seniority.Senior, answerWords: 120, transcript: null);

        Assert.Equal("GENERAL answer", answer.ModelAnswer);
        var prompt = Assert.Single(llm.GeneralAnswerCalls).Prompt;
        Assert.Contains("<job_description>\n(none)\n</job_description>", prompt);
        Assert.Contains(TechBank.GeneralAnswerNote, prompt);
        Assert.Contains("software engineer", prompt);
        Assert.Contains("Mode: learn", prompt);
        Assert.Contains("Seniority: Senior", prompt);
        Assert.Contains("Requested model answer length: about 120 words", prompt);
        Assert.Contains("<question>\nWhat is a closure?\n</question>", prompt);
    }

    [Fact]
    public async Task A_general_answer_with_no_requested_length_is_given_the_concept_range_and_the_question_type()
    {
        var (bank, llm, _, _) = Create();

        await bank.WriteGeneralAnswerAsync("What is a closure?", Seniority.Senior, answerWords: null, transcript: null);

        var prompt = Assert.Single(llm.GeneralAnswerCalls).Prompt;
        Assert.Contains("Question type: technical_concept", prompt);
        Assert.Contains("Requested model answer length: between 60 and 150 words (roughly 30 sec to 1 min 10 sec spoken)", prompt);
    }

    [Fact]
    public async Task A_word_count_replaces_the_range_for_general_answers()
    {
        var (bank, llm, _, _) = Create();

        await bank.WriteGeneralAnswerAsync("What is a closure?", Seniority.Senior, answerWords: 90, transcript: null);

        var prompt = Assert.Single(llm.GeneralAnswerCalls).Prompt;
        Assert.Contains("Requested model answer length: about 90 words", prompt);
        Assert.DoesNotContain("Requested model answer length: between", prompt); // the rule text itself quotes a range as an example
    }

    [Fact]
    public async Task Answers_saved_before_the_Coach_was_told_the_range_are_not_reused()
    {
        // They were written with no length guidance and ran 250+ words for concept questions. They were saved under key 0.
        var (bank, _, repo, _) = Create();
        var question = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        await repo.SaveAnswerAsync(question.Id, 0, new CoachOutput { ModelAnswer = "an old, far too long answer" });

        Assert.Null(await bank.GetSavedAnswerAsync(question.Id, null));

        await bank.SaveAnswerAsync(question.Id, null, new CoachOutput { ModelAnswer = "a new, right-sized answer" });
        Assert.Equal("a new, right-sized answer", (await bank.GetSavedAnswerAsync(question.Id, null))!.ModelAnswer);
        Assert.Equal("an old, far too long answer", (await repo.GetAnswerAsync(question.Id, 0))!.ModelAnswer); // left alone, just unused
    }

    [Fact]
    public async Task A_general_answer_has_any_invented_feedback_removed()
    {
        var llm = new ScriptedLlmService(_ => Task.FromResult<object>(new CoachOutput
        {
            ModelAnswer = "answer", Delivery = "too long", Feedback = [new FeedbackPoint { Kind = "fix", Point = "invented" }],
        }));
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")));

        var answer = await bank.WriteGeneralAnswerAsync("Q?", Seniority.Mid, null, null);

        Assert.Empty(answer.Feedback);
        Assert.Null(answer.Delivery);
    }

    [Fact]
    public async Task Saved_answers_are_found_only_for_the_same_requested_length()
    {
        var (bank, _, _, _) = Create();
        var question = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        var answer = new CoachOutput { ModelAnswer = "saved for 120" };

        await bank.SaveAnswerAsync(question.Id, 120, answer);

        Assert.Equal("saved for 120", (await bank.GetSavedAnswerAsync(question.Id, 120))!.ModelAnswer);
        Assert.Null(await bank.GetSavedAnswerAsync(question.Id, null));   // interviewer norm is a different request
        Assert.Null(await bank.GetSavedAnswerAsync(question.Id, 200));
    }

    [Fact]
    public async Task Interviewer_norm_answers_are_saved_under_their_own_key()
    {
        var (bank, _, _, _) = Create();
        var question = await bank.NextQuestionAsync("C#", Seniority.Senior, []);

        await bank.SaveAnswerAsync(question.Id, null, new CoachOutput { ModelAnswer = "norm" });

        Assert.Equal("norm", (await bank.GetSavedAnswerAsync(question.Id, null))!.ModelAnswer);
        Assert.Null(await bank.GetSavedAnswerAsync(question.Id, 120));
    }

    [Fact]
    public async Task Clearing_removes_questions_and_answers_but_keeps_the_technologies_found()
    {
        var (bank, llm, _, _) = Create();
        await bank.GetTechnologiesAsync(Profile());
        var question = await bank.NextQuestionAsync("C#", Seniority.Senior, []);
        await bank.SaveAnswerAsync(question.Id, null, new CoachOutput { ModelAnswer = "x" });
        Assert.Equal(new TechBankStats(1, 1), await bank.GetStatsAsync());

        await bank.ClearAsync();

        Assert.Equal(new TechBankStats(0, 0), await bank.GetStatsAsync());
        llm.Calls.Clear();
        await bank.GetTechnologiesAsync(Profile());
        Assert.Empty(llm.Calls); // still remembered
    }
}
