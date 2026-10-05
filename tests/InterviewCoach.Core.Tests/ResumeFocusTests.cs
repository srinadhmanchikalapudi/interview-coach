using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

public class ResumeFocusTests
{
    // ---- recognising the same question in other words

    [Fact]
    public void The_two_RabbitMQ_questions_from_the_real_log_count_as_the_same_question()
    {
        const string first = "You've built messaging pipelines with both Kafka and RabbitMQ at Acme—what made you choose one over the other for specific workloads?";
        const string second = "At Acme, you built event-driven messaging with Kafka, RabbitMQ, and NATS—how would you choose between them for a new workload in our architecture?";

        Assert.True(TextTools.IsNearDuplicate(first, second));
        Assert.True(TextTools.IsNearDuplicate(second, first));
    }

    [Fact]
    public void Questions_that_only_share_a_topic_or_a_template_are_not_duplicates()
    {
        // Two real questions from the same session about authentication middleware, but asking different things.
        Assert.False(TextTools.IsNearDuplicate(
            "At Acme, you implemented custom ASP.NET Core middleware for request validation and logging—what specific risks were you protecting against?",
            "At Acme, you handled per-store data isolation with token-based access control—how would you prevent a customer from reading another store's orders if the validation middleware failed?"));
        Assert.False(TextTools.IsNearDuplicate("How does garbage collection work in .NET?", "How does garbage collection work in Go?"));
        Assert.False(TextTools.IsNearDuplicate("Redis concept question 1?", "Redis concept question 2?"));
        Assert.False(TextTools.IsNearDuplicate("What is a struct?", "What is a class?"));
        Assert.False(TextTools.IsNearDuplicate("Why did you choose polling over push?", "How did you handle React versioning?"));
    }

    [Fact]
    public void The_same_question_with_different_punctuation_or_case_is_a_duplicate()
    {
        Assert.True(TextTools.IsNearDuplicate("Why Redis?", "why redis"));
        Assert.False(TextTools.IsNearDuplicate("", "Why Redis?"));
    }

    // ---- spreading questions over the resume

    private static readonly ResumeTopic[] Topics =
    [
        new("Acme", "orders platform", "built the product search service"),
        new("Acme", "orders platform", "added Redis caching"),
        new("Acme", "orders platform", "wrote auth middleware"),
        new("Acme", "orders platform", "ran per-store queues"),
        new("Globex", "monolith migration", "applied feature flags during cutover"),
        new("Globex", "monolith migration", "moved legacy services to .NET 8"),
        new("Initech", "", "maintained the billing module"),
    ];

    private static List<ResumeFocus> Take(ResumeFocusPicker picker, int count)
    {
        var list = new List<ResumeFocus>();
        for (var i = 0; i < count; i++)
        {
            var focus = picker.Pick(Topics)!;
            picker.Commit(focus);
            list.Add(focus);
        }
        return list;
    }

    [Fact]
    public void Every_employer_gets_its_turn_even_when_one_has_far_more_highlights()
    {
        var picks = Take(new ResumeFocusPicker(Random.Shared.NextDouble), 9);

        Assert.Equal(3, picks.Count(p => p.Topic.Employer == "Acme"));
        Assert.Equal(3, picks.Count(p => p.Topic.Employer == "Globex"));
        Assert.Equal(3, picks.Count(p => p.Topic.Employer == "Initech"));
    }

    [Fact]
    public void The_same_employer_is_not_asked_about_twice_in_a_row_when_another_is_due()
    {
        for (var seed = 0; seed < 25; seed++)
        {
            var random = new Random(seed);
            var picks = Take(new ResumeFocusPicker(random.NextDouble), 12);

            for (var i = 1; i < picks.Count; i++)
                Assert.NotEqual(picks[i - 1].Topic.Employer, picks[i].Topic.Employer);
        }
    }

    [Fact]
    public void Inside_an_employer_every_highlight_is_used_before_one_repeats()
    {
        var picks = Take(new ResumeFocusPicker(Random.Shared.NextDouble), 12);

        var acme = picks.Where(p => p.Topic.Employer == "Acme").Select(p => p.Topic.Highlight).ToList();
        Assert.Equal(4, acme.Take(4).Distinct().Count());  // the first four Acme questions are about four different things
        var globex = picks.Where(p => p.Topic.Employer == "Globex").Select(p => p.Topic.Highlight).ToList();
        Assert.Equal(2, globex.Take(2).Distinct().Count());
    }

    [Fact]
    public void Nothing_is_used_up_until_it_is_committed()
    {
        var picker = new ResumeFocusPicker(() => 0.0);

        var a = picker.Pick(Topics);
        var b = picker.Pick(Topics);

        Assert.Equal(a, b); // asked twice without a question being asked: the same answer both times
    }

