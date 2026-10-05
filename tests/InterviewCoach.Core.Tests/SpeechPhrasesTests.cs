using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.Core.Tests;

/// <summary>The words a recognizer is told to expect (found in the first real voice interview: ".NET", "queue" and "state" came out wrong).</summary>
public class SpeechPhrasesTests
{
    // ---- building the list

    [Fact]
    public void Terms_are_merged_most_important_first_without_duplicates_ignoring_case()
    {
        var phrases = SpeechPhrases.Build(["Redis", "Kafka"], ["redis", "Docker", "KAFKA"], null, ["Azure"]);

        Assert.Equal(["Redis", "Kafka", "Docker", "Azure"], phrases);
    }

    [Theory]
    [InlineData(".NET", ".NET")]
    [InlineData("  C#  ", "C#")]
    [InlineData("SQL Server 2012", "SQL Server")]
    [InlineData("C# 5.0/6.0/7.0", "C#")]
    [InlineData(".NET Framework 4.5.2/4.6/4.7", ".NET Framework")]
    [InlineData("ASP.NET MVC 5", "ASP.NET MVC")]
    [InlineData("(Redis)", "Redis")]
    [InlineData("Node.js", "Node.js")]
    [InlineData("React.", "React")]
    [InlineData("Entity   Framework\t6", "Entity Framework")]
    public void A_term_is_tidied_the_way_a_person_would_say_it(string raw, string expected)
        => Assert.Equal([expected], SpeechPhrases.Build([raw]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C")]                                                      // a single letter says nothing
    [InlineData("2012")]                                                   // no letters
    [InlineData("4.5.2/4.6/4.7")]
    [InlineData("a b c d e")]                                              // five words is a sentence, not a term
    [InlineData("a name that goes on and on past the forty character limit")]
    public void Things_that_are_not_short_terms_are_left_out(string raw)
        => Assert.Empty(SpeechPhrases.Build([raw]));

    [Fact]
    public void Nothing_in_gives_nothing_out_and_null_lists_are_skipped()
    {
        Assert.Empty(SpeechPhrases.Build());
        Assert.Empty(SpeechPhrases.Build(null, null));
        Assert.Equal(["Redis"], SpeechPhrases.Build(null, [null, "Redis", ""]));
    }

    [Fact]
    public void The_list_is_capped_and_earlier_terms_win()
    {
        var many = Enumerable.Range(1, 250).Select(i => $"Term{i}");

        var phrases = SpeechPhrases.Build(["First"], many);

        Assert.Equal(SpeechPhrases.MaxPhrases, phrases.Count);
        Assert.Equal("First", phrases[0]);
        Assert.Equal("Term1", phrases[1]);
    }

    // ---- the resume's own skills lines

    [Fact]
    public void Skills_lines_of_a_resume_give_their_terms_and_other_lines_give_none()
    {
        var resume = """
            Jane Doe - Senior Software Engineer
            Experience
            - Built the ranking service in C# on .NET 8, cutting p99 from 300ms to 80ms
            Skills: C#, .NET, SQL Server 2019, Redis; RabbitMQ | Docker
            - Technologies: Azure, React
            Environment: .NET Framework 4.5.2/4.6/4.7, ASP.NET MVC 5, Web API 2, SQL Server 2012
            Education
            University of Maryland
            """;

        var terms = SpeechPhrases.FromResume(resume);

        Assert.Equal(["C#", ".NET", "SQL Server 2019", "Redis", "RabbitMQ", "Docker", "Azure", "React", ".NET Framework 4.5.2/4.6/4.7", "ASP.NET MVC 5", "Web API 2", "SQL Server 2012"], terms);
        var phrases = SpeechPhrases.Build(terms);
        Assert.Contains("SQL Server", phrases);
        Assert.Contains(".NET Framework", phrases);
        Assert.DoesNotContain(phrases, p => p.Contains("2012"));
        Assert.Equal(phrases.Count, phrases.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("skills: Go, Rust")]
    [InlineData("SKILLS : Go, Rust")]
    [InlineData("  • Skills: Go, Rust")]
    [InlineData("Technical Skills: Go, Rust")]
    [InlineData("Languages: Go, Rust")]
    public void Skills_lines_are_found_whatever_their_label_case_or_bullet(string line)
        => Assert.Equal(["Go", "Rust"], SpeechPhrases.FromResume("Name\n" + line + "\nMore text"));

    [Fact]
    public void A_missing_resume_has_no_terms()
    {
        Assert.Empty(SpeechPhrases.FromResume(null));
        Assert.Empty(SpeechPhrases.FromResume("   "));
        Assert.Empty(SpeechPhrases.FromResume("I like skills: but not in a list"));   // the label must start the line
    }

    // ---- what a session has to hand

    [Fact]
    public void A_session_gets_the_picked_technologies_first_then_the_job_description_the_resume_the_employers_and_the_role()
    {
        var profile = new CandidateProfile { JobRole = "Backend Engineer", ResumeText = "Skills: Redis, Docker" };
        var topics = new[] { new ResumeTopic("Acme Retail", "Order emails", "Redesigned the pipeline") };

        var phrases = SpeechPhrases.ForProfile(profile, ["Kafka"], ["C#", "Redis"], topics);

        Assert.Equal(["Kafka", "C#", "Redis", "Docker", "Acme Retail", "Order emails", "Backend Engineer"], phrases);
    }

    [Fact]
    public void A_session_with_nothing_saved_still_has_the_role_and_the_resume_skills()
    {
        var profile = new CandidateProfile { JobRole = "Data Engineer", ResumeText = "Skills: Spark" };

        Assert.Equal(["Spark", "Data Engineer"], SpeechPhrases.ForProfile(profile, null, null, null));
    }

    // ---- Whisper takes a prompt, not a list

    [Fact]
    public void The_prompt_for_Whisper_is_a_short_sentence_and_the_terms()
    {
        var prompt = SpeechPhrases.AsPrompt([".NET", "gRPC", "Redis"]);

        Assert.Equal("A technical job interview. Terms that may be spoken: .NET, gRPC, Redis.", prompt);
    }

    [Fact]
    public void The_prompt_is_cut_at_a_term_never_in_the_middle_of_one()
    {
        var terms = Enumerable.Range(1, 200).Select(i => $"Technology{i}").ToList();

        var prompt = SpeechPhrases.AsPrompt(terms, maxChars: 200);

        Assert.True(prompt.Length <= 200);
        Assert.EndsWith(".", prompt);
        var listed = prompt["A technical job interview. Terms that may be spoken: ".Length..^1].Split(", ");
        Assert.All(listed, t => Assert.Matches(@"^Technology\d+$", t));
        Assert.Equal(terms.Take(listed.Length), listed);
    }

    [Fact]
    public void With_no_terms_or_no_room_there_is_no_prompt()
    {
        Assert.Equal("", SpeechPhrases.AsPrompt([]));
        Assert.Equal("", SpeechPhrases.AsPrompt(["Redis"], maxChars: 10));
    }

    // ---- reading what was saved, never asking the model

    private static (TechBank Bank, ScriptedLlmService Llm, BankScript Script) Create()
    {
        var script = new BankScript();
        var llm = new ScriptedLlmService(script.Handle);
        var bank = new TechBank(new InMemoryTechBankRepository(), llm, new PromptLibrary(Path.Combine(Path.GetTempPath(), "no-such-dir")), () => 0.0);
        return (bank, llm, script);
    }

    private static CandidateProfile Profile() => new()
    {
        Name = "p", JobRole = "Backend Engineer", JobDescription = "We use C# and SQL Server.", ResumeText = "Skills: Redis\nWorked at Acme.",
    };

    [Fact]
    public async Task Reading_the_saved_technologies_and_employers_never_calls_the_model_and_is_empty_until_they_were_read()
    {
        var (bank, llm, script) = Create();

        Assert.Empty(await bank.GetSavedTechnologiesAsync(Profile()));
        Assert.Empty(await bank.GetSavedResumeTopicsAsync(Profile()));
        Assert.Empty(llm.Calls);

        script.ResumeEntries = [new ResumeTopicEntryDto { Employer = "Acme", Project = "Ranking", Highlights = ["Cut p99"] }];
        await bank.GetTechnologiesAsync(Profile());                           // read once, as a session does
        await bank.GetResumeTopicsAsync(Profile());
        var calls = llm.Calls.Count;

        Assert.Equal(["C#", "SQL Server"], await bank.GetSavedTechnologiesAsync(Profile()));
        var topics = await bank.GetSavedResumeTopicsAsync(Profile());
        Assert.Equal("Acme", Assert.Single(topics).Employer);
        Assert.Equal(calls, llm.Calls.Count);                                 // reading them again asked nothing
    }

    [Fact]
    public async Task A_profile_without_a_job_description_or_resume_has_nothing_saved_to_read()
    {
        var (bank, llm, _) = Create();
        var empty = new CandidateProfile { JobRole = "Dev" };

        Assert.Empty(await bank.GetSavedTechnologiesAsync(empty));
        Assert.Empty(await bank.GetSavedResumeTopicsAsync(empty));
        Assert.Empty(llm.Calls);
    }

    [Fact]
    public async Task The_source_gives_a_session_its_terms_from_what_was_saved_and_the_resume_without_a_model_call()
    {
        var (bank, llm, script) = Create();
        script.ResumeEntries = [new ResumeTopicEntryDto { Employer = "Acme", Highlights = ["Cut p99"] }];
        await bank.GetTechnologiesAsync(Profile());
        await bank.GetResumeTopicsAsync(Profile());
        var calls = llm.Calls.Count;

        var phrases = await new SpeechPhraseSource(bank).ForProfileAsync(Profile(), ["Kafka"]);

        Assert.Equal(["Kafka", "C#", "SQL Server", "Redis", "Acme", "Backend Engineer"], phrases);
        Assert.Equal(calls, llm.Calls.Count);
    }

    [Fact]
    public async Task Without_a_bank_the_source_still_gives_the_resume_skills_and_the_role()
    {
        var phrases = await new SpeechPhraseSource(null).ForProfileAsync(Profile(), ["Kafka"]);

        Assert.Equal(["Kafka", "Redis", "Backend Engineer"], phrases);
    }
}
