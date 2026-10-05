using System.IO;
using InterviewCoach.App.ViewModels;
using InterviewCoach.Core.Abstractions;
using InterviewCoach.Core.Engines;
using InterviewCoach.Core.Models;
using InterviewCoach.Core.Speech;
using InterviewCoach.Infrastructure.Fakes;
using InterviewCoach.Infrastructure.Prompts;

namespace InterviewCoach.App.Tests;

/// <summary>The recognizer is told which technologies, employers and topics to expect when the microphone starts.</summary>
public class PhraseWiringTests
{
    private sealed class MemorySettings(AppSettings s) : ISettingsStore
    {
        public AppSettings Current => s;
        public void Save(AppSettings settings) { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private static readonly PromptLibrary Prompts = new(Path.Combine(Path.GetTempPath(), "no-such-dir"));

    private static CandidateProfile Profile() => new()
    {
        Id = 1, Name = "Orders platform", JobRole = "Senior Backend Engineer", Seniority = Seniority.Senior,
        JobDescription = "We build order services in C# and .NET.",
        ResumeText = "Built the ranking service.\nSkills: Redis, Kafka, gRPC, SQL Server 2019",
    };

    private sealed class World
    {
        public FakeLlmService Llm { get; } = new();
        public TechBank Bank { get; }
        public TestSpeech Speech { get; } = new();
        public MemorySettings Settings { get; } = new(new AppSettings { AutoListen = false, SpeakQuestions = false });

        public World()
        {
            Bank = new TechBank(new InMemoryTechBankRepository(), Llm, Prompts, () => 0.0);
        }

        public PracticeViewModel Practice(bool withSource = true)
            => new(Llm, Prompts, Settings, Bank, () => 0.0, null, null, Speech, withSource ? new SpeechPhraseSource(Bank) : null);

        public MockViewModel Mock(bool withSource = true)
            => new(Llm, Prompts, Settings, Speech, null, null, null, withSource ? new SpeechPhraseSource(Bank) : null);
    }

    private static LearnSessionRequest Request(params string[] technologies)
        => new(Profile(), [QuestionType.Behavioral], null, technologies, EmploymentType.FullTime);

    // ---- Practice

    [Fact]
    public async Task Practice_tells_the_recognizer_the_technologies_the_resume_skills_the_employers_and_the_role()
    {
        var w = new World();
        await w.Bank.GetTechnologiesAsync(Profile());                       // what a session had read before: the job description's technologies
        var vm = w.Practice();

        vm.Begin(Request());
        await Task.Delay(100);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        var phrases = w.Speech.Mic.Phrases;
        Assert.Contains("C#", phrases);
        Assert.Contains("SQL Server", phrases);                             // the demo model's job description technologies, saved earlier
        Assert.Contains("Redis", phrases);
        Assert.Contains("gRPC", phrases);
        Assert.DoesNotContain(phrases, p => p.Contains("2019"));            // version numbers are noise
        Assert.Contains("Senior Backend Engineer", phrases);
    }

    [Fact]
    public async Task The_technologies_picked_for_the_session_come_first_and_the_question_s_own_technology_is_added()
    {
        var w = new World();
        var vm = w.Practice();

        vm.Begin(new LearnSessionRequest(Profile(), [QuestionType.TechnicalConcept], null, ["Kafka"], EmploymentType.FullTime));
        await Task.Delay(100);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        var phrases = w.Speech.Mic.Phrases;
        Assert.Equal("Kafka", phrases[0]);
        Assert.Equal(1, phrases.Count(p => p == "Kafka"));                  // the question is about Kafka too, and it is listed once
    }

    [Fact]
    public async Task Without_the_source_Practice_still_tells_it_the_picked_technologies_and_the_role()
    {
        var w = new World();
        var vm = w.Practice(withSource: false);

        vm.Begin(new LearnSessionRequest(Profile(), [QuestionType.TechnicalConcept], null, ["Kafka"], EmploymentType.FullTime));
        await vm.ToggleMicCommand.ExecuteAsync(null);

        Assert.Equal(["Kafka", "Senior Backend Engineer"], w.Speech.Mic.Phrases);
    }

    [Fact]
    public async Task A_new_session_does_not_keep_the_terms_of_the_last_one()
    {
        var w = new World();
        var vm = w.Practice();
        vm.Begin(Request("Kafka"));
        await Task.Delay(100);

        var other = new CandidateProfile { Id = 2, Name = "Other", JobRole = "Data Engineer", JobDescription = "x", ResumeText = "Skills: Spark" };
        vm.Begin(new LearnSessionRequest(other, [QuestionType.Behavioral], null, [], EmploymentType.FullTime));
        await Task.Delay(100);
        await vm.ToggleMicCommand.ExecuteAsync(null);

        var phrases = w.Speech.Mic.Phrases;
        Assert.Contains("Spark", phrases);
        Assert.Contains("Data Engineer", phrases);
        Assert.DoesNotContain("Kafka", phrases);
        Assert.DoesNotContain("Redis", phrases);
    }

    // ---- Mock Interview

    [Fact]
    public async Task A_mock_interview_tells_the_recognizer_the_plans_topics_as_well()
    {
        var w = new World();
        await w.Bank.GetTechnologiesAsync(Profile());
        var vm = w.Mock();

        vm.Begin(new MockSessionRequest(Profile(), RoundType.Technical, 15, true, EmploymentType.FullTime));
        await Task.Delay(100);
        await vm.Composer.ToggleMicCommand.ExecuteAsync(null);

        var phrases = w.Speech.Mic.Phrases;
        Assert.Contains("a proud project", phrases);                        // a topic from the demo plan
        Assert.Contains("debugging", phrases);
        Assert.Contains("Redis", phrases);
        Assert.Contains("SQL Server", phrases);
        Assert.Contains("Senior Backend Engineer", phrases);
        var order = phrases.ToList();
        Assert.True(order.IndexOf("a proud project") < order.IndexOf("Redis"));   // the round's own topics come before the general list
    }

    [Fact]
    public async Task A_resumed_interview_tells_the_recognizer_its_stored_plans_topics()
    {
        var w = new World();
        var history = new InMemoryMockHistory();
        var first = new MockViewModel(w.Llm, Prompts, w.Settings, w.Speech, null, history, null, new SpeechPhraseSource(w.Bank));
        var request = new MockSessionRequest(Profile(), RoundType.Technical, 15, true, EmploymentType.FullTime);
        first.Begin(request);
        first.Abandon();
        var record = (await history.ListAsync())[0];
        var vm = new MockViewModel(w.Llm, Prompts, w.Settings, w.Speech, null, history, null, new SpeechPhraseSource(w.Bank));

        vm.Resume(request, record);
        await Task.Delay(100);
        await vm.Composer.ToggleMicCommand.ExecuteAsync(null);

        Assert.Contains("a proud project", w.Speech.Mic.Phrases);
    }

    [Fact]
    public async Task Without_the_source_a_mock_interview_still_tells_it_the_role_and_the_plans_topics()
    {
        var w = new World();
        var vm = w.Mock(withSource: false);

        vm.Begin(new MockSessionRequest(Profile(), RoundType.Technical, 15, true, EmploymentType.FullTime));
        await vm.Composer.ToggleMicCommand.ExecuteAsync(null);

        Assert.Contains("Senior Backend Engineer", w.Speech.Mic.Phrases);
        Assert.Contains("debugging", w.Speech.Mic.Phrases);
        Assert.DoesNotContain("Redis", w.Speech.Mic.Phrases);
    }

    // ---- a recognizer that cannot use them

    [Fact]
    public async Task A_recognizer_that_ignores_the_terms_changes_nothing_for_the_screen()
    {
        var w = new World();
        var plain = new PlainSpeech();
        var vm = new PracticeViewModel(w.Llm, Prompts, w.Settings, w.Bank, () => 0.0, null, null, plain, new SpeechPhraseSource(w.Bank));
        vm.Begin(Request());

        await vm.ToggleMicCommand.ExecuteAsync(null);

        Assert.True(vm.IsListening);
    }

    private sealed class PlainSpeech : ISpeechFactory
    {
        public ITextToSpeech TextToSpeech { get; } = new HeldVoice();
        public SpeechReadiness TextToSpeechReadiness => SpeechReadiness.Ready;
        public SpeechReadiness SpeechToTextReadiness => SpeechReadiness.Ready;
        public ISpeechToText CreateSpeechToText() => new PlainStt();
    }

    private sealed class PlainStt : ISpeechToText
    {
        // (does not implement SetPhrases: the interface's default does nothing)
        public bool SupportsPartials => true;
        public event EventHandler<string>? PartialRecognized { add { } remove { } }
        public event EventHandler<string>? FinalRecognized { add { } remove { } }
        public event EventHandler<string>? Error { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