    [Fact]
    public void The_first_word_changes_every_time()
    {
        var picks = Take(new ResumeFocusPicker(Random.Shared.NextDouble), 30);

        for (var i = 1; i < picks.Count; i++) Assert.NotEqual(picks[i - 1].Word, picks[i].Word);
        Assert.All(picks, p => Assert.Contains(p.Word, new[] { "Why", "How", "What", "When", "Which" }));
    }

    [Fact]
    public void A_resume_with_a_single_employer_still_rotates_its_highlights()
    {
        var picker = new ResumeFocusPicker(Random.Shared.NextDouble);
        IReadOnlyList<ResumeTopic> single = [new("Acme", "", "a"), new("Acme", "", "b"), new("Acme", "", "c")];

        var seen = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var focus = picker.Pick(single)!;
            picker.Commit(focus);
            seen.Add(focus.Topic.Highlight);
        }

        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void With_no_topics_there_is_no_focus()
    {
        Assert.Null(new ResumeFocusPicker(() => 0.0).Pick([]));
    }

    [Fact]
    public void The_focus_text_names_the_part_of_the_resume_the_word_and_forbids_the_usual_opening()
    {
        var text = new ResumeFocus(new ResumeTopic("Globex", "monolith migration", "applied feature flags during cutover"), "Why").Describe();

        Assert.Contains("Globex, monolith migration: applied feature flags during cutover", text);
        Assert.Contains("Begin the question with the word \"Why\"", text);
        Assert.Contains("do not start with \"At Globex, you\"", text);
    }

    [Fact]
    public void A_job_without_a_project_is_named_by_the_employer_alone()
    {
        Assert.Equal("Initech", new ResumeTopic("Initech", "", "x").Where);
        Assert.Equal("Globex, monolith migration", new ResumeTopic("Globex", "monolith migration", "x").Where);
    }

    // ---- reading topics out of a resume

    private static (TechBank Bank, ScriptedLlmService Llm, BankScript Script) NewBank()
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        return (new TechBank(new InMemoryTechBankRepository(), llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => 0.0), llm, script);
    }

    private static CandidateProfile Profile(string resume = "RESUME-ONE", string jd = "JD-ONE") => new()
    {
        Name = "p", JobRole = "Backend Engineer", Seniority = Seniority.Senior, JobDescription = jd, ResumeText = resume,
    };

    private static ResumeTopicEntryDto Entry(string employer, string project, params string[] highlights)
        => new() { Employer = employer, Project = project, Highlights = [.. highlights] };

    [Fact]
    public async Task The_resume_is_read_once_and_remembered_whatever_happens_to_the_job_description()
    {
        var (bank, llm, script) = NewBank();
        script.ResumeEntries = [Entry("Acme", "claims", "built the ranking service", "added Redis caching"), Entry("Globex", "", "migrated the monolith")];

        var first = await bank.GetResumeTopicsAsync(Profile());
        var again = await bank.GetResumeTopicsAsync(Profile(jd: "a completely different job description"));
        var spaced = await bank.GetResumeTopicsAsync(Profile(resume: "RESUME-ONE  \r\n"));

        Assert.Single(llm.ResumeTopicCalls);
        Assert.Equal(3, first.Count);
        Assert.Equal(first, again);
        Assert.Equal(first, spaced);
        Assert.Equal(new ResumeTopic("Acme", "claims", "built the ranking service"), first[0]);
        Assert.Contains("RESUME-ONE", llm.ResumeTopicCalls.Single().Prompt);
    }

    [Fact]
    public async Task Changing_the_resume_reads_it_again()
    {
        var (bank, llm, script) = NewBank();
        script.ResumeEntries = [Entry("Acme", "", "a")];

        await bank.GetResumeTopicsAsync(Profile(resume: "old resume"));
        await bank.GetResumeTopicsAsync(Profile(resume: "a new resume with a new job on it"));

        Assert.Equal(2, llm.ResumeTopicCalls.Count());
    }

    [Fact]
    public async Task What_the_model_returns_is_tidied_and_capped()
    {
        var (bank, _, script) = NewBank();
        script.ResumeEntries =
        [
            Entry("  Acme  ", "  claims  ", "  one  ", "ONE", "", "two", "three", "four", "five", "six"),   // trimmed, a repeat and a blank dropped, at most five kept
            Entry("", "", "work with no employer named"),                                                     // becomes Other work
            .. Enumerable.Range(1, 12).Select(i => Entry($"Client {i}", "", "something")),                  // only eight entries are kept in all
        ];

        var topics = await bank.GetResumeTopicsAsync(Profile());

        Assert.Equal(["one", "two", "three", "four", "five"], topics.Where(t => t.Employer == "Acme").Select(t => t.Highlight).ToArray());
        Assert.Equal("claims", topics[0].Project);
        Assert.Contains(topics, t => t.Employer == "Other work");
        Assert.Equal(TechBank.MaxEmployers, topics.Select(t => t.Employer).Distinct().Count());
    }

    [Fact]
    public async Task A_blank_resume_is_not_sent_to_the_model()
    {
        var (bank, llm, _) = NewBank();

        Assert.Empty(await bank.GetResumeTopicsAsync(Profile(resume: "  ")));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task A_resume_with_nothing_to_list_is_remembered_as_empty_and_not_asked_about_again()
    {
        var (bank, llm, _) = NewBank();

        await bank.GetResumeTopicsAsync(Profile());
        Assert.Empty(await bank.GetResumeTopicsAsync(Profile()));

        Assert.Single(llm.ResumeTopicCalls);
    }

    // ---- what the questions are told

    private static string FocusBlock(LlmCall call)
    {
        var start = call.Prompt.IndexOf("<resume_focus>\n", StringComparison.Ordinal) + "<resume_focus>\n".Length;
        return call.Prompt[start..call.Prompt.IndexOf("\n</resume_focus>", start, StringComparison.Ordinal)];
    }

    private sealed class Rig
    {
        public BankScript Script { get; } = new() { ModelQuestionType = "resume_deep_dive" };
        public InMemoryTechBankRepository Bank { get; } = new();
        public ScriptedLlmService Llm { get; }
        private readonly PromptLibrary _prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

        public Rig() => Llm = new ScriptedLlmService(Script.Handle);

        public LearnEngine NewEngine(bool withBank = true) => new(Llm, _prompts, withBank ? new TechBank(Bank, Llm, _prompts, () => 0.0) : null, () => 0.0) { PrefetchNext = false };
    }

    private static readonly QuestionType[] ResumeOnly = [QuestionType.ResumeDeepDive];

    [Fact]
    public async Task Resume_questions_take_turns_between_the_employers_on_the_resume()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Acme", "claims", "built the ranking service", "added Redis caching"), Entry("Globex", "migration", "applied feature flags during cutover")];
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), ResumeOnly);
        for (var i = 0; i < 3; i++) await engine.NextAsync();

        var employers = rig.Llm.ModelQuestionCalls.Select(c => FocusBlock(c)).Select(b => b.Contains("Globex") ? "Globex" : "Acme").ToList();
        Assert.Equal(4, employers.Count);
        Assert.Equal(2, employers.Count(e => e == "Acme"));
        Assert.Equal(2, employers.Count(e => e == "Globex"));
        for (var i = 1; i < employers.Count; i++) Assert.NotEqual(employers[i - 1], employers[i]);
    }

    [Fact]
    public async Task The_resume_is_read_once_per_session_not_once_per_question()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Acme", "", "a"), Entry("Globex", "", "b")];
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), ResumeOnly);
        await engine.NextAsync();
        await engine.NextAsync();
        await rig.NewEngine().StartAsync(Profile(), ResumeOnly); // a new session: the saved reading is reused

        Assert.Single(rig.Llm.ResumeTopicCalls);
    }

    [Fact]
    public async Task The_focus_says_which_part_of_the_resume_and_the_first_word()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Globex", "monolith migration", "applied feature flags during cutover")];

        await rig.NewEngine().StartAsync(Profile(), ResumeOnly);

        var block = FocusBlock(rig.Llm.ModelQuestionCalls.Single());
        Assert.Contains("Globex, monolith migration: applied feature flags during cutover", block);
        Assert.Matches("Begin the question with the word \"(Why|How|What|When|Which)\"", block);
    }

    [Fact]
    public async Task With_several_types_including_resume_questions_the_focus_is_offered_because_the_model_may_write_one()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Acme", "", "a")];

        await rig.NewEngine().StartAsync(Profile(), [QuestionType.ResumeDeepDive, QuestionType.Behavioral]);

        Assert.Single(rig.Llm.ResumeTopicCalls);
        Assert.Contains("Acme", FocusBlock(rig.Llm.ModelQuestionCalls.First()));
    }

    [Fact]
    public async Task When_resume_questions_are_not_wanted_the_resume_is_not_read_and_no_focus_is_given()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Acme", "", "a")];

        await rig.NewEngine().StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.SystemDesign]);

        Assert.Empty(rig.Llm.ResumeTopicCalls);
        Assert.Equal("(none)", FocusBlock(rig.Llm.ModelQuestionCalls.Single()));
    }

    [Fact]
    public async Task Without_the_saved_bank_there_is_no_focus_and_no_extra_call()
    {
        var rig = new Rig();
        rig.Script.ResumeEntries = [Entry("Acme", "", "a")];

        await rig.NewEngine(withBank: false).StartAsync(Profile(), ResumeOnly);

        Assert.Empty(rig.Llm.ResumeTopicCalls);
        Assert.Equal("(none)", FocusBlock(rig.Llm.ModelQuestionCalls.Single()));
    }

    [Fact]
    public async Task If_the_resume_cannot_be_read_the_question_is_still_asked_without_a_focus()
    {
        var rig = new Rig();
        rig.Script.FailResumeTopics = true;

        var engine = rig.NewEngine();
        await engine.StartAsync(Profile(), ResumeOnly);

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal("(none)", FocusBlock(rig.Llm.ModelQuestionCalls.Single()));
    }

    [Fact]
    public async Task A_part_of_the_resume_is_only_used_up_by_a_question_that_was_really_about_the_resume()
    {
        var rig = new Rig();
        rig.Script.ModelQuestionType = "behavioral"; // the model chose another type, so the focus was not used
        rig.Script.ResumeEntries = [Entry("Acme", "", "a"), Entry("Globex", "", "b")];
        var engine = rig.NewEngine();

        await engine.StartAsync(Profile(), [QuestionType.Behavioral, QuestionType.ResumeDeepDive]);
        await engine.NextAsync();

        var blocks = rig.Llm.ModelQuestionCalls.Select(FocusBlock).ToList();
        Assert.Equal(blocks[0].Contains("Acme"), blocks[1].Contains("Acme")); // the same employer is offered again
    }

    // ---- asking again when a question is too close to an earlier one

    private sealed class NearDuplicateModel
    {
        public List<LlmCall> Calls { get; } = [];
        private readonly Queue<string> _questions;
        public NearDuplicateModel(params string[] questions) => _questions = new Queue<string>(questions);

        public Task<object> Handle(LlmCall call)
        {
            Calls.Add(call);
            if (call.Role == LlmRole.QuestionGenerator)
                return Task.FromResult<object>(new QuestionDto { Question = _questions.Dequeue(), QuestionType = "behavioral", Source = "resume", Focus = "f" });
            return Task.FromResult<object>(new CoachOutput { ModelAnswer = "answer", Shape = "A → B" });
        }
    }

    private static LearnEngine EngineFor(ScriptedLlmService llm) => new(llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), null, () => 0.0) { PrefetchNext = false };

    [Fact]
    public async Task A_question_in_other_words_is_sent_back_once_with_the_reason()
    {
        var model = new NearDuplicateModel(
            "Why did you choose RabbitMQ over Azure Service Bus for specific workloads at Acme?",
            "How would you choose between RabbitMQ and Azure Service Bus for a new workload at Acme?",       // the same question again
            "What was the hardest part of the feature-flag migration at Globex?");
        var llm = new ScriptedLlmService(model.Handle);
        var engine = EngineFor(llm);
        await engine.StartAsync(Profile(), [QuestionType.Behavioral]);

        await engine.NextAsync();

        Assert.Equal("What was the hardest part of the feature-flag migration at Globex?", engine.Current!.Question);
        var retry = llm.To(LlmRole.QuestionGenerator).Last();
        Assert.Equal(3, retry.Messages.Count);
        Assert.Equal(ChatTurnRole.Assistant, retry.Messages[1].Role);
        Assert.Contains("How would you choose between RabbitMQ", retry.Messages[1].Content);
        Assert.Contains("too close to a question you already asked", retry.Messages[2].Content);
    }

    [Fact]
    public async Task A_new_question_is_not_sent_back()
    {
        var model = new NearDuplicateModel("Why did you pick polling over push?", "How did you version the React library?");
        var llm = new ScriptedLlmService(model.Handle);
        var engine = EngineFor(llm);
        await engine.StartAsync(Profile(), [QuestionType.Behavioral]);

        await engine.NextAsync();

        Assert.Equal(2, llm.To(LlmRole.QuestionGenerator).Count()); // one per question, no retries
        Assert.All(llm.To(LlmRole.QuestionGenerator), c => Assert.Single(c.Messages));
    }

    [Fact]
    public async Task If_the_model_repeats_itself_a_second_time_the_question_is_accepted_rather_than_looping()
    {
        var model = new NearDuplicateModel(
            "Why did you choose RabbitMQ over Azure Service Bus for specific workloads at Acme?",
            "Why did you choose RabbitMQ over Azure Service Bus for specific workloads at Acme?",
            "How would you choose between RabbitMQ and Azure Service Bus for a new workload at Acme?");
        var llm = new ScriptedLlmService(model.Handle);
        var engine = EngineFor(llm);
        await engine.StartAsync(Profile(), [QuestionType.Behavioral]);
        await engine.NextAsync(); // the model repeats itself once more after being told

        Assert.Equal(LearnPhase.Ready, engine.Phase);
        Assert.Equal(3, llm.To(LlmRole.QuestionGenerator).Count()); // first, then a question and its single retry
    }
}
